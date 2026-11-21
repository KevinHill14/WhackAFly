"""Pick the input rates and the strength normalisation for each subnetwork,
and save them next to it as subnet_tN.calib.json.

Vision (unchanged rule): the highest looming-detector rate (from a grid) at
which, under one-eye full looming, each Giant Fiber averages <= GF_MAX spikes
per 50 ms window (bursts, not nonstop firing) and no escape DN exceeds DN_MAX.
full_drive is the steady-state escape drive at full looming, averaged over
left and right; strength = drive / full_drive. Then the stronger eye is turned
down to match the weaker one.

Sound (same units, so sound and looming strengths are comparable): the JO
rate at which one-ear full sound from the STRONGER ear gives the same drive
as one-eye full looming (strength ~1), within the same GF/DN burst limits and
SOUND_RATE_MAX. Then the WEAKER ear's input gain is raised (up to
MAX_EAR_GAIN) until it matches. In FlyWire v783 the left ear is far weaker
(see README), so its gain ends up large; that is reported, not hidden.

By default the saved vision calibration is kept and only sound is
(re)calibrated, so the looming curve can't move; --vision redoes vision too.

Run:  python brain/calibrate.py               # all subnetworks
      python brain/calibrate.py brain/subnet/subnet_t10.npz
      python brain/calibrate.py --vision      # also redo the looming calibration
"""
import json
import sys
from pathlib import Path

import numpy as np

from brain import SUBNET_DIR, FlyBrain, calibration_path, load_calibration

RATES = list(range(10, 61, 5))
GF_MAX = 3.0
DN_MAX = 8.0
WARMUP, MEASURE = 6, 30   # decision windows
SOUND_RATE_MAX = 400.0    # Hz, stronger ear
MAX_EAR_GAIN = 30.0       # weaker ear's rate may be up to this x the stronger's


def measure(path, rate, seed=0, eye_gain=None):
    b = FlyBrain(path, max_rate_hz=rate, seed=seed)
    # Explicit gain: never inherit the one saved by a previous calibration run
    # (that made re-runs measure something different from the first run).
    b.eye_gain = dict(eye_gain or {"left": 1.0, "right": 1.0})
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


def measure_sound(path, rate, ear_gain, ears=("left", "right"), seed=0):
    """One-ear full sound, no looming: drive, spike means, jump side."""
    b = FlyBrain(path, seed=seed)
    b.sound_rate_hz = rate
    b.ear_gain = dict(ear_gain)
    names = b.group_names
    gf = [names.index("DNp01_L"), names.index("DNp01_R")]
    dns = [i for i, n in enumerate(names) if n.startswith("DNp")]
    out = {}
    for ear in ears:
        b.reset()
        for _ in range(WARMUP):
            b.decide(0.0, "center", 1.0, ear)
        sp, ema, away = [], [], []
        for _ in range(MEASURE):
            r = b.decide(0.0, "center", 1.0, ear)
            sp.append(r["spikes"])
            ema.append(b.drive_ema)
            away.append(r["side"] != ear)
        sp = np.array(sp)
        out[ear] = dict(gf=sp[:, gf].mean(0), dn=sp[:, dns].mean(0), drive=float(np.mean(ema)),
                        away=float(np.mean(away)))
    return out


def within_limits(m):
    return m["gf"].max() <= GF_MAX and m["dn"].max() <= DN_MAX


def calibrate_vision(path):
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
    return {"max_rate_hz": chosen, "full_drive": round(full_drive, 3),
            "eye_gain": eye_gain,
            "gf_spikes_per_window": {s: result[s]["gf"].round(2).tolist() for s in result},
            "rule": f"max rate with GF <= {GF_MAX} and DN <= {DN_MAX} spikes/50ms at full looming"}


