"""Tests for the swing-classifier tooling. Standard library only (no TensorFlow, no NumPy).

    python -m unittest swatter_ml.test_swatter_ml -v      (from the repo root)
"""
import csv
import json
import random
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "backend"))

from swatter_ml import features as F  # noqa: E402
from swatter_ml import takes as T  # noqa: E402
from swatter_ml.synthetic import LABELS, SyntheticIMU  # noqa: E402


def fake_model(folder, labels=("idle", "swing"), seed=0):
    """A random network in the exported format, so the inference path can be tested without TF."""
    rng = random.Random(seed)
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)

    def layer(n_in, n_out, act):
        return {"activation": act, "W": [[rng.gauss(0, 0.2) for _ in range(n_out)] for _ in range(n_in)],
                "b": [0.0] * n_out}
    K = len(labels)
    (folder / "weights.json").write_text(json.dumps({"n_classes": K, "heads": {"probs": 2, "strength": 3}, "layers": [
        layer(F.N_FEATURES, 8, "relu"), layer(8, 4, "relu"), layer(4, K, "softmax"), layer(4, 1, "sigmoid")]}))
    (folder / "labels.json").write_text(json.dumps({"labels": list(labels)}))
    (folder / "meta.json").write_text(json.dumps({"features": F.spec(), "default_model": "none.tflite"}))
    return folder


class Preprocessing(unittest.TestCase):
    def test_resampler_uniform_and_interpolates(self):
        r = F.Resampler()
        out = []
        for t, v in ((0, 0.0), (7, 7.0), (10, 10.0), (21, 21.0)):
            o, _ = r.push(t, [v] * 6)
            out += o
        self.assertEqual([round(s[0], 6) for s in out], [0.0, 5.0, 10.0, 15.0, 20.0])

    def test_resampler_restarts_on_gap_and_reset(self):
        r = F.Resampler()
        r.push(1000, [0] * 6)
        _, restarted = r.push(1005, [0] * 6)
        self.assertFalse(restarted)
        _, restarted = r.push(1500, [0] * 6)          # 495 ms gap
        self.assertTrue(restarted)
        _, restarted = r.push(10, [0] * 6)            # device rebooted
        self.assertTrue(restarted)

    def test_duplicate_timestamp_dropped(self):
        r = F.Resampler()
        r.push(0, [0] * 6)
        out, _ = r.push(0, [1] * 6)
        self.assertEqual(out, [])

    def test_feature_shape_and_clip(self):
        win = [[100.0, 0, 0, 50.0, 0, 0]] * F.WINDOW
        x = F.features(win)
        self.assertEqual(len(x), F.N_FEATURES)
        self.assertTrue(all(-F.CLIP <= v <= F.CLIP for v in x))
        with self.assertRaises(ValueError):
            F.features(win[:-1])

    def test_cut_pads_edges(self):
        series = [[float(i)] * 6 for i in range(10)]
        w = F.cut(series, 2)
        self.assertEqual(len(w), F.WINDOW)
        self.assertEqual(w[0][0], 0.0)
        self.assertEqual(w[-1][0], 9.0)
        self.assertEqual(w[F.PRE_PEAK][0], 2.0)

    def test_online_window_equals_offline_window(self):
        """The guarantee the model relies on: live windows are exactly the training windows."""
        imu = SyntheticIMU(seed=5)
        checked = 0
        for label in ("swing", "flick", "bump") * 6:
            rows = imu.take(label, 8)
            series = F.resample(rows)
            off = F.cut(series, F.peak_index(series))
            r, s, wins = F.Resampler(), F.Segmenter(), []
            for row in rows:
                for x in r.push(row[0], row[1:7])[0]:
                    w = s.push(x)
                    if w:
                        wins.append(w)
            if F.energy(off[F.PRE_PEAK]) < F.TRIGGER_ENERGY:
                continue                                  # never triggers live: nothing to compare
            self.assertIn(off, wins, label)
            self.assertEqual(F.features(off), F.features(wins[[w == off for w in wins].index(True)]))
            checked += 1
        self.assertGreater(checked, 10)

    def test_idle_never_triggers(self):
        imu = SyntheticIMU(seed=2)
        r, s = F.Resampler(), F.Segmenter()
        for _ in range(2000):
            t, v = imu.next()
            for x in r.push(t, v)[0]:
                self.assertIsNone(s.push(x))


