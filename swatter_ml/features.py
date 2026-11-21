"""Shared preprocessing for the swatter swing classifier. Standard library only.

Recording, training and on-device inference all go through this file, so the model always
sees exactly the input it was trained on. Change a constant here and you must retrain.

Input stream (from the ESP32, see firmware/src/main.cpp and backend/gesture.py):
    {"t": millis, "ax","ay","az": raw acceleration in m/s^2 (gravity included),
     "gx","gy","gz": rotation in rad/s (start-up bias removed)}   about 200 per second

Pipeline, identical offline (training) and online (inference):
  1. Resample to exactly 200 Hz (5 ms) by linear interpolation on the device timestamp `t`.
     A gap over 100 ms, or time going backwards (device reset), starts a fresh stream.
  2. Motion energy per sample: |gyro| (rad/s) + 0.3 * | |accel| - g | (m/s^2).
  3. Window: 80 samples (0.40 s) around the energy peak, the peak at index 52
     (52 samples = 260 ms before it, 27 = 135 ms after). Missing samples at the edges are
     filled with the nearest real sample.
       - Training: the peak is the highest-energy sample of the recorded take.
       - Online: energy rising over TRIGGER_ENERGY starts a search; the peak is the highest
         energy in the next PEAK_SEARCH samples; the window is classified as soon as its
         27 post-peak samples have arrived. Then REFRACTORY samples are ignored.
  4. Features: the window is cut into 20 blocks of 4 samples. Per block, 8 channels:
       mean ax/g, mean ay/g, mean az/g, mean gx/10, mean gy/10, mean gz/10,
       mean |gyro|/10, max | |accel| - g | / g
     (g = 9.80665 m/s^2, gyro scaled by 10 rad/s), each clipped to +-8.
     Flattened block-major: [b0c0, b0c1, ..., b0c7, b1c0, ...] = 160 float32 values.
No learned normalisation: the scaling above is all there is.
"""
import math
from collections import deque

FEATURE_VERSION = 1
SAMPLE_RATE_HZ = 200
DT_MS = 1000.0 / SAMPLE_RATE_HZ
WINDOW = 80                 # samples per window (0.40 s)
PRE_PEAK = 52               # index of the peak inside the window
POST_PEAK = WINDOW - PRE_PEAK - 1   # 27 samples after the peak
BLOCK = 4                   # samples averaged per feature step
STEPS = WINDOW // BLOCK     # 20
CHANNELS = ("ax", "ay", "az", "gx", "gy", "gz", "gyro_mag", "accel_dev_max")
N_FEATURES = STEPS * len(CHANNELS)   # 160
G = 9.80665
GYRO_SCALE = 10.0
CLIP = 8.0
ACCEL_ENERGY_WEIGHT = 0.3
TRIGGER_ENERGY = 2.0        # online: start looking for a peak above this
PEAK_SEARCH = 30            # online: samples after the trigger searched for the peak (150 ms)
REFRACTORY = 40             # online: samples ignored after a classified window (200 ms)
MAX_GAP_MS = 100.0

assert WINDOW % BLOCK == 0


def spec():
    """Everything the on-device side must match. Stored in the model's meta.json."""
    return {"feature_version": FEATURE_VERSION, "sample_rate_hz": SAMPLE_RATE_HZ, "window": WINDOW,
            "pre_peak": PRE_PEAK, "post_peak": POST_PEAK, "block": BLOCK, "steps": STEPS,
            "channels": list(CHANNELS), "n_features": N_FEATURES, "g": G, "gyro_scale": GYRO_SCALE,
            "clip": CLIP, "accel_energy_weight": ACCEL_ENERGY_WEIGHT, "trigger_energy": TRIGGER_ENERGY,
            "peak_search": PEAK_SEARCH, "refractory": REFRACTORY, "max_gap_ms": MAX_GAP_MS,
            "input": "raw ax,ay,az m/s^2 (gravity included), gx,gy,gz rad/s, timestamp t in ms"}


def energy(s):
    """s = [ax, ay, az, gx, gy, gz]."""
    a = math.sqrt(s[0] * s[0] + s[1] * s[1] + s[2] * s[2])
    w = math.sqrt(s[3] * s[3] + s[4] * s[4] + s[5] * s[5])
    return w + ACCEL_ENERGY_WEIGHT * abs(a - G)


