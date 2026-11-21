"""Build the fallback lookup table from a sweep of the live brain.

Three parts, each a steady-state measurement per input: mean escape strength,
probability of jumping right (when the brain voted), how often it voted, and
mean spike count per neuron-panel group.

  sides        looming only, per eye side, levels 0..1 in 0.05 steps
  sound_sides  sound only, per ear side, levels 0..1 in 0.05 steps
  mix          looming x sound together, for every (eye side, ear side) pair,
               on an 11 x 11 grid (0..1 in 0.1 steps)

Saved as JSON so the fallback needs only the stdlib.

Run:  python brain/build_lookup.py [--subnet brain/subnet/subnet_t5.npz]
"""
import argparse
import json
import time

import numpy as np

from brain import SUBNET_DIR, FlyBrain

SIDES = ("left", "right", "center")
LEVELS = [round(i * 0.05, 2) for i in range(21)]
MIX_LEVELS = [round(i * 0.1, 1) for i in range(11)]
WARMUP, REPS, MIX_REPS = 6, 40, 20


def point(b, look, lside, snd, sside, reps):
    b.reset()
    for _ in range(WARMUP):
        b.decide(look, lside, snd, sside)
    rs = [b.decide(look, lside, snd, sside) for _ in range(reps)]
    strength = round(float(np.mean([r["strength"] for r in rs])), 4)
    # Only count windows where the brain actually voted; else 0.5.
    votes = [r for r in rs if r["vote"][0] != r["vote"][1]]
    pr = np.mean([r["side"] == "right" for r in votes]) if votes else 0.5
    spikes = np.mean([r["spikes"] for r in rs], axis=0)
    return strength, round(float(pr), 3), round(len(votes) / reps, 3), spikes


def row(b, sense, side):
    out = {"strength": [], "p_right": [], "p_vote": [], "spikes": []}
    for v in LEVELS:
        args = (v, side, 0.0, "center") if sense == "looming" else (0.0, "center", v, side)
        s, pr, pv, sp = point(b, *args, REPS)
        out["strength"].append(s)
        out["p_right"].append(pr)
        out["p_vote"].append(pv)
        out["spikes"].append(sp.round(3).tolist())
    print(f"  {sense:7s} {side:6s} strength " + " ".join(f"{s:.2f}" for s in out["strength"][::2]))
    print(f"  {sense:7s} {side:6s} p(right) " + " ".join(f"{p:.2f}" for p in out["p_right"][::2]))
    return out


def build(path, seed=0, mix=True):
    b = FlyBrain(path, seed=seed)
    table = {"subnet": b.path.name, "levels": LEVELS, "group_names": b.group_names,
             "max_rate_hz": b.max_rate_hz, "sound_rate_hz": b.sound_rate_hz,
             "built": time.strftime("%Y-%m-%d %H:%M"), "sides": {}}
    for side in SIDES:
        table["sides"][side] = row(b, "looming", side)
    if not b.has_sound:
        return table
    table["sound_sides"] = {side: row(b, "sound", side) for side in SIDES}
    if not mix:
        return table
    table["mix_levels"] = MIX_LEVELS
    table["mix"] = {}
    for ls in SIDES:
        for ss in SIDES:
            g = {"strength": [], "p_right": [], "p_vote": [], "spikes": []}
            for lv in MIX_LEVELS:
                cells = [point(b, lv, ls, sv, ss, MIX_REPS) for sv in MIX_LEVELS]
                g["strength"].append([c[0] for c in cells])
                g["p_right"].append([c[1] for c in cells])
                g["p_vote"].append([c[2] for c in cells])
                g["spikes"].append([c[3].round(2).tolist() for c in cells])
            table["mix"][f"{ls}|{ss}"] = g
            print(f"  mix looming {ls:6s} + sound {ss:6s}: strength at (0.5,0.5) "
                  f"{g['strength'][5][5]:.2f}, (1,1) {g['strength'][10][10]:.2f}")
    return table


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--subnet", default=str(SUBNET_DIR / "subnet_t5.npz"))
    ap.add_argument("--out", default=None)
    ap.add_argument("--no-mix", action="store_true", help="skip the looming x sound grid")
    a = ap.parse_args()
    t0 = time.perf_counter()
    table = build(a.subnet, mix=not a.no_mix)
    out = a.out or str(SUBNET_DIR / "lookup.json")
    with open(out, "w") as f:
        json.dump(table, f, separators=(",", ":"))
    print(f"-> {out} ({time.perf_counter() - t0:.0f}s)")


if __name__ == "__main__":
    main()
