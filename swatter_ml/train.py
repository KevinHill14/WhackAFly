"""Train the swing classifier on recorded takes and export it for the QNX Pi.

    python -m swatter_ml.train                                   all takes in swatter_ml/data/takes
    python -m swatter_ml.train --data swatter_ml/data/takes data/training_data.csv --holdout-person alice

Model: 160 features (see features.py) -> Dense 32 relu -> Dense 16 relu -> two heads:
class probabilities (softmax) and strength (sigmoid, strength/10). About 6k parameters.

Writes to --out (default swatter_ml/models/latest):
  model_int8.tflite   int8 weights and activations, float32 input/output [1,160] -> [1, classes+1]
  model_float.tflite  the same network in float32
  weights.json        float weights for the plain-Python fallback (no TFLite or NumPy needed)
  labels.json         {"labels": [...]} index -> class name; output index len(labels) is strength
  meta.json           preprocessing spec, metrics, confusion matrices, which model is the default
  report.txt          the printed report
TensorFlow is only needed here, not on the Pi.
"""
import argparse
import datetime as _dt
import io
import json
import random
import sys
import time
from contextlib import redirect_stdout
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
    __package__ = "swatter_ml"

from swatter_ml import features as F  # noqa: E402
from swatter_ml import takes as T  # noqa: E402

ROOT = Path(__file__).resolve().parent
PREFERRED_ORDER = ["idle", "aiming", "swing", "flick", "bump"]


def class_order(found):
    return [l for l in PREFERRED_ORDER if l in found] + sorted(l for l in found if l not in PREFERRED_ORDER)


def split(takes, test_frac, holdout_person, rng):
    if holdout_person:
        test = [t for t in takes if (t.get("person") or "") == holdout_person]
        train = [t for t in takes if (t.get("person") or "") != holdout_person]
        if not test:
            raise SystemExit(f"no takes by person '{holdout_person}'")
        return train, test
    by = {}
    for t in takes:
        by.setdefault(t["label"], []).append(t)
    train, test = [], []
    for label, group in sorted(by.items()):
        rng.shuffle(group)
        n_test = int(round(len(group) * test_frac))
        if len(group) >= 3:
            n_test = max(1, n_test)
        else:
            n_test = 0
            print(f"  warning: only {len(group)} '{label}' takes, all used for training (record at least 3)")
        test += group[:n_test]
        train += group[n_test:]
    return train, test


def rotate(series, rng, max_deg):
    """The whole take seen by a sensor turned a little (a different grip or mounting).
    Accelerometer and gyro rotate together, so the physics stays consistent."""
    import math
    ax_ = [rng.gauss(0, 1) for _ in range(3)]
    n = math.sqrt(sum(v * v for v in ax_)) or 1.0
    x, y, z = (v / n for v in ax_)
    a = math.radians(rng.uniform(-max_deg, max_deg))
    c, s_, C = math.cos(a), math.sin(a), 1 - math.cos(a)
    R = [[c + x * x * C, x * y * C - z * s_, x * z * C + y * s_],
         [y * x * C + z * s_, c + y * y * C, y * z * C - x * s_],
         [z * x * C - y * s_, z * y * C + x * s_, c + z * z * C]]
    out = []
    for v in series:
        out.append([sum(R[i][j] * v[j] for j in range(3)) for i in range(3)] +
                   [sum(R[i][j] * v[3 + j] for j in range(3)) for i in range(3)])
    return out


def build(takes, labels, augment, rng, noise=0.0, rot_deg=0.0):
    """Feature rows. augment > 0 adds that many shifted copies of each take (training only)."""
    import numpy as np
    X, y, s = [], [], []
    for t in takes:
        series = F.resample(t["samples"])
        if not series:
            continue
        p = F.peak_index(series)
        shifts = [0] + [rng.randint(-6, 6) for _ in range(augment)]
        for k, sh in enumerate(shifts):
            src = rotate(series, rng, rot_deg) if (k and rot_deg) else series
            x = F.features(F.cut(src, p + sh))
            if noise and sh:
                x = [v + rng.gauss(0, noise) for v in x]
            X.append(x)
            y.append(labels.index(t["label"]))
            s.append(min(max(float(t.get("strength") or 0.0) / 10.0, 0.0), 1.0))
    return np.asarray(X, np.float32), np.asarray(y, np.int64), np.asarray(s, np.float32)


def confusion(y_true, y_pred, n):
    m = [[0] * n for _ in range(n)]
    for a, b in zip(y_true, y_pred):
        m[int(a)][int(b)] += 1
    return m


