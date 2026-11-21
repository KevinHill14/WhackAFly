"""Synthetic swatter IMU, for developing without the real swatter. Standard library only.

Produces the same samples the ESP32 sends (raw accel with gravity in m/s^2, gyro in rad/s,
200 Hz, millisecond timestamps), with rough physical models of each gesture:
  swing   big rotation pulse (6-15 rad/s, 0.2-0.3 s) plus centripetal acceleration
  flick   short wrist snap, rotation one way then back (0.08-0.13 s)
  aiming  slow, smooth rotation under 1.5 rad/s
  bump    sharp acceleration knock with a ringing tail, little rotation
  idle    hand tremor and sensor noise only
Strength 0-10 scales the amplitude, so the strength head has something real to learn.
These are not real swings: a model trained on them only proves the pipeline works.
"""
import math
import random

from . import features as F

LABELS = ("idle", "aiming", "swing", "flick", "bump")
ACC_LIMIT = 8 * F.G          # the firmware sets +-8 g
GYRO_LIMIT = math.radians(1000)


def _unit(v):
    n = math.sqrt(sum(x * x for x in v)) or 1.0
    return [x / n for x in v]


class Gesture:
    def __init__(self, label, strength, rng):
        self.label, self.strength = label, strength
        f = 0.4 + 0.06 * strength
        self.axis = rng.choice([0, 1, 2]) if label in ("aiming", "bump") else rng.choice([2, 2, 2, 0])
        self.sign = rng.choice([-1, 1])
        if label == "swing":
            self.dur, self.amp = rng.uniform(0.20, 0.30), 15.0 * f * rng.uniform(0.9, 1.1)
        elif label == "flick":
            self.dur, self.amp = rng.uniform(0.08, 0.13), 11.0 * f * rng.uniform(0.9, 1.1)
        elif label == "aiming":
            self.dur, self.amp = rng.uniform(0.4, 0.9), 1.3 * f * rng.uniform(0.7, 1.1)
        elif label == "bump":
            self.dur, self.amp = rng.uniform(0.10, 0.16), 28.0 * f * rng.uniform(0.85, 1.15)
            self.bump_dir = _unit([rng.gauss(0, 1) for _ in range(3)])
        else:
            self.dur, self.amp = 0.4, 0.0
        self.t = 0.0

    def sample(self, dt):
        """(accel delta [3], gyro [3]) at the current time, then advance. None when finished."""
        if self.t > self.dur:
            return None
        u = self.t / self.dur
        self.t += dt
        acc, gyro = [0.0] * 3, [0.0] * 3
        a = self.axis
        if self.label in ("swing", "flick"):
            if self.label == "swing":
                w = self.amp * math.sin(math.pi * u) ** 2
                dw = self.amp * math.pi / self.dur * math.sin(2 * math.pi * u)
                r = 0.35
            else:
                w = self.amp * math.sin(2 * math.pi * u)
                dw = self.amp * 2 * math.pi / self.dur * math.cos(2 * math.pi * u)
                r = 0.12
            gyro[a] = self.sign * w
            gyro[(a + 1) % 3] = 0.15 * w
            acc[(a + 1) % 3] = -r * w * w            # centripetal, towards the hand
            acc[(a + 2) % 3] = self.sign * r * dw * 0.6
        elif self.label == "aiming":
            gyro[a] = self.sign * self.amp * math.sin(math.pi * u)
            gyro[(a + 1) % 3] = 0.4 * self.amp * math.sin(2 * math.pi * u)
            acc[(a + 2) % 3] = 0.3 * self.amp * math.sin(2 * math.pi * u)
        elif self.label == "bump":
            tt = self.t
            ring = math.exp(-tt / 0.025) * math.cos(2 * math.pi * 40 * tt)
            for k in range(3):
                acc[k] = self.amp * ring * self.bump_dir[k]
            gyro[a] = self.sign * 0.08 * self.amp * ring
        return acc, gyro


