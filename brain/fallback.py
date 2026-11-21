"""Fallback fly brain: answers from a lookup table built by sweeping the live
brain (build_lookup.py). Same decide() interface and output as FlyBrain.
Standard library only, so it works even where NumPy doesn't.

Per decision: interpolate the table at the looming value, pass strength
through the same 100 ms readout filter as the live brain, pick the jump side
from the measured left/right probabilities, and Poisson-sample spike counts
around the measured means so the neuron panel still flickers.
"""
import bisect
import json
import math
import random
from pathlib import Path

DEFAULT_TABLE = Path(__file__).resolve().parent / "subnet" / "lookup.json"
EMA_ALPHA = 1.0 - math.exp(-50.0 / 100.0)   # same 100 ms filter as brain.py


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


class FallbackBrain:
    def __init__(self, path=None, seed=None):
        self.path = Path(path) if path else DEFAULT_TABLE
        with open(self.path) as f:
            t = json.load(f)
        self.table = t
        self.levels = t["levels"]
        self.group_names = t["group_names"]
        self.n_groups = len(self.group_names)
        self.rng = random.Random(seed)
        self.reset()

    def reset(self):
        self.strength = 0.0
        self.jump = "left" if self.rng.random() < 0.5 else "right"

    def sync(self, strength, side):
        """Carry over the live brain's last output so a switch is seamless."""
        self.strength = float(strength)
        self.jump = side

    def _interp(self, xs, v):
        lv = self.levels
        i = bisect.bisect_right(lv, v) - 1
        i = max(0, min(len(lv) - 2, i))
        f = (v - lv[i]) / (lv[i + 1] - lv[i])
        f = max(0.0, min(1.0, f))
        a, b = xs[i], xs[i + 1]
        if isinstance(a, list):
            return [x + f * (y - x) for x, y in zip(a, b)]
        return a + f * (b - a)

    def decide(self, value, side="center"):
        v = min(1.0, max(0.0, float(value)))
        side = side if side in ("left", "right", "center") else "center"
        row = self.table["sides"][side]
        target = self._interp(row["strength"], v)
        self.strength += EMA_ALPHA * (target - self.strength)
        spikes = [_poisson(self.rng, lam) for lam in self._interp(row["spikes"], v)]
        # Like the live brain: re-decide only as often as its lateral escape
        # neurons actually voted at this level; otherwise keep the last side.
        # (Use the nearest level, not interpolation: p_right is undefined
        # where nothing voted.)
        i = min(range(len(self.levels)), key=lambda k: abs(self.levels[k] - v))
        if self.rng.random() < row["p_vote"][i]:
            self.jump = "right" if self.rng.random() < row["p_right"][i] else "left"
        return {"type": "escape", "strength": round(self.strength, 3),
                "side": self.jump, "spikes": spikes}