def print_confusion(m, labels, title):
    print(f"\n{title} (rows = true, columns = predicted)")
    print(" " * 10 + "".join(f"{l[:8]:>9}" for l in labels))
    for l, row in zip(labels, m):
        print(f"{l[:9]:<10}" + "".join(f"{v:>9}" for v in row))


def tflite_predict(path, X):
    import numpy as np
    import tensorflow as tf
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        it = tf.lite.Interpreter(model_path=str(path))
    it.allocate_tensors()
    i, o = it.get_input_details()[0], it.get_output_details()[0]
    out = []
    t0 = time.perf_counter()
    for x in X:
        it.set_tensor(i["index"], x[None, :].astype(np.float32))
        it.invoke()
        out.append(it.get_tensor(o["index"])[0].copy())
    ms = (time.perf_counter() - t0) * 1000.0 / max(1, len(X))
    return np.asarray(out), ms


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", nargs="+", default=[str(T.TAKES_DIR)], help="take folders, take files or collect.py CSVs")
    ap.add_argument("--out", default=str(ROOT / "models" / "latest"))
    ap.add_argument("--test-frac", type=float, default=0.2)
    ap.add_argument("--holdout-person", help="test only on this person's takes (never trained on)")
    ap.add_argument("--epochs", type=int, default=300)
    ap.add_argument("--augment", type=int, default=8, help="shifted copies per training take")
    ap.add_argument("--rotate", type=float, default=25.0, help="max degrees of random rotation in augmented copies")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--min-takes", type=int, default=10)
    a = ap.parse_args()

    log = io.StringIO()

    class Tee:
        def write(self, s):
            sys.__stdout__.write(s)
            sys.__stdout__.flush()
            log.write(s)

        def flush(self):
            sys.__stdout__.flush()

    with redirect_stdout(Tee()):
        run(a)
    out = Path(a.out)
    if out.exists():
        (out / "report.txt").write_text(log.getvalue())