class SyntheticIMU:
    """Call next() for one sample: (t_ms, [ax, ay, az, gx, gy, gz]).
    perform(label, strength, delay_s) schedules a gesture; with random_gestures=True it also does
    random ones by itself every few seconds. `truth` lists (t_ms of start, label, strength)."""

    def __init__(self, seed=None, random_gestures=False, start_ms=0):
        self.rng = random.Random(seed)
        self.t_ms = float(start_ms)
        self.random_gestures = random_gestures
        self.queue = []                # [start_ms, label, strength]
        self.active = None
        self.truth = []
        self.grav = _unit([0.1, -1.0, 0.2])
        self.pose = list(self.grav)    # the hand's resting orientation; gravity drifts towards it
        self.next_random = self.t_ms + 2000
        self.tremor = [0.0] * 6

    def perform(self, label, strength=5.0, delay_s=0.0):
        self.queue.append([self.t_ms + delay_s * 1000.0, label, float(strength)])
        self.queue.sort()

    def new_pose(self, spread=0.5):
        """A new way of holding the swatter: gravity within roughly 30 degrees of the usual grip."""
        self.pose = _unit([b + self.rng.gauss(0, spread) for b in (0.1, -1.0, 0.2)])

    def clear(self):
        self.queue.clear()

    def next(self):
        dt = F.DT_MS / 1000.0
        rng = self.rng
        if self.active is None and self.queue and self.queue[0][0] <= self.t_ms:
            _, label, strength = self.queue.pop(0)
            self.active = Gesture(label, strength, rng)
            self.truth.append((self.t_ms, label, strength))
        if self.random_gestures and self.active is None and not self.queue and self.t_ms >= self.next_random:
            self.new_pose()
            self.perform(rng.choice(LABELS[1:]), rng.uniform(2, 10))
            self.next_random = self.t_ms + rng.uniform(1500, 3500)

        # gravity wanders slowly (the hand moves a little)
        self.grav = _unit([g + rng.gauss(0, 0.002) + 0.01 * (p - g) for g, p in zip(self.grav, self.pose)])
        acc = [F.G * g for g in self.grav]
        gyro = [0.0, 0.0, 0.0]
        for k in range(6):            # tremor: low-pass noise
            self.tremor[k] += 0.1 * (rng.gauss(0, 1) - self.tremor[k])
        for k in range(3):
            acc[k] += 0.25 * self.tremor[k] + rng.gauss(0, 0.06)
            gyro[k] += 0.05 * self.tremor[3 + k] + rng.gauss(0, 0.015)
        if self.active is not None:
            s = self.active.sample(dt)
            if s is None:
                self.active = None
            else:
                for k in range(3):
                    acc[k] += s[0][k]
                    gyro[k] += s[1][k]
        acc = [max(-ACC_LIMIT, min(ACC_LIMIT, v)) for v in acc]
        gyro = [max(-GYRO_LIMIT, min(GYRO_LIMIT, v)) for v in gyro]
        # the ESP32's clock: integer ms with a little jitter around 5 ms
        t = int(self.t_ms + rng.choice((0, 0, 0, 1)))
        self.t_ms += F.DT_MS
        return t, acc + gyro

    def take(self, label, strength=5.0, seconds=1.5, lead_s=None):
        """Rows of one recorded take with the gesture somewhere inside."""
        self.new_pose()
        lead = self.rng.uniform(0.3, max(0.35, seconds - 0.6)) if lead_s is None else lead_s
        if label != "idle":
            self.perform(label, strength, lead)
        n = int(seconds * F.SAMPLE_RATE_HZ)
        return [[t, *v] for t, v in (self.next() for _ in range(n))]


def main():
    """python -m swatter_ml.synthetic --per-class 40 --out swatter_ml/data/synthetic_takes"""
    import argparse
    from . import takes as T
    ap = argparse.ArgumentParser(description="Write synthetic takes, for testing the pipeline without the swatter.")
    ap.add_argument("--per-class", type=int, default=40)
    ap.add_argument("--out", default=str(T.ROOT / "data" / "synthetic_takes"))
    ap.add_argument("--seed", type=int, default=7)
    a = ap.parse_args()
    imu = SyntheticIMU(seed=a.seed)
    rng = random.Random(a.seed)
    n = 0
    for label in LABELS:
        for i in range(a.per_class):
            strength = 0.0 if label == "idle" else float(rng.randint(1, 10))
            rows = imu.take(label, strength)
            T.save(T.make_take(rows, label, strength, "synthetic", "single", "synthetic",
                               take_id=f"syn-{label}-{a.seed}-{i:03d}"), a.out)
            for _ in range(rng.randint(20, 80)):   # some idle time between takes
                imu.next()
            n += 1
    print(f"wrote {n} synthetic takes to {a.out}")


if __name__ == "__main__":
    main()