def calibrate_sound(path, full_drive):
    one = {"left": 1.0, "right": 1.0}
    probe = measure_sound(path, 100.0, one)
    strong = "right" if probe["right"]["drive"] >= probe["left"]["drive"] else "left"
    weak = "left" if strong == "right" else "right"
    print(f"  sound probe 100 Hz: drive L {probe['left']['drive']:.2f} "
          f"R {probe['right']['drive']:.2f} -> stronger ear: {strong}")

    # Stronger ear: highest rate with drive <= full_drive and within burst limits.
    def ok_rate(r):
        m = measure_sound(path, r, one, ears=(strong,))[strong]
        return m["drive"] <= full_drive and within_limits(m), m
    if ok_rate(SOUND_RATE_MAX)[0]:
        rate = SOUND_RATE_MAX
    else:
        lo, hi = 10.0, SOUND_RATE_MAX
        for _ in range(9):
            mid = (lo + hi) / 2
            lo, hi = (mid, hi) if ok_rate(mid)[0] else (lo, mid)
        rate = round(lo, 1)
    m_strong = measure_sound(path, rate, one, ears=(strong,))[strong]

    # Weaker ear: raise its gain until its drive matches the stronger ear.
    target = m_strong["drive"]

    def ok_gain(gn):
        m = measure_sound(path, rate, {strong: 1.0, weak: gn}, ears=(weak,))[weak]
        return m["drive"] <= target and within_limits(m)
    if ok_gain(MAX_EAR_GAIN):
        gain = MAX_EAR_GAIN
    else:
        lo, hi = 1.0, MAX_EAR_GAIN
        for _ in range(9):
            mid = (lo + hi) / 2
            lo, hi = (mid, hi) if ok_gain(mid) else (lo, mid)
        gain = round(lo, 2)
    ear_gain = {strong: 1.0, weak: gain}
    res = measure_sound(path, rate, ear_gain)
    s = {e: res[e]["drive"] / full_drive for e in res}
    reached = abs(res[weak]["drive"] - target) <= 0.15 * max(target, 1e-9)
    print(f"  sound rate {rate} Hz ({strong} ear), {weak} ear gain x{gain} "
          f"(= {rate * gain:.0f} Hz){'' if reached else '  (cap hit: weaker ear stays weaker)'}")
    for e in res:
        print(f"    {e:5s} ear full sound: strength ~{min(1, s[e]):.2f} (drive {res[e]['drive']:.2f}"
              f" vs looming {full_drive:.2f}), GF {res[e]['gf'].round(1).tolist()}, "
              f"DN peak {res[e]['dn'].max():.1f}, jumps away {res[e]['away'] * 100:.0f}%")
    return {"sound_rate_hz": rate, "ear_gain": ear_gain,
            "sound_strength_full": {e: round(min(1.0, s[e]), 3) for e in s},
            "sound_gf_spikes_per_window": {e: res[e]["gf"].round(2).tolist() for e in res},
            "sound_jump_away": {e: round(res[e]["away"], 3) for e in res},
            "sound_ears_matched": bool(reached),
            "sound_rule": (f"stronger ear: max rate (<= {SOUND_RATE_MAX:.0f} Hz) with one-ear "
                           f"full-sound drive <= one-eye full-looming drive and GF <= {GF_MAX}, "
                           f"DN <= {DN_MAX}; weaker ear gain (<= {MAX_EAR_GAIN:.0f}) to match")}


VISION_KEYS = ("max_rate_hz", "full_drive", "eye_gain", "gf_spikes_per_window", "rule")


def calibrate(path, redo_vision=False):
    saved = load_calibration(path)
    if not redo_vision and all(k in saved for k in VISION_KEYS):
        # Keep the looming calibration the game already uses (so adding a
        # sense can't shift the looming curve); --vision redoes it.
        calib = {k: saved[k] for k in VISION_KEYS}
        print(f"  vision: keeping saved calibration (max_rate {calib['max_rate_hz']} Hz, "
              f"full_drive {calib['full_drive']}, eye_gain {calib['eye_gain']})")
    else:
        calib = calibrate_vision(path)
    if FlyBrain(path).has_sound:
        calib.update(calibrate_sound(path, calib["full_drive"]))
    calibration_path(path).write_text(json.dumps(calib, indent=2))
    print(f"  -> {calibration_path(path).name}: max_rate {calib['max_rate_hz']} Hz, "
          f"full_drive {calib['full_drive']:.2f}"
          + (f", sound {calib['sound_rate_hz']} Hz, ear gain {calib['ear_gain']}"
             if "sound_rate_hz" in calib else ""))


def main():
    args = [a for a in sys.argv[1:] if a != "--vision"]
    paths = [Path(p) for p in args] or sorted(SUBNET_DIR.glob("subnet_t*.npz"))
    for p in paths:
        print(p.name)
        calibrate(p, redo_vision="--vision" in sys.argv[1:])


if __name__ == "__main__":
    main()
