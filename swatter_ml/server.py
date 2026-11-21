"""Recording and training UI for the swatter's swing classifier.

    python -m swatter_ml.server                     open http://127.0.0.1:8090
    python -m swatter_ml.server --source synthetic  start on the synthetic swatter

The ESP32 connects to ws://<this laptop's IP>:8767 (any path), the same stream format the game
backend takes: {"t","ax","ay","az","gx","gy","gz"} about 200 times a second. Sources:
  live       the ESP32 (always listening; shown when selected)
  synthetic  a simulated swatter that performs the gesture you are recording
  replay     a take, a folder of takes or a collect.py CSV, played back at real speed
Takes are saved in swatter_ml/data/takes/ (one JSON file each). Needs aiohttp.
"""
import argparse
import asyncio
import json
import os
import random
import subprocess
import sys
import time
from collections import deque
from pathlib import Path

from aiohttp import WSMsgType, web

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
    __package__ = "swatter_ml"

from swatter_ml import features as F  # noqa: E402
from swatter_ml import takes as T  # noqa: E402
from swatter_ml.synthetic import SyntheticIMU  # noqa: E402

ROOT = Path(__file__).resolve().parent
STATIC = ROOT / "static"
MODEL_DIR = ROOT / "models" / "latest"
NO_PEAK_LABELS = ("idle", "aiming")   # burst mode cuts these at a fixed rhythm instead of at peaks
PRE_ROLL_S = 0.4                       # samples kept from before the record window
FIELDS = ("ax", "ay", "az", "gx", "gy", "gz")


def parse_sample(m):
    vals = []
    for k in FIELDS:
        v = m.get(k)
        if isinstance(v, bool) or not isinstance(v, (int, float)) or v != v or abs(v) > 1e6:
            raise ValueError(k)
        vals.append(float(v))
    t = m.get("t")
    return (float(t) if isinstance(t, (int, float)) and not isinstance(t, bool) else None), vals