class Resampler:
    """Online linear resampling to exactly SAMPLE_RATE_HZ using the device timestamps."""

    def __init__(self):
        self.reset()

    def reset(self):
        self.prev_t = None
        self.prev = None
        self.next_t = None

    def push(self, t_ms, vals):
        """Returns (list of resampled [6] samples, True if the stream restarted)."""
        vals = [float(v) for v in vals]
        restarted = False
        if self.prev_t is not None and (t_ms < self.prev_t or t_ms - self.prev_t > MAX_GAP_MS):
            self.reset()
            restarted = True
        if self.prev_t is None:
            self.prev_t, self.prev, self.next_t = t_ms, vals, t_ms + DT_MS
            return [vals], restarted
        if t_ms == self.prev_t:          # duplicate timestamp: keep the newer reading
            self.prev = vals
            return [], restarted
        out = []
        span = t_ms - self.prev_t
        while self.next_t <= t_ms:
            f = (self.next_t - self.prev_t) / span
            out.append([p + f * (v - p) for p, v in zip(self.prev, vals)])
            self.next_t += DT_MS
        self.prev_t, self.prev = t_ms, vals
        return out, restarted


def resample(rows):
    """rows = [[t_ms, ax, ay, az, gx, gy, gz], ...]. Returns the longest continuous 200 Hz run."""
    r = Resampler()
    runs, cur = [], []
    for row in rows:
        out, restarted = r.push(row[0], row[1:7])
        if restarted and cur:
            runs.append(cur)
            cur = []
        cur.extend(out)
    if cur:
        runs.append(cur)
    return max(runs, key=len) if runs else []


def cut(series, peak):
    """WINDOW samples with the peak at PRE_PEAK, edges padded with the nearest sample."""
    n = len(series)
    return [series[min(max(i, 0), n - 1)] for i in range(peak - PRE_PEAK, peak - PRE_PEAK + WINDOW)]


def peak_index(series):
    best, best_e = 0, -1.0
    for i, s in enumerate(series):
        e = energy(s)
        if e > best_e:
            best, best_e = i, e
    return best


def window_from_rows(rows, shift=0):
    """Offline (training): raw rows of one take -> (window, peak index in the resampled take).
    `shift` moves the window (augmentation only; 0 for evaluation)."""
    series = resample(rows)
    if not series:
        raise ValueError("take has no samples")
    p = peak_index(series)
    return cut(series, p + shift), p


def _clip(x):
    return CLIP if x > CLIP else (-CLIP if x < -CLIP else x)


def features(window):
    """WINDOW samples of [ax..gz] -> N_FEATURES floats. THE function the model is trained on."""
    if len(window) != WINDOW:
        raise ValueError(f"window must have {WINDOW} samples, got {len(window)}")
    out = []
    for b in range(STEPS):
        blk = window[b * BLOCK:(b + 1) * BLOCK]
        sums = [0.0] * 7
        dev_max = 0.0
        for s in blk:
            for k in range(3):
                sums[k] += s[k] / G
                sums[3 + k] += s[3 + k] / GYRO_SCALE
            sums[6] += math.sqrt(s[3] * s[3] + s[4] * s[4] + s[5] * s[5]) / GYRO_SCALE
            dev = abs(math.sqrt(s[0] * s[0] + s[1] * s[1] + s[2] * s[2]) - G) / G
            if dev > dev_max:
                dev_max = dev
        out.extend(_clip(v / BLOCK) for v in sums)
        out.append(_clip(dev_max))
    return out


def peak_rate(window):
    """Dominant rotation at the peak, in the backend's format: (axis 'X'/'Y'/'Z', 'positive'/'negative', rad/s)."""
    s = window[PRE_PEAK]
    k = max(range(3), key=lambda i: abs(s[3 + i]))
    return "XYZ"[k], ("positive" if s[3 + k] > 0 else "negative"), abs(s[3 + k])


class Segmenter:
    """Online peak segmentation. push() one resampled sample; returns a window (list) when one is
    complete, else None. Same window shape and peak position as window_from_rows()."""

    def __init__(self, trigger=TRIGGER_ENERGY):
        self.trigger = trigger
        self.reset()

    def reset(self):
        self.buf = deque(maxlen=WINDOW + PEAK_SEARCH + 8)
        self.n = -1                 # index of the newest sample
        self.search_until = None    # set while a peak is being tracked
        self.best_n = None
        self.best_e = 0.0
        self.quiet_until = -1

    def push(self, s):
        self.n += 1
        self.buf.append(s)
        e = energy(s)
        if self.search_until is None:
            if self.n > self.quiet_until and e >= self.trigger:
                self.search_until = self.n + PEAK_SEARCH
                self.best_n, self.best_e = self.n, e
            return None
        if self.n <= self.search_until and e > self.best_e:
            self.best_n, self.best_e = self.n, e
        if self.n >= self.search_until and self.n >= self.best_n + POST_PEAK:
            first_n = self.n - len(self.buf) + 1
            series = list(self.buf)
            win = cut(series, self.best_n - first_n)
            self.search_until = None
            self.quiet_until = self.n + REFRACTORY
            self.last_peak_energy = self.best_e
            return win
        return None