class Takes(unittest.TestCase):
    def test_save_load_update_delete(self):
        with tempfile.TemporaryDirectory() as d:
            rows = SyntheticIMU(seed=1).take("swing", 7)
            t = T.make_take(rows, "swing", 7, "eesha", "single", "synthetic")
            T.save(t, d)
            back = T.load(t["id"], d)
            self.assertEqual(back["label"], "swing")
            self.assertEqual(len(back["samples"]), len(rows))
            self.assertAlmostEqual(back["sample_rate_hz"], 200, delta=3)
            T.update(t["id"], d, label="flick", strength=3)
            self.assertEqual(T.load(t["id"], d)["label"], "flick")
            self.assertTrue(T.delete(t["id"], d))
            self.assertEqual(T.load_all([d]), [])

    def test_bad_ids_rejected(self):
        for bad in ("../x", "a/b", "", "x" * 200):
            with self.assertRaises(ValueError):
                T.path_for(bad)

    def test_collect_csv_import(self):
        """backend/collect.py's CSV becomes one take per trial."""
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "training_data.csv"
            imu = SyntheticIMU(seed=3)
            with open(p, "w", newline="") as f:
                w = csv.writer(f)
                w.writerow(["timestamp", "ax", "ay", "az", "gx", "gy", "gz", "label", "trial_id", "person"])
                for trial, label in ((1, "swing left"), (2, "idle")):
                    for r in imu.take("swing" if trial == 1 else "idle", 5):
                        w.writerow([int(r[0]), *r[1:], label, trial, "alice"])
            tk = T.load_all([p])
            self.assertEqual(sorted(t["label"] for t in tk), ["idle", "swing left"])
            self.assertTrue(all(t["person"] == "alice" for t in tk))
            F.window_from_rows(tk[0]["samples"])


class Inference(unittest.TestCase):
    def test_model_classifier_interface(self):
        from swatter_ml.infer import ModelClassifier
        with tempfile.TemporaryDirectory() as d:
            clf = ModelClassifier(fake_model(d), backend="python", min_conf=0.0)
            imu = SyntheticIMU(seed=4)
            imu.perform("swing", 9, 0.5)
            acts, preds = [], []
            clf.on_prediction = preds.append
            for _ in range(600):
                t, v = imu.next()
                acts += clf.update(t, v[:3], v[3:])
            self.assertEqual(len(preds), 1)
            for a in acts:
                self.assertEqual(a["evt"], "swing")
                self.assertIn(a["axis"], "XYZ")
                self.assertGreaterEqual(a["strength"], 0)
            clf.reset()

    def test_rejects_other_preprocessing(self):
        from swatter_ml.infer import ModelClassifier
        with tempfile.TemporaryDirectory() as d:
            fake_model(d)
            meta = json.loads((Path(d) / "meta.json").read_text())
            meta["features"]["window"] = 64
            (Path(d) / "meta.json").write_text(json.dumps(meta))
            with self.assertRaises(ValueError):
                ModelClassifier(d, backend="python")

    def test_backend_swap_point_falls_back(self):
        from gesture import ThresholdClassifier, make_classifier
        clf, why = make_classifier("model", "no/such/model")
        self.assertIsInstance(clf, ThresholdClassifier)
        self.assertIn("unavailable", why)
        with tempfile.TemporaryDirectory() as d:
            clf, why = make_classifier("model", fake_model(d))
            self.assertIsNone(why)
            self.assertEqual(clf.name, "model")
        clf, why = make_classifier("threshold")
        self.assertEqual(clf.name, "threshold")

    def test_tflite_matches_python_if_available(self):
        """With a trained model and a TFLite runtime, both backends must agree."""
        from swatter_ml.infer import DEFAULT_MODEL_DIR, PythonNet, TFLiteNet
        d = DEFAULT_MODEL_DIR
        if not (d / "model_float.tflite").exists():
            self.skipTest("no trained model")
        labels = json.loads((d / "labels.json").read_text())["labels"]
        try:
            tfl = TFLiteNet(d / "model_float.tflite", len(labels))
        except ImportError:
            self.skipTest("no TFLite runtime")
        py = PythonNet(d / "weights.json")
        imu = SyntheticIMU(seed=9)
        for label in LABELS:
            series = F.resample(imu.take(label, 6))
            x = F.features(F.cut(series, F.peak_index(series)))
            a, b = tfl.run(x), py.run(x)
            for p, q in zip(a[0], b[0]):
                self.assertAlmostEqual(p, q, places=4)
            self.assertAlmostEqual(a[1], b[1], places=4)


if __name__ == "__main__":
    unittest.main()