class Hub:
    def __init__(self, takes_dir):
        self.takes_dir = Path(takes_dir)
        self.source = "synthetic"
        self.ring = deque(maxlen=F.SAMPLE_RATE_HZ * 30)    # raw [t, ax..gz] of the active source
        self.pending = []                                   # rows not yet sent to the browsers
        self.ui = set()
        self.esp = {}                                       # peer -> last sample time
        self.live_count = 0
        self.rate_times = deque(maxlen=400)
        self.synth = SyntheticIMU(seed=None, random_gestures=True)
        self.replay = None                                  # {"rows", "i", "name", "loop", "back_to"}
        self.rec = {"state": "idle"}                        # idle | countdown | recording
        self.rec_rows = None
        self.last_saved = []
        self.model = None
        self.model_error = None
        self.preds = deque(maxlen=20)
        self.train = {"running": False, "log": "", "code": None}
        self.load_model()

    # ---- model --------------------------------------------------------------------------
    def load_model(self):
        self.model, self.model_error = None, None
        if not (MODEL_DIR / "meta.json").exists():
            self.model_error = "no model yet: record takes, then Train"
            return
        try:
            from swatter_ml.infer import ModelClassifier
            self.model = ModelClassifier(MODEL_DIR)
            self.model.on_prediction = self.on_prediction
        except Exception as e:
            self.model_error = f"model not loaded: {e}"

    def on_prediction(self, p):
        p = dict(p, wall=time.time())
        self.preds.appendleft(p)
        self.broadcast({"type": "pred", "pred": p})

    # ---- samples ------------------------------------------------------------------------
    def on_sample(self, t, vals, source):
        if source != self.source:
            return
        if t is None:
            t = time.monotonic() * 1000.0
        row = [t, *vals]
        self.ring.append(row)
        self.pending.append(row)
        self.rate_times.append(time.monotonic())
        if self.rec_rows is not None:
            self.rec_rows.append(row)
        if self.model:
            try:
                self.model.update(t, vals[:3], vals[3:])
            except Exception as e:
                self.model_error = f"model error: {e}"
                self.model = None

    def rate(self):
        ts = self.rate_times
        if len(ts) < 2 or time.monotonic() - ts[-1] > 0.5:
            return 0.0
        return round((len(ts) - 1) / (ts[-1] - ts[0]), 1) if ts[-1] > ts[0] else 0.0

    def set_source(self, kind, path=None, loop=True):
        if kind == "replay":
            if not path:
                raise ValueError("replay needs a file or folder")
            tk = T.load_all([path])
            rows = []
            for t in tk:          # concatenate takes on one clock, 0.3 s apart
                if not t["samples"]:
                    continue
                off = (rows[-1][0] + 300 - t["samples"][0][0]) if rows else 0
                rows += [[r[0] + off, *r[1:7]] for r in t["samples"]]
            if not rows:
                raise ValueError("nothing to replay there")
            back = self.source if self.source != "replay" else (self.replay or {}).get("back_to", "synthetic")
            self.replay = {"rows": rows, "i": 0, "name": Path(path).name, "loop": loop, "back_to": back}
        elif kind not in ("live", "synthetic"):
            raise ValueError("unknown source")
        self.source = kind
        self.ring.clear()
        self.rate_times.clear()
        if self.model:
            self.model.reset()

    # ---- browsers -----------------------------------------------------------------------
    def broadcast(self, msg):
        data = json.dumps(msg)
        for ws in list(self.ui):
            if not ws.closed:
                asyncio.ensure_future(ws.send_str(data))

    def counts(self):
        c = {}
        for f in self.takes_dir.glob("*.json"):
            try:
                label = json.loads(f.read_text()).get("label", "?")
            except (OSError, ValueError):
                continue
            c[label] = c.get(label, 0) + 1
        return c

    def status(self):
        return {"type": "status", "source": self.source, "rate": self.rate(),
                "live_connected": [p for p, ts in self.esp.items() if time.monotonic() - ts < 2.0],
                "replay": self.replay and {"name": self.replay["name"], "i": self.replay["i"], "n": len(self.replay["rows"])},
                "rec": self.rec, "model": bool(self.model), "model_error": self.model_error,
                "model_backend": self.model.net.kind if self.model else None,
                "training": self.train["running"]}

    # ---- recording ----------------------------------------------------------------------
    async def record(self, label, strength, person, seconds, mode, countdown):
        if self.rec["state"] != "idle":
            raise ValueError("already recording")
        try:
            for n in range(int(countdown), 0, -1):
                self.rec = {"state": "countdown", "n": n, "label": label}
                self.broadcast({"type": "status", **self.status()})
                await asyncio.sleep(1.0)
            src = self.source
            if src == "synthetic":
                self.synth.random_gestures = False
                self.synth.clear()
                if label != "idle":
                    if mode == "single":
                        self.synth.perform(label, strength, delay_s=max(0.2, min(seconds - 0.6, 0.3 + 0.4 * seconds * random.random())))
                    else:
                        k = 0.0
                        while k < seconds - 0.7:
                            k += random.uniform(0.7, 1.3)
                            self.synth.perform(label, max(0, min(10, strength + random.uniform(-1, 1))), delay_s=k)
            pre = [r for r in self.ring if self.ring and r[0] >= self.ring[-1][0] - PRE_ROLL_S * 1000]
            self.rec_rows = []
            self.rec = {"state": "recording", "label": label, "until": time.time() + seconds, "seconds": seconds}
            self.broadcast(self.status())
            await asyncio.sleep(seconds)
            rows, self.rec_rows = pre + self.rec_rows, None
            if src == "synthetic":
                self.synth.random_gestures = True
            if len(rows) < F.WINDOW // 2:
                raise ValueError(f"only {len(rows)} samples arrived: is the source running?")
            dev = {"live": ",".join(self.esp) or "esp32", "replay": (self.replay or {}).get("name", "")}.get(src, "")
            saved = []
            if mode == "single":
                saved.append(T.make_take(rows, label, strength, person, "single", src, dev))
            else:
                saved = self.burst_cut(rows, label, strength, person, src, dev)
            for t in saved:
                T.save(t, self.takes_dir)
            self.last_saved = [t["id"] for t in saved]
            return [T.summary(t) for t in saved]
        finally:
            self.rec_rows = None
            self.rec = {"state": "idle"}
            self.broadcast(self.status())

    def burst_cut(self, rows, label, strength, person, src, dev):
        """One take per motion peak (the same Segmenter the model uses live); for idle and
        aiming, one take every 0.5 s. Each take keeps the raw rows around its window."""
        series_t = []      # resampled timestamps, to map windows back to raw rows
        r = F.Resampler()
        seg = F.Segmenter()
        spans = []
        step = int(0.5 * F.SAMPLE_RATE_HZ)
        n = 0
        for row in rows:
            out, restarted = r.push(row[0], row[1:7])
            if restarted:
                seg.reset()
            for s in out:
                t_res = r.next_t - F.DT_MS
                series_t.append(t_res)
                if label in NO_PEAK_LABELS:
                    if n >= F.WINDOW and (n - F.WINDOW) % step == 0:
                        spans.append((t_res - (F.WINDOW - 1) * F.DT_MS, t_res))
                elif seg.push(s) is not None:
                    spans.append((t_res - (F.WINDOW - 1) * F.DT_MS, t_res))
                n += 1
        out = []
        margin = 10 * F.DT_MS
        for i, (a, b) in enumerate(spans):
            part = [row for row in rows if a - margin <= row[0] <= b + margin]
            if len(part) >= F.WINDOW // 2:
                t = T.make_take(part, label, strength, person, "burst", src, dev)
                t["id"] += f"-{i:02d}"
                out.append(t)
        return out

    # ---- training -----------------------------------------------------------------------
    async def run_training(self, extra):
        if self.train["running"]:
            raise ValueError("training is already running")
        self.train = {"running": True, "log": "", "code": None}
        cmd = [sys.executable, "-u", "-m", "swatter_ml.train", "--data", str(self.takes_dir), *extra]
        self.train["log"] = "$ " + " ".join(cmd[2:]) + "\n"
        try:
            env = dict(os.environ, TF_CPP_MIN_LOG_LEVEL="2", PYTHONWARNINGS="ignore")
            proc = await asyncio.create_subprocess_exec(*cmd, cwd=str(ROOT.parent), stdout=subprocess.PIPE,
                                                        stderr=subprocess.STDOUT, env=env)
            while True:
                line = await proc.stdout.readline()
                if not line:
                    break
                text = line.decode("utf-8", "replace")
                if any(k in text for k in ("oneDNN", "cpu_feature_guard", "W0000 ", "I0000 ", "XNNPACK")):
                    continue
                self.train["log"] += text
                self.broadcast({"type": "train_log", "text": text})
            self.train["code"] = await proc.wait()
        finally:
            self.train["running"] = False
            self.load_model()
            self.broadcast({**self.status(), "type": "train_done", "code": self.train["code"]})


