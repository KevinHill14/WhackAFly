"""Build the fallback lookup table from a sweep of the live brain.

For each side and looming level (0..1 in 0.05 steps): steady-state mean
escape strength, probability of jumping right, and mean spike count per
neuron-panel group. Saved as JSON so the fallback needs only the stdlib.

Run:  python brain/build_lookup.py [--subnet brain/subnet/subnet_t5.npz]
"""
import argparse
import json
import time

import numpy as np

from brain import SUBNET_DIR, FlyBrain

LEVELS = [round(i * 0.05, 2) for i in range(21)]
WARMUP, REPS = 6, 40


def build(path, seed=0):
    b = FlyBrain(path, seed=seed)
    table = {"subnet": b.path.name, "levels": LEVELS, "group_names": b.group_names,
             "max_rate_hz": b.max_rate_hz, "built": time.strftime("%Y-%m-%d %H:%M"),
             "sides": {}}
    for side in ("left", "right", "center"):
        strength, p_right, p_vote, spikes = [], [], [], []
        for v in LEVELS:
            b.reset()
            for _ in range(WARMUP):
                b.decide(v, side)
            rs = [b.decide(v, side) for _ in range(REPS)]
            strength.append(round(float(np.mean([r["strength"] for r in rs])), 4))
            # Only count windows where the brain actually voted; else 0.5.
            votes = [r for r in rs if r["vote"][0] != r["vote"][1]]
            pr = np.mean([r["side"] == "right" for r in votes]) if votes else 0.5
            p_right.append(round(float(pr), 3))
            p_vote.append(round(len(votes) / REPS, 3))
            spikes.append(np.mean([r["spikes"] for r in rs], axis=0).round(3).tolist())
        table["sides"][side] = {"strength": strength, "p_right": p_right,
                                "p_vote": p_vote, "spikes": spikes}
        print(f"  {side:6s} strength " + " ".join(f"{s:.2f}" for s in strength[::2]))
        print(f"  {side:6s} p(right) " + " ".join(f"{p:.2f}" for p in p_right[::2]))
    return table


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--subnet", default=str(SUBNET_DIR / "subnet_t5.npz"))
    ap.add_argument("--out", default=None)
    a = ap.parse_args()
    t0 = time.perf_counter()
    table = build(a.subnet)
    out = a.out or str(SUBNET_DIR / "lookup.json")
    with open(out, "w") as f:
        json.dump(table, f, separators=(",", ":"))
    print(f"-> {out} ({time.perf_counter() - t0:.0f}s)")


if __name__ == "__main__":
    main()
