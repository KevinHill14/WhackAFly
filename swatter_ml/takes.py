"""Recorded takes on disk. Standard library only.

One take = one labelled gesture = one JSON file in swatter_ml/data/takes/ (git-ignored):
    {"format": "whackafly-take/1", "id", "label", "strength" (0-10), "person", "created" (ISO time),
     "mode" ("single", "burst", "import"), "device" {"source", "peer"}, "nominal_rate_hz": 200,
     "sample_rate_hz" (measured), "columns": ["t","ax","ay","az","gx","gy","gz"],
     "samples": [[t_ms, ax, ay, az, gx, gy, gz], ...]}
The raw samples are kept; the window is cut again at training time by features.window_from_rows,
so changing the preprocessing never needs re-recording.

CSV files from backend/collect.py or pi_server/collect_training_data.py
(timestamp, ax, ay, az, gx, gy, gz, label, trial_id[, person]) load too: one take per trial_id.
"""
import csv
import datetime as _dt
import json
import os
import re
from pathlib import Path

from . import features as F

ROOT = Path(__file__).resolve().parent
TAKES_DIR = ROOT / "data" / "takes"
FORMAT = "whackafly-take/1"
COLUMNS = ["t", "ax", "ay", "az", "gx", "gy", "gz"]
_ID_OK = re.compile(r"^[A-Za-z0-9_.-]{1,80}$")


def measured_rate(rows):
    if len(rows) < 2 or rows[-1][0] <= rows[0][0]:
        return None
    return round((len(rows) - 1) * 1000.0 / (rows[-1][0] - rows[0][0]), 1)


def new_id(label):
    now = _dt.datetime.now()
    return f"{now:%Y%m%d-%H%M%S}-{now.microsecond // 1000:03d}-{re.sub('[^a-z0-9]+', '_', label.lower())}"


def make_take(rows, label, strength, person="", mode="single", source="", peer="", take_id=None):
    rows = [[float(r[0])] + [round(float(v), 5) for v in r[1:7]] for r in rows]
    t = {"format": FORMAT, "id": take_id or new_id(label), "label": label,
         "strength": float(strength), "person": person, "created": _dt.datetime.now().isoformat(timespec="seconds"),
         "mode": mode, "device": {"source": source, "peer": peer},
         "nominal_rate_hz": F.SAMPLE_RATE_HZ, "sample_rate_hz": measured_rate(rows),
         "columns": COLUMNS, "samples": rows}
    return t


def save(take, folder=TAKES_DIR):
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f"{take['id']}.json"
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(take, separators=(",", ":")))
    os.replace(tmp, path)
    return path


def path_for(take_id, folder=TAKES_DIR):
    if not _ID_OK.match(take_id or ""):
        raise ValueError("bad take id")
    return Path(folder) / f"{take_id}.json"


def load(take_id, folder=TAKES_DIR):
    return json.loads(path_for(take_id, folder).read_text())


def delete(take_id, folder=TAKES_DIR):
    p = path_for(take_id, folder)
    if p.exists():
        p.unlink()
        return True
    return False


def update(take_id, folder=TAKES_DIR, **fields):
    t = load(take_id, folder)
    for k in ("label", "strength", "person"):
        if k in fields and fields[k] is not None:
            t[k] = float(fields[k]) if k == "strength" else str(fields[k])
    save(t, folder)
    return t


def load_csv(path, person=""):
    """Takes from a collect.py CSV, one per trial_id."""
    groups = {}
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            try:
                row = [float(r["timestamp"])] + [float(r[k]) for k in ("ax", "ay", "az", "gx", "gy", "gz")]
            except (KeyError, TypeError, ValueError):
                continue
            key = (r.get("trial_id", ""), r.get("label", ""))
            g = groups.setdefault(key, {"rows": [], "person": r.get("person") or person})
            g["rows"].append(row)
    out = []
    for (trial, label), g in groups.items():
        tid = f"csv-{Path(path).stem}-{trial}"
        t = make_take(g["rows"], label, 0.0, g["person"], "import", "csv", str(path), take_id=re.sub("[^A-Za-z0-9_.-]", "_", tid))
        out.append(t)
    return out


def load_all(sources=(TAKES_DIR,)):
    """Every take from folders of take JSON files, single JSON files and collect.py CSVs."""
    out = []
    for src in sources:
        p = Path(src)
        if p.is_dir():
            for f in sorted(p.glob("*.json")):
                try:
                    out.append(json.loads(f.read_text()))
                except (OSError, ValueError):
                    print(f"skipping unreadable {f}")
            for f in sorted(p.glob("*.csv")):
                out.extend(load_csv(f))
        elif p.suffix.lower() == ".csv":
            out.extend(load_csv(p))
        elif p.exists():
            out.append(json.loads(p.read_text()))
        else:
            print(f"not found: {p}")
    return out


def summary(take):
    rows = take.get("samples", [])
    return {k: take.get(k) for k in ("id", "label", "strength", "person", "created", "mode", "sample_rate_hz")} | {
        "n": len(rows), "seconds": round((rows[-1][0] - rows[0][0]) / 1000.0, 2) if len(rows) > 1 else 0,
        "source": take.get("device", {}).get("source", "")}
