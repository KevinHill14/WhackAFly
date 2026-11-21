"""Run the trained swing classifier on a live, replayed or synthetic stream.

ModelClassifier has the same interface as backend/gesture.py's ThresholdClassifier
(`name`, `update(t_ms, accel, gyro) -> list of actions`, `reset()`), so the backend's single
swap point can use either. It needs only the standard library: with a TFLite runtime
(ai_edge_litert, tflite_runtime or tensorflow, which also need NumPy) it runs model.tflite;
without one it runs the same network from weights.json in plain Python.

    python -m swatter_ml.infer --takes swatter_ml/data/takes      replay recorded takes, score them
    python -m swatter_ml.infer --synthetic --seconds 20           synthetic stream with random gestures
    python -m swatter_ml.infer --csv data/training_data.csv       replay a collect.py CSV
    python -m swatter_ml.infer --live --port 8767                 listen for the ESP32 (stdlib WebSocket)
Options: --model swatter_ml/models/latest  --backend auto|tflite|python  --model-file model_int8.tflite
"""
import argparse
import json
import math
import sys
import time
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
    __package__ = "swatter_ml"

from swatter_ml import features as F  # noqa: E402

DEFAULT_MODEL_DIR = Path(__file__).resolve().parent / "models" / "latest"
SWING_KINDS = ("swing", "flick")


class PythonNet:
    """The exported network in plain Python (float weights from weights.json)."""
    kind = "python"

    def __init__(self, path):
        d = json.loads(Path(path).read_text())
        self.layers = [(l["W"], l["b"], l["activation"]) for l in d["layers"]]
        self.n_classes = d["n_classes"]
        self.heads = d["heads"]          # {"probs": layer index, "strength": layer index} of the last two

    @staticmethod
    def _dense(x, W, b, act):
        out = list(b)
        for i, xi in enumerate(x):
            if xi:
                row = W[i]
                for j in range(len(out)):
                    out[j] += xi * row[j]
        if act == "relu":
            return [v if v > 0 else 0.0 for v in out]
        if act == "sigmoid":
            return [1.0 / (1.0 + math.exp(-max(-60.0, min(60.0, v)))) for v in out]
        if act == "softmax":
            m = max(out)
            e = [math.exp(v - m) for v in out]
            s = sum(e)
            return [v / s for v in e]
        return out

    def run(self, x):
        trunk = self.layers[:-2]
        for W, b, act in trunk:
            x = self._dense(x, W, b, act)
        probs = self._dense(x, *self.layers[-2])
        strength = self._dense(x, *self.layers[-1])[0]
        return probs, strength


class TFLiteNet:
    """model*.tflite: input float32 [1, N_FEATURES], output float32 [1, n_classes + 1]
    (class probabilities, then strength 0-1)."""
    kind = "tflite"

    def __init__(self, path, n_classes):
        import importlib
        Interpreter = None
        for mod in ("ai_edge_litert.interpreter", "tflite_runtime.interpreter", "tensorflow"):
            try:
                m = importlib.import_module(mod)
                Interpreter = m.lite.Interpreter if mod == "tensorflow" else m.Interpreter   # tf.lite is lazy
                break
            except Exception:
                continue
        if Interpreter is None:
            raise ImportError("no TFLite runtime (ai_edge_litert, tflite_runtime or tensorflow)")
        import numpy as np
        self.np = np
        import warnings
        with warnings.catch_warnings():         # tf.lite's "use ai_edge_litert" notice
            warnings.simplefilter("ignore")
            self.it = Interpreter(model_path=str(path))
        self.it.allocate_tensors()
        self.inp = self.it.get_input_details()[0]
        self.out = self.it.get_output_details()[0]
        self.n_classes = n_classes
        self.kind = f"tflite:{Path(path).name}"

    def run(self, x):
        np = self.np
        a = np.asarray([x], dtype=np.float32)
        if self.inp["dtype"] != np.float32:              # int8 input model
            s, z = self.inp["quantization"]
            a = np.clip(np.round(a / s + z), -128, 127).astype(self.inp["dtype"])
        self.it.set_tensor(self.inp["index"], a)
        self.it.invoke()
        y = self.it.get_tensor(self.out["index"])[0]
        if self.out["dtype"] != np.float32:
            s, z = self.out["quantization"]
            y = (y.astype(np.float32) - z) * s
        y = [float(v) for v in y]
        return y[:self.n_classes], y[self.n_classes]


