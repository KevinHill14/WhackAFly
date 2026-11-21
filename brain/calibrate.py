"""Pick the looming-detector max rate and the strength normalisation for each
subnetwork, and save them next to it as subnet_tN.calib.json.

Rule: the highest input rate (from a grid) at which, under one-eye full
looming, each Giant Fiber averages <= GF_MAX spikes per 50 ms window (bursts,
not nonstop firing) and no escape DN exceeds DN_MAX. full_drive is then the
steady-state escape drive at full looming, averaged over left and right.

Run:  python brain/calibrate.py               # all subnetworks
      python brain/calibrate.py brain/subnet/subnet_t10.npz
"""
import json
import sys
from pathlib import Path

import numpy as np

from brain import SUBNET_DIR, FlyBrain, calibration_path

RATES = list(range(10, 61, 5))
GF_MAX = 3.0
DN_MAX = 8.0
WARMUP, MEASURE = 6, 30   # decision windows


def measure(path, rate, seed=0, eye_gain=None):
    b = FlyBrain(path, max_rate_hz=rate, seed=seed)
    if eye_gain:
        b.eye_gain = dict(eye_gain)
    names = b.group_names
    gf = [names.index("DNp01_L"), names.index("DNp01_R")]
    dns = [i for i, n in enumerate(names) if n.startswith("DNp")]
    out = {}
    for side in ("left", "right"):
        b.reset()
        for _ in range(WARMUP):
            b.decide(1.0, side)
        sp, ema = [], []
        for _ in range(MEASURE):
            r = b.decide(1.0, side)
            sp.append(r["spikes"])
            ema.append(b.drive_ema)
        sp = np.array(sp)
        out[side] = dict(gf=sp[:, gf].mean(0), dn=sp[:, dns].mean(0), drive=np.mean(ema))
    return out


def calibrate(path):
    chosen, result = RATES[0], None
    for rate in RATES:
        m = measure(path, rate)
        gf_peak = max(m[s]["gf"].max() for s in m)
        dn_peak = max(m[s]["dn"].max() for s in m)
        ok = gf_peak <= GF_MAX and dn_peak <= DN_MAX
        print(f"  rate {rate:2d} Hz: GF peak {gf_peak:.1f}/window, DN peak {dn_peak:.1f}"
              f"{'' if ok else '  (too strong)'}")
        if not ok:
            break
        chosen, result = rate, m
    if result is None:
        result = measure(path, chosen)

    # Equalise the eyes: turn down the input gain of the stronger eye until
    # its full-looming drive matches the weaker one (bisection).
    dl, dr = result["left"]["drive"], result["right"]["drive"]
    strong, target = ("right", dl) if dr > dl else ("left", dr)
    lo, hi = 0.2, 1.0
    for _ in range(8):
        mid = (lo + hi) / 2
        gain = {"left": 1.0, "right": 1.0, strong: mid}
        d = measure(path, chosen, eye_gain=gain)[strong]["drive"]
        lo, hi = (lo, mid) if d > target else (mid, hi)
    eye_gain = {"left": 1.0, "right": 1.0, strong: round((lo + hi) / 2, 3)}
    result = measure(path, chosen, eye_gain=eye_gain)
    print(f"  eye drive before: L {dl:.2f} R {dr:.2f}; gain {eye_gain} -> "
          f"L {result['left']['drive']:.2f} R {result['right']['drive']:.2f}")

    full_drive = float(np.mean([result[s]["drive"] for s in result]))
    calib = {"max_rate_hz": chosen, "full_drive": round(full_drive, 3),
             "eye_gain": eye_gain,
             "gf_spikes_per_window": {s: result[s]["gf"].round(2).tolist() for s in result},
             "rule": f"max rate with GF <= {GF_MAX} and DN <= {DN_MAX} spikes/50ms at full looming"}
    calibration_path(path).write_text(json.dumps(calib, indent=2))
    print(f"  -> {calibration_path(path).name}: max_rate {chosen} Hz, full_drive {full_drive:.2f}")


def main():
    paths = [Path(p) for p in sys.argv[1:]] or sorted(SUBNET_DIR.glob("subnet_t*.npz"))
    for p in paths:
        print(p.name)
        calibrate(p)


if __name__ == "__main__":
    main()