# ---- sources ------------------------------------------------------------------------------
async def synthetic_loop(hub):
    """200 Hz in batches of 4 samples every 20 ms, on the simulated device clock."""
    nxt = time.perf_counter()
    while True:
        nxt += 0.02
        for _ in range(4):
            t, v = hub.synth.next()
            hub.on_sample(t, v, "synthetic")
        await asyncio.sleep(max(0.0, nxt - time.perf_counter()))
        if time.perf_counter() - nxt > 0.5:
            nxt = time.perf_counter()


async def replay_loop(hub):
    nxt = time.perf_counter()
    while True:
        nxt += 0.02
        rp = hub.replay
        if hub.source == "replay" and rp:
            rows = rp["rows"]
            if rp["i"] >= len(rows):
                if rp["loop"]:
                    rp["i"] = 0
                    rp["t_off"] = rp.get("t_off", 0) + rows[-1][0] - rows[0][0] + 300
                else:
                    hub.set_source(rp["back_to"])
                    hub.broadcast(hub.status())
                    continue
            # play on the recording's own clock, so the rate and any gaps are as recorded
            if rp.get("clock") is None or rp["i"] == 0:
                rp["clock"] = rows[rp["i"]][0]
            rp["clock"] += 20
            while rp["i"] < len(rows) and rows[rp["i"]][0] < rp["clock"]:
                r = rows[rp["i"]]
                hub.on_sample(r[0] + rp.get("t_off", 0), r[1:7], "replay")
                rp["i"] += 1
        await asyncio.sleep(max(0.0, nxt - time.perf_counter()))
        if time.perf_counter() - nxt > 0.5:
            nxt = time.perf_counter()