def run(a):
    rng = random.Random(a.seed)
    takes = [t for t in T.load_all(a.data) if t.get("label") and t.get("samples")]
    print(f"{len(takes)} takes from {', '.join(a.data)}")
    if len(takes) < a.min_takes:
        raise SystemExit(f"need at least {a.min_takes} takes to train (have {len(takes)})")
    labels = class_order({t["label"] for t in takes})
    if len(labels) < 2:
        raise SystemExit("need at least two classes")
    counts = {l: sum(t["label"] == l for t in takes) for l in labels}
    print("takes per class:", ", ".join(f"{l} {n}" for l, n in counts.items()))

    import numpy as np
    import tensorflow as tf
    tf.keras.utils.set_random_seed(a.seed)

    train, test = split(takes, a.test_frac, a.holdout_person, rng)
    Xtr, ytr, str_ = build(train, labels, a.augment, rng, noise=0.01, rot_deg=a.rotate)
    Xte, yte, ste = build(test, labels, 0, rng)
    print(f"train: {len(train)} takes -> {len(Xtr)} windows (with shifted copies); test: {len(test)} takes held out")

    K = len(labels)
    inp = tf.keras.Input(shape=(F.N_FEATURES,), name="features")
    h = tf.keras.layers.Dense(32, activation="relu", name="dense1")(inp)
    h = tf.keras.layers.Dropout(0.1)(h)
    h = tf.keras.layers.Dense(16, activation="relu", name="dense2")(h)
    probs = tf.keras.layers.Dense(K, activation="softmax", name="probs")(h)
    strength = tf.keras.layers.Dense(1, activation="sigmoid", name="strength")(h)
    model = tf.keras.Model(inp, {"probs": probs, "strength": strength})

    # balance the classes
    freq = np.bincount(ytr, minlength=K).astype(np.float32)
    w = (len(ytr) / (K * np.maximum(freq, 1)))[ytr]
    model.compile(optimizer=tf.keras.optimizers.Adam(3e-3),
                  loss={"probs": "sparse_categorical_crossentropy", "strength": "mse"},
                  loss_weights={"probs": 1.0, "strength": 2.0})
    stop = tf.keras.callbacks.EarlyStopping(monitor="val_loss", patience=30, restore_best_weights=True)
    hist = model.fit(Xtr, {"probs": ytr, "strength": str_}, sample_weight=w, validation_split=0.15,
                     epochs=a.epochs, batch_size=32, verbose=0, callbacks=[stop])
    print(f"trained {len(hist.history['loss'])} epochs, {model.count_params()} parameters")

    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)

    # One output tensor for the device: [probabilities..., strength]
    export = tf.keras.Model(inp, tf.keras.layers.Concatenate(name="probs_strength")([probs, strength]))
    conv = tf.lite.TFLiteConverter.from_keras_model(export)
    with redirect_stdout(io.StringIO()):
        (out / "model_float.tflite").write_bytes(conv.convert())
    rep = Xtr[np.random.default_rng(a.seed).permutation(len(Xtr))[:500]]
    conv = tf.lite.TFLiteConverter.from_keras_model(export)
    conv.optimizations = [tf.lite.Optimize.DEFAULT]
    conv.representative_dataset = lambda: ([x[None, :]] for x in rep)
    int8_ok = True
    try:
        with redirect_stdout(io.StringIO()):
            (out / "model_int8.tflite").write_bytes(conv.convert())
    except Exception as e:
        int8_ok = False
        print("int8 conversion failed, float model only:", e)

    # plain-Python weights (same float network)
    layers = []
    for name, act in (("dense1", "relu"), ("dense2", "relu"), ("probs", "softmax"), ("strength", "sigmoid")):
        W, b = model.get_layer(name).get_weights()
        layers.append({"name": name, "activation": act, "W": W.astype(float).round(7).tolist(),
                       "b": b.astype(float).round(7).tolist()})
    (out / "weights.json").write_text(json.dumps({"n_classes": K, "heads": {"probs": 2, "strength": 3},
                                                  "layers": layers}))
    (out / "labels.json").write_text(json.dumps({"labels": labels, "strength_index": K,
                                                 "strength_scale": "output * 10 = strength 0-10"}, indent=2))

    report = {}
    if len(Xte):
        kp = model.predict(Xte, verbose=0)
        pred = kp["probs"].argmax(1)
        acc = float((pred == yte).mean())
        mae = float(np.abs(kp["strength"][:, 0] - ste).mean() * 10)
        m = confusion(yte, pred, K)
        print(f"\nheld-out test: {len(Xte)} takes   Keras accuracy {acc * 100:.1f}%   strength error {mae:.2f} / 10")
        print_confusion(m, labels, "Confusion matrix, Keras float")
        report["keras"] = {"accuracy": acc, "strength_mae": mae, "confusion": m}
        for kind in (["float", "int8"] if int8_ok else ["float"]):
            y, ms = tflite_predict(out / f"model_{kind}.tflite", Xte)
            p = y[:, :K].argmax(1)
            acc_k = float((p == yte).mean())
            mae_k = float(np.abs(y[:, K] - ste).mean() * 10)
            agree = float((p == pred).mean())
            mk = confusion(yte, p, K)
            print(f"TFLite {kind:<5}: accuracy {acc_k * 100:.1f}%  strength error {mae_k:.2f}  "
                  f"agrees with Keras on {agree * 100:.1f}%  {ms:.3f} ms per window on this machine")
            report[kind] = {"accuracy": acc_k, "strength_mae": mae_k, "agree_with_keras": agree,
                            "ms_per_window": ms, "confusion": mk}
        if int8_ok:
            print_confusion(report["int8"]["confusion"], labels, "Confusion matrix, TFLite int8 (what the Pi runs)")
        # the plain-Python network must give the same answer as Keras
        sys.path.insert(0, str(ROOT.parent))
        from swatter_ml.infer import PythonNet
        net = PythonNet(out / "weights.json")
        diffs = [max(abs(a_ - b_) for a_, b_ in zip(net.run(list(map(float, x)))[0], kp["probs"][i]))
                 for i, x in enumerate(Xte[:50])]
        print(f"plain-Python network vs Keras: max probability difference {max(diffs):.2e}")
        report["python_vs_keras_max_diff"] = float(max(diffs))
    else:
        print("no held-out takes (too few per class); nothing to evaluate")

    default = "model_int8.tflite"
    if not int8_ok or (report.get("int8") and report["int8"]["accuracy"] < report["float"]["accuracy"] - 0.03):
        default = "model_float.tflite"
        print("int8 lost more than 3 points of accuracy: the float model is the default")
    meta = {"created": _dt.datetime.now().isoformat(timespec="seconds"), "labels": labels,
            "default_model": default, "features": F.spec(),
            "io": {"input": f"float32 [1,{F.N_FEATURES}]", "output": f"float32 [1,{K + 1}] = probabilities then strength/10"},
            "takes": {"total": len(takes), "train": len(train), "test": len(test), "per_class": counts},
            "holdout_person": a.holdout_person, "data": a.data, "metrics": report,
            "sources": sorted({(t.get("device") or {}).get("source", "") for t in takes})}
    (out / "meta.json").write_text(json.dumps(meta, indent=2))
    print(f"\nexported to {out}: {default} (default), "
          f"{', '.join(p.name for p in sorted(out.glob('*')) if p.suffix in ('.tflite', '.json'))}")
    if meta["sources"] == ["synthetic"]:
        print("NOTE: trained on synthetic data only; this proves the pipeline, not the real swatter.")


if __name__ == "__main__":
    main()
