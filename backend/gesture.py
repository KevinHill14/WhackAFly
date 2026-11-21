"""Turns the swatter's IMU sample stream into actions. Pure Python, no I/O.

The ESP32 sends about 200 samples a second: {"t": millis, "ax","ay","az": raw acceleration
in m/s^2 (gravity included), "gx","gy","gz": rotation in rad/s (bias already removed)}.

A classifier has `update(t_ms, accel, gyro) -> list of actions` and `reset()`. An action is a
dict such as {"evt": "swing", "axis": "X", "dir": "positive", "rate": 6.2}. The backend forwards
each one to Unity as a `swatter` message.

ThresholdClassifier is the stand-in: the same rules the firmware uses. The trained model will
implement the same two methods, so nothing else has to change when it replaces this.
"""
import math

AXES = "XYZ"
FIELDS = ("ax", "ay", "az", "gx", "gy", "gz")


def parse_sample(m):
    """Validate one sample. Returns (t_ms or None, accel[3], gyro[3]); raises ValueError."""
    if not isinstance(m, dict):
        raise ValueError("sample must be an object")
    vals = []
    for k in FIELDS:
        v = m.get(k)
        if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v):
            raise ValueError(f"bad {k}")
        vals.append(float(v))
    t = m.get("t")
    t = float(t) if isinstance(t, (int, float)) and not isinstance(t, bool) and math.isfinite(t) else None
    return t, vals[:3], vals[3:]


def _dominant(v):
    return max(range(3), key=lambda k: abs(v[k]))


class ThresholdClassifier:
    name = "threshold"

    def __init__(self, move_threshold=3.0, swing_threshold=4.0, gravity_alpha=0.02,
                 move_cooldown_ms=300, swing_cooldown_ms=400):
        self.move_threshold, self.swing_threshold = move_threshold, swing_threshold
        self.alpha = gravity_alpha
        self.move_cooldown, self.swing_cooldown = move_cooldown_ms, swing_cooldown_ms
        self.reset()

    def reset(self):
        self.gravity = None
        self.last_move = self.last_swing = -1e12

    def update(self, t_ms, accel, gyro):
        if self.gravity is None:
            self.gravity = list(accel)             # start from the first reading
        linear = [0.0, 0.0, 0.0]
        for k in range(3):
            self.gravity[k] += self.alpha * (accel[k] - self.gravity[k])
            linear[k] = accel[k] - self.gravity[k]

        # A swing is strong rotation about one axis. It is checked first because a swing also
        # produces linear acceleration, and that must not be reported as a push as well.
        g = _dominant(gyro)
        if abs(gyro[g]) > self.swing_threshold and t_ms - self.last_swing > self.swing_cooldown:
            self.last_swing = self.last_move = t_ms
            return [{"evt": "swing", "axis": AXES[g], "dir": "positive" if gyro[g] > 0 else "negative",
                     "rate": round(abs(gyro[g]), 2)}]
        l = _dominant(linear)
        if abs(linear[l]) > self.move_threshold and t_ms - self.last_move > self.move_cooldown:
            self.last_move = t_ms
            return [{"evt": "move", "axis": AXES[l], "dir": "positive" if linear[l] > 0 else "negative",
                     "accel": round(abs(linear[l]), 2)}]
        return []


def make_classifier(kind="threshold", model_dir=None):
    """The single swap point. "model" loads the trained network (swatter_ml/infer.py: TFLite if a
    runtime is installed, else plain Python from weights.json). If the model cannot load, this
    falls back to the threshold rules so the game keeps working, and says why."""
    if kind == "model":
        try:
            import sys
            from pathlib import Path
            sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
            from swatter_ml.infer import DEFAULT_MODEL_DIR, ModelClassifier
            return ModelClassifier(model_dir or DEFAULT_MODEL_DIR), None
        except Exception as e:   # missing model, wrong preprocessing version, bad file...
            return ThresholdClassifier(), f"model classifier unavailable ({e}); using threshold"
    return ThresholdClassifier(), None