async def pump(hub):
    """Batched samples to the browsers at 25 Hz, status at 4 Hz."""
    k = 0
    while True:
        await asyncio.sleep(0.04)
        if hub.pending and hub.ui:
            hub.broadcast({"type": "samples", "rows": [[round(v, 4) for v in r] for r in hub.pending]})
        hub.pending = []
        k += 1
        if k % 6 == 0:
            hub.broadcast(hub.status())


# ---- web ----------------------------------------------------------------------------------
def make_app(hub):
    routes = web.RouteTableDef()

    @routes.get("/")
    async def index(req):
        return web.FileResponse(STATIC / "index.html")

    @routes.get("/ui")
    async def ui(req):
        ws = web.WebSocketResponse(heartbeat=20)
        await ws.prepare(req)
        hub.ui.add(ws)
        await ws.send_str(json.dumps(hub.status()))
        try:
            async for _ in ws:
                pass
        finally:
            hub.ui.discard(ws)
        return ws

    def err(e, code=400):
        return web.json_response({"error": str(e)}, status=code)

    @routes.get("/api/state")
    async def state(req):
        return web.json_response({**hub.status(), "counts": hub.counts(), "spec": F.spec(),
                                  "preds": list(hub.preds), "train_log": hub.train["log"],
                                  "takes_dir": str(hub.takes_dir)})

    @routes.post("/api/source")
    async def source(req):
        b = await req.json()
        try:
            hub.set_source(b.get("kind"), b.get("path"), bool(b.get("loop", True)))
        except (ValueError, OSError) as e:
            return err(e)
        hub.broadcast(hub.status())
        return web.json_response(hub.status())

    @routes.post("/api/record")
    async def record(req):
        b = await req.json()
        label = str(b.get("label", "")).strip()
        if not label:
            return err("pick a label")
        try:
            strength = float(b.get("strength", 0))
            seconds = min(max(float(b.get("seconds", 1.5)), 0.5), 120.0)
            countdown = min(max(int(b.get("countdown", 3)), 0), 10)
            saved = await hub.record(label, strength, str(b.get("person", "")), seconds,
                                     "burst" if b.get("mode") == "burst" else "single", countdown)
        except ValueError as e:
            return err(e)
        return web.json_response({"saved": saved, "counts": hub.counts()})

    @routes.get("/api/takes")
    async def list_takes(req):
        out = []
        for f in sorted(hub.takes_dir.glob("*.json"), reverse=True):
            try:
                out.append(T.summary(json.loads(f.read_text())))
            except (OSError, ValueError):
                continue
        return web.json_response({"takes": out, "counts": hub.counts()})

    @routes.get("/api/takes/{id}")
    async def get_take(req):
        try:
            t = T.load(req.match_info["id"], hub.takes_dir)
        except (ValueError, OSError) as e:
            return err(e, 404)
        series = F.resample(t["samples"])
        p = F.peak_index(series) if series else 0
        t0 = t["samples"][0][0] if t["samples"] else 0
        # the resampled run starts at its first raw sample's time (the longest continuous run)
        info = {"peak_index": p, "n_resampled": len(series),
                "window_start_ms": (p - F.PRE_PEAK) * F.DT_MS, "window_end_ms": (p + F.POST_PEAK) * F.DT_MS,
                "peak_ms": p * F.DT_MS, "t0": t0}
        if hub.model and series:
            from swatter_ml.infer import predict_window
            info["prediction"] = predict_window(hub.model.net, hub.model.labels, F.cut(series, p))
        return web.json_response({"take": t, "window": info})

    @routes.delete("/api/takes/{id}")
    async def del_take(req):
        try:
            ok = T.delete(req.match_info["id"], hub.takes_dir)
        except ValueError as e:
            return err(e)
        return web.json_response({"deleted": ok, "counts": hub.counts()})

    @routes.post("/api/takes/{id}")
    async def edit_take(req):
        b = await req.json()
        try:
            t = T.update(req.match_info["id"], hub.takes_dir, label=b.get("label"), strength=b.get("strength"))
        except (ValueError, OSError) as e:
            return err(e)
        return web.json_response({"take": T.summary(t), "counts": hub.counts()})

    @routes.post("/api/delete_last")
    async def delete_last(req):
        n = sum(T.delete(i, hub.takes_dir) for i in hub.last_saved)
        hub.last_saved = []
        return web.json_response({"deleted": n, "counts": hub.counts()})

    @routes.post("/api/play/{id}")
    async def play(req):
        try:
            p = T.path_for(req.match_info["id"], hub.takes_dir)
            hub.set_source("replay", str(p), loop=False)
        except (ValueError, OSError) as e:
            return err(e)
        return web.json_response(hub.status())

    @routes.post("/api/train")
    async def train(req):
        b = await req.json() if req.can_read_body else {}
        extra = []
        if b.get("holdout_person"):
            extra += ["--holdout-person", str(b["holdout_person"])]
        if b.get("epochs"):
            extra += ["--epochs", str(int(b["epochs"]))]
        if hub.train["running"]:
            return err("training is already running")
        asyncio.ensure_future(hub.run_training(extra))
        return web.json_response({"started": True})

    @routes.get("/api/model")
    async def model(req):
        try:
            return web.json_response(json.loads((MODEL_DIR / "meta.json").read_text()))
        except OSError:
            return err("no model", 404)

    app = web.Application()
    app.add_routes(routes)
    return app