def load_net(model_dir=DEFAULT_MODEL_DIR, backend="auto", model_file=None):
    """Returns (net, labels, meta). Refuses a model trained with different preprocessing."""
    d = Path(model_dir)
    meta = json.loads((d / "meta.json").read_text())
    labels = json.loads((d / "labels.json").read_text())["labels"]
    want, have = meta.get("features", {}), F.spec()
    diff = [k for k in have if k in want and want[k] != have[k]]
    if diff or want.get("feature_version") != F.FEATURE_VERSION:
        raise ValueError(f"model was trained with different preprocessing ({', '.join(diff) or 'version'}); retrain it")
    if backend in ("auto", "tflite"):
        try:
            return TFLiteNet(d / (model_file or meta.get("default_model", "model_int8.tflite")), len(labels)), labels, meta
        except Exception as e:
            if backend == "tflite":
                raise
            print(f"[swatter_ml] TFLite not available ({e}); using the plain-Python network", file=sys.stderr)
    return PythonNet(d / "weights.json"), labels, meta


def predict_window(net, labels, window):
    t0 = time.perf_counter()
    probs, strength = net.run(F.features(window))
    ms = (time.perf_counter() - t0) * 1000.0
    k = max(range(len(probs)), key=lambda i: probs[i])
    return {"label": labels[k], "conf": round(probs[k], 3), "strength": round(10.0 * strength, 1),
            "probs": {l: round(p, 3) for l, p in zip(labels, probs)}, "ms": round(ms, 3)}


class ModelClassifier:
    """Drop-in for backend/gesture.py's ThresholdClassifier."""
    name = "model"

    def __init__(self, model_dir=DEFAULT_MODEL_DIR, backend="auto", min_conf=0.5, emit=SWING_KINDS, model_file=None):
        self.net, self.labels, self.meta = load_net(model_dir, backend, model_file)
        self.min_conf, self.emit = min_conf, tuple(emit)
        self.resampler = F.Resampler()
        self.segmenter = F.Segmenter()
        self.last = None            # the last prediction, whatever its class
        self.on_prediction = None   # optional callback(prediction dict)
        self.reset()

    def reset(self):
        self.resampler.reset()
        self.segmenter.reset()

    def update(self, t_ms, accel, gyro):
        out, restarted = self.resampler.push(t_ms, list(accel) + list(gyro))
        if restarted:
            self.segmenter.reset()
        actions = []
        for s in out:
            win = self.segmenter.push(s)
            if win is None:
                continue
            p = predict_window(self.net, self.labels, win)
            p["t"] = t_ms
            self.last = p
            if self.on_prediction:
                self.on_prediction(p)
            if p["label"] in self.emit and p["conf"] >= self.min_conf:
                axis, direction, rate = F.peak_rate(win)
                actions.append({"evt": "swing", "kind": p["label"], "strength": round(p["strength"] / 10.0, 3),
                                "conf": p["conf"], "axis": axis, "dir": direction, "rate": round(rate, 2),
                                "source": "model"})
        return actions


def _print_pred(p, t0_ms, truth=None):
    extra = f"   truth: {truth}" if truth else ""
    print(f"t={(p['t'] - t0_ms) / 1000:7.2f}s  {p['label']:<7} conf {p['conf']:.2f}  "
          f"strength {p['strength']:4.1f}/10  ({p['ms']:.2f} ms){extra}", flush=True)


