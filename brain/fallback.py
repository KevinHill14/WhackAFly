"""Fallback fly brain: answers from a lookup table built by sweeping the live
brain (build_lookup.py). Same decide() interface and output as FlyBrain.
Standard library only, so it works even where NumPy doesn't.

Per decision: look the input up in the table (looming only -> the looming
rows, sound only -> the sound rows, both -> the looming x sound grid,
interpolated), pass strength through the same 100 ms readout filter as the
live brain, pick the jump side from the measured left/right probabilities,
and Poisson-sample spike counts around the measured means so the neuron panel
still flickers.
"""
import bisect
import json
import math
import random
from pathlib import Path

DEFAULT_TABLE = Path(__file__).resolve().parent / "subnet" / "lookup.json"
EMA_ALPHA = 1.0 - math.exp(-50.0 / 100.0)   # same 100 ms filter as brain.py
SIDES = ("left", "right", "center")


def _poisson(rng, lam):
    if lam <= 0:
        return 0
    if lam > 30:                               # normal approx for big counts
        return max(0, int(round(rng.gauss(lam, math.sqrt(lam)))))
    l, k, p = math.exp(-lam), 0, 1.0
    while True:
        p *= rng.random()
        if p <= l:
            return k
        k += 1


def _bracket(levels, v):
    """Index i and fraction f with v = levels[i] + f * (levels[i+1] - levels[i])."""
    i = bisect.bisect_right(levels, v) - 1
    i = max(0, min(len(levels) - 2, i))
    f = (v - levels[i]) / (levels[i + 1] - levels[i])
    return i, max(0.0, min(1.0, f))


def _lerp(a, b, f):
    if isinstance(a, list):
        return [x + f * (y - x) for x, y in zip(a, b)]
    return a + f * (b - a)


def _nearest(levels, v):
    return min(range(len(levels)), key=lambda k: abs(levels[k] - v))


class FallbackBrain:
    def __init__(self, path=None, seed=None):
        self.path = Path(path) if path else DEFAULT_TABLE
        with open(self.path) as f:
            t = json.load(f)
        self.table = t
        self.levels = t["levels"]
        self.group_names = t["group_names"]
        self.n_groups = len(self.group_names)
        self.has_sound = "sound_sides" in t
        self.mix_levels = t.get("mix_levels")
        self.rng = random.Random(seed)
        self.reset()

    def reset(self):
        self.strength = 0.0
        self.jump = "left" if self.rng.random() < 0.5 else "right"

    def sync(self, strength, side):
        """Carry over the live brain's last output so a switch is seamless."""
        self.strength = float(strength)
        self.jump = side

    def _row(self, row, v):
        """1-D table row at value v: (strength, spikes, p_vote, p_right)."""
        i, f = _bracket(self.levels, v)
        # p_right is undefined where nothing voted: use the nearest level.
        k = _nearest(self.levels, v)
        return (_lerp(row["strength"][i], row["strength"][i + 1], f),
                _lerp(row["spikes"][i], row["spikes"][i + 1], f),
                row["p_vote"][k], row["p_right"][k])

    def _grid(self, g, lv, sv):
        """Looming x sound grid, bilinear (strength, spikes), nearest (side)."""
        lev = self.mix_levels
        i, fi = _bracket(lev, lv)
        j, fj = _bracket(lev, sv)

        def at(key):
            a = _lerp(g[key][i][j], g[key][i][j + 1], fj)
            b = _lerp(g[key][i + 1][j], g[key][i + 1][j + 1], fj)
            return _lerp(a, b, fi)
        ki, kj = _nearest(lev, lv), _nearest(lev, sv)
        return at("strength"), at("spikes"), g["p_vote"][ki][kj], g["p_right"][ki][kj]

    def decide(self, value, side="center", sound=0.0, sound_side="center"):
        v = min(1.0, max(0.0, float(value)))
        s = min(1.0, max(0.0, float(sound))) if self.has_sound else 0.0
        side = side if side in SIDES else "center"
        sound_side = sound_side if sound_side in SIDES else "center"
        t = self.table
        if s <= 0:
            target, lam, p_vote, p_right = self._row(t["sides"][side], v)
        elif v <= 0 or "mix" not in t:
            target, lam, p_vote, p_right = self._row(t["sound_sides"][sound_side], s)
        else:
            target, lam, p_vote, p_right = self._grid(t["mix"][f"{side}|{sound_side}"], v, s)
        self.strength += EMA_ALPHA * (target - self.strength)
        spikes = [_poisson(self.rng, x) for x in lam]
        # Like the live brain: re-decide only as often as its lateral escape
        # neurons actually voted here; otherwise keep the last side.
        if self.rng.random() < p_vote:
            self.jump = "right" if self.rng.random() < p_right else "left"
        return {"type": "escape", "strength": round(self.strength, 3),
                "side": self.jump, "spikes": spikes}