def make_esp_app(hub):
    async def esp(req):
        ws = web.WebSocketResponse(heartbeat=None, max_msg_size=1 << 16)
        await ws.prepare(req)
        peer = req.remote or "?"
        hub.esp[peer] = time.monotonic()
        print(f"swatter connected from {peer}")
        async for msg in ws:
            if msg.type != WSMsgType.TEXT:
                continue
            try:
                t, vals = parse_sample(json.loads(msg.data))
            except (ValueError, TypeError, AttributeError):
                continue
            hub.esp[peer] = time.monotonic()
            hub.live_count += 1
            hub.on_sample(t, vals, "live")
        hub.esp.pop(peer, None)
        print(f"swatter {peer} disconnected")
        return ws

    app = web.Application()
    app.router.add_get("/{tail:.*}", esp)
    return app


async def amain(a):
    hub = Hub(a.takes)
    if a.source == "replay":
        hub.set_source("replay", a.replay)
    else:
        hub.source = a.source
    runners = []
    for app, host, port in ((make_app(hub), a.host, a.port), (make_esp_app(hub), "0.0.0.0", a.esp_port)):
        r = web.AppRunner(app)
        await r.setup()
        await web.TCPSite(r, host, port).start()
        runners.append(r)
    asyncio.ensure_future(synthetic_loop(hub))
    asyncio.ensure_future(replay_loop(hub))
    asyncio.ensure_future(pump(hub))
    print(f"Swing trainer UI:  http://{'127.0.0.1' if a.host in ('0.0.0.0', '') else a.host}:{a.port}")
    print(f"ESP32 stream:      ws://<this machine's IP>:{a.esp_port}   (source now: {hub.source})")
    print(f"Takes folder:      {hub.takes_dir}")
    await asyncio.Event().wait()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1", help="UI address (0.0.0.0 to open it from a phone)")
    ap.add_argument("--port", type=int, default=8090, help="UI port")
    ap.add_argument("--esp-port", type=int, default=8767, help="WebSocket port the ESP32 streams to")
    ap.add_argument("--source", default="synthetic", choices=["live", "synthetic", "replay"])
    ap.add_argument("--replay", help="file or folder for --source replay")
    ap.add_argument("--takes", default=str(T.TAKES_DIR), help="where takes are saved")
    a = ap.parse_args()
    try:
        asyncio.run(amain(a))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