def replay_takes(clf, takes):
    """Each take through the online path (resampler + segmenter + model), like the live stream."""
    """Returns (exact matches, swing-or-not matches, total, confusion {true: Counter(predicted)}).
    A take that never crosses the trigger is predicted 'idle' (the game sees no swing either)."""
    from collections import Counter
    right = decision = total = 0
    conf = {}
    for t in takes:
        clf.reset()
        preds = []
        clf.on_prediction = preds.append
        for r in t["samples"]:
            clf.update(r[0], r[1:4], r[4:7])
        got = max(preds, key=lambda p: p["conf"])["label"] if preds else "idle"
        total += 1
        right += got == t["label"]
        decision += (got in SWING_KINDS) == (t["label"] in SWING_KINDS)
        conf.setdefault(t["label"], Counter())[got] += 1
    clf.on_prediction = None
    return right, decision, total, conf


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--takes", nargs="+", help="take folders/files or collect.py CSVs to replay and score")
    src.add_argument("--csv", help="replay a collect.py CSV as one continuous stream")
    src.add_argument("--synthetic", action="store_true", help="synthetic stream with random gestures")
    src.add_argument("--live", action="store_true", help="listen for the ESP32 WebSocket stream")
    ap.add_argument("--model", default=str(DEFAULT_MODEL_DIR))
    ap.add_argument("--backend", default="auto", choices=["auto", "tflite", "python"])
    ap.add_argument("--model-file", help="e.g. model_float.tflite (default: meta.json's default_model)")
    ap.add_argument("--seconds", type=float, default=20.0, help="synthetic: length of the stream")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--port", type=int, default=8767, help="live: WebSocket port")
    ap.add_argument("--realtime", action="store_true", help="synthetic/csv: pace samples at 200 Hz")
    a = ap.parse_args()

    clf = ModelClassifier(a.model, a.backend, model_file=a.model_file)
    print(f"model: {a.model}  backend: {clf.net.kind}  classes: {', '.join(clf.labels)}")

    if a.takes:
        from swatter_ml import takes as T
        tk = T.load_all(a.takes)
        right, decision, total, conf = replay_takes(clf, tk)
        print(f"online replay of {total} takes: {right}/{total} exact class ({100.0 * right / max(1, total):.1f}%), "
              f"{decision}/{total} right swing-or-not decision ({100.0 * decision / max(1, total):.1f}%)")
        labels = clf.labels
        print("true \\ predicted " + " ".join(f"{l[:7]:>7}" for l in labels))
        for l in labels:
            row = conf.get(l, {})
            print(f"{l:<17}" + " ".join(f"{row.get(m, 0):>7}" for m in labels))
        return

    if a.synthetic:
        from swatter_ml.synthetic import SyntheticIMU
        imu = SyntheticIMU(seed=a.seed, random_gestures=True)
        n = int(a.seconds * F.SAMPLE_RATE_HZ)
        t0 = None
        for _ in range(n):
            t, v = imu.next()
            t0 = t if t0 is None else t0
            before = clf.last
            acts = clf.update(t, v[:3], v[3:])
            if clf.last is not before:
                truth = next((f"{l} {s:.0f}" for ts, l, s in reversed(imu.truth) if ts <= t), None)
                _print_pred(clf.last, t0, truth)
            for act in acts:
                print("   ACTION ->", json.dumps(act))
            if a.realtime:
                time.sleep(F.DT_MS / 1000.0)
        return

    if a.csv:
        from swatter_ml import takes as T
        rows = sorted((r for t in T.load_csv(a.csv) for r in t["samples"]), key=lambda r: r[0])
        t0 = rows[0][0] if rows else 0
        for r in rows:
            before = clf.last
            acts = clf.update(r[0], r[1:4], r[4:7])
            if clf.last is not before:
                _print_pred(clf.last, t0)
            for act in acts:
                print("   ACTION ->", json.dumps(act))
            if a.realtime:
                time.sleep(F.DT_MS / 1000.0)
        return

    # live: the backend's standard-library WebSocket, so this runs on the Pi with nothing installed
    import asyncio
    root = Path(__file__).resolve().parent.parent
    sys.path.insert(0, str(root / "brain"))
    sys.path.insert(0, str(root / "backend"))
    import ws  # noqa: E402
    from gesture import parse_sample  # noqa: E402

    async def on_esp32(sock, path):
        print(f"swatter connected from {sock.peer}")
        clf.reset()
        t0 = None
        try:
            while True:
                try:
                    t, accel, gyro = parse_sample(json.loads(await sock.recv()))
                except (ValueError, TypeError, RecursionError):
                    continue
                t = t if t is not None else time.monotonic() * 1000.0
                t0 = t if t0 is None else t0
                before = clf.last
                acts = clf.update(t, accel, gyro)
                if clf.last is not before:
                    _print_pred(clf.last, t0)
                for act in acts:
                    print("   ACTION ->", json.dumps(act), flush=True)
        except ws.ConnectionClosed:
            print("swatter disconnected")

    async def run():
        await ws.serve(on_esp32, "0.0.0.0", a.port)
        print(f"waiting for the swatter on ws://0.0.0.0:{a.port} (Ctrl+C to stop)")
        await asyncio.Event().wait()

    try:
        asyncio.run(run())
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
