"""Tests: python3 backend/test_backend.py   (starts the brain and backend itself)."""
import asyncio, json, random, subprocess, sys, time, socket
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "brain")); sys.path.insert(0, str(ROOT / "backend"))
import ws
import csv, tempfile, os
import qnx
from gesture import ThresholdClassifier, parse_sample
from game import FlyGame, TICK_HZ

FAIL = []
def check(ok, what):
    print(f"  [{'PASS' if ok else 'FAIL'}] {what}")
    if not ok: FAIL.append(what)

SPOTS = [{"id": f"s{i}", "position": [i * 0.8, 1.0, 2.0], "normal": [0, 1, 0]} for i in range(6)]

def unpack(m):
    """The server sends one "tick" per frame holding the fly, controller, brain and events; tests look at the parts."""
    if m.get("type") != "tick":
        return [m]
    return [m[k] for k in ("fly", "ctl", "brain") if k in m] + m.get("events", [])


def unit_tests():
    print("game logic")
    g = FlyGame(random.Random(1)); g.set_room(SPOTS, [0, 1.6, 0])
    check(g.state == "landed", "starts landed")
    g.swing([0, 1.0, 0.8], [0, 0, 1], 0.8)        # line passes through spot 0 (0,1,2)
    check(g.state == "dead" and g.score == 1, "swing along the line squishes the fly")
    g2 = FlyGame(random.Random(1)); g2.set_room(SPOTS, [0, 1.6, 0])
    check(not g2.swing([0, 1.0, 0], [1, 0, 0], 0.8) and g2.score == 0, "swing the wrong way misses")
    # Directional swings: the swatter sweeps an arc, and the fly is hit anywhere on the swept part.
    g6 = FlyGame(random.Random(1)); g6.set_room(SPOTS, [0, 1.6, 0])
    check(g6.swing([0, 1.0, 0.8], [1, 0, 0], 0.8, swept=[[0.5, 0, 0.5], [0, 0, 1]]) and g6.swings == 1,
          "the fly is hit by a swept direction even when the landing direction misses; counted as one swing")
    g7 = FlyGame(random.Random(1)); g7.set_room(SPOTS, [0, 1.6, 0])
    check(not g7.swing([0, 1.0, 0], [1, 0, 0], 0.8, swept=[[1, 0.2, 0], "x", [0, None, 1], [float("nan"), 0, 1]])
          and g7.swings == 1, "bad swept directions are skipped, not a crash; a sweep away from the fly misses")
    for _ in range(300): g.tick()
    check(g.state != "dead", "fly respawns after being squished")
    g3 = FlyGame(random.Random(2)); g3.set_room(SPOTS, [0, 1.6, 0]); g3.pos = [1.6, 1.0, 2.0]; g3.spot = 2
    g3.set_pose([1.6, 1.6, 1.0], [0, -0.5, 1]); g3.set_pose([1.6, 1.6, 1.0], [0, -0.55, 1])
    v, side = g3.looming()
    check(v > 0, f"swatter near the fly gives looming > 0 ({v:.2f}, {side})")
    g3.on_escape(0.9, "right")
    check(g3.state == "fleeing" and g3.target is not None, "strong escape makes the fly flee")
    g4 = FlyGame(random.Random(2)); g4.set_room(SPOTS); g4.set_pose([0, 1.6, 0], [0, 0, 1])
    g4.on_escape(0.1, "left")
    check(g4.state == "landed", "weak escape is ignored")
    moves = 0
    g5 = FlyGame(random.Random(3)); g5.set_room(SPOTS)
    for _ in range(600):
        s = g5.state; g5.tick()
        if s == "landed" and g5.state == "flying": moves += 1
    check(moves >= 2, f"idle fly hops between spots ({moves} hops in 20 s)")

def regression_tests():
    print("regressions found in review")
    g = FlyGame(random.Random(1)); g.set_room(SPOTS, [0, 1.6, 0])
    check(g.head == [0, 1.6, 0], "room head is kept after set_room")
    # Unity is left-handed: a fly facing +z has +x on its right.
    g = FlyGame(random.Random(1)); g.set_room(SPOTS); g.pos = [1.6, 1.0, 2.0]; g.spot = 2; g.heading = [0, 0, 1]
    g.tip = [2.2, 1.0, 2.0]
    check(g.looming()[1] == "right", "swatter on +x of a fly facing +z is on its right")
    g.tip = [1.0, 1.0, 2.0]
    check(g.looming()[1] == "left", "swatter on -x of a fly facing +z is on its left")
    for side, sign in (("right", 1), ("left", -1)):
        g = FlyGame(random.Random(1)); g.set_room(SPOTS); g.pos = [1.6, 1.0, 2.0]; g.spot = 2
        g.set_pose([1.6, 1.6, 0.8], [0, -0.4, 1], reach=1.0); g.on_escape(0.9, side)   # tip in front of the fly, so it jumps away along +z
        check(g.target is not None and (g.target[0] - g.pos[0]) * sign > 0, f"escape '{side}' jumps to the fly's {side}")
    g = FlyGame(random.Random(1)); g.set_room(SPOTS); g.pos = [1.6, 1.0, 2.0]; g.spot = 2
    g.set_pose([1.6, 1.6, 0.8], [0, -0.4, 1]); g.on_escape(0.9, "right"); first = list(g.target)
    for _ in range(20): g.on_escape(0.9, "right")
    check(g.target == first and g.escapes == 1, "repeated escape replies do not change the target mid-flight")
    for bad in ({"landingSpots": [{"position": [0, None, 2]}]}, {"landingSpots": "x"}):
        g = FlyGame(); g.set_room(bad.get("landingSpots"), [0, 1.6, 0]); g.snapshot()
    ok = True
    for call in (lambda g: g.set_pose([0, 1], [0, 0, 1]), lambda g: g.set_pose([0, 1, float("nan")], [0, 0, 1]),
                 lambda g: g.swing("x", [0, 0, 1], 1)):
        g = FlyGame(); g.set_room(SPOTS, [0, 1.6, 0])
        try: call(g); ok = False
        except ValueError: pass
        g.snapshot()
    check(ok and g.head == [0, 1.6, 0], "bad vectors are rejected and leave the game state intact")

def round_tests():
    print("rounds (3-2-1, timed hunt, won or lost)")
    g = FlyGame(random.Random(1), rounds=True, countdown_s=1.0, round_s=2.0); g.set_room(SPOTS, [0, 1.6, 0])
    check(g.phase == "countdown" and not g.active(), "a round starts with the countdown")
    check(not g.swing([0, 1.0, 0.8], [0, 0, 1], 0.8) and g.swings == 0, "swings are ignored during the countdown")
    g.set_pose([0.8, 1.3, 1.0], [0, -0.3, 1]); g.pos = [0.8, 1.0, 2.0]
    check(g.looming() == (0.0, "center"), "the fly senses nothing during the countdown")
    for _ in range(int(TICK_HZ * 1.1)): g.tick()
    check(g.phase == "playing" and any(e["kind"] == "round_start" for e in g.events), "the hunt starts when the countdown ends")
    for _ in range(TICK_HZ * 3): g.tick()
    check(g.phase == "lost" and any(e.get("result") == "lost" for e in g.events), "time up is a loss")
    check(not g.swing([0, 1.0, 0.8], [0, 0, 1], 0.8), "no swinging after the round")
    g.reset(); check(g.phase == "countdown" and g.spots, "reset starts a new round and keeps the room")
    g = FlyGame(random.Random(1), rounds=True, countdown_s=0.1, round_s=5.0); g.set_room(SPOTS, [0, 1.6, 0])
    for _ in range(10): g.tick()
    check(g.swing([0, 1.0, 0.8], [0, 0, 1], 0.8) and g.phase == "won", "swatting the fly wins the round")
    for _ in range(TICK_HZ * 4): g.tick()
    check(g.state == "dead" and g.phase == "won", "the fly stays dead after a win (no respawn in a round)")
    snap = g.snapshot()
    check(snap["phase"] == "won" and "time_left" in snap and snap["round_seconds"] == 5.0, "snapshot reports the round")

def gesture_tests():
    print("swatter classifier (stand-in for the model)")
    still = ({"t": 0, "ax": 0.1, "ay": -0.2, "az": 9.81, "gx": 0.0, "gy": 0.0, "gz": 0.0})
    check(parse_sample(still)[1] == [0.1, -0.2, 9.81], "a valid sample parses")
    bad = 0
    for s in ({}, {**still, "ax": "x"}, {**still, "gz": float("nan")}, {**still, "ay": True}, [1, 2], None):
        try: parse_sample(s)
        except ValueError: bad += 1
    check(bad == 6, "bad samples are rejected")
    c = ThresholdClassifier()
    ev = []
    for i in range(400): ev += c.update(i * 5, [0.1, -0.2, 9.81], [0.0, 0.0, 0.0])
    check(ev == [], "a swatter lying still reports nothing (gravity is removed)")
    ev = c.update(2000, [0.1, -0.2, 9.81], [6.0, 0.5, 0.0])
    check(len(ev) == 1 and ev[0]["evt"] == "swing" and ev[0]["axis"] == "X" and ev[0]["dir"] == "positive", "a fast rotation is a swing about X")
    check(c.update(2100, [0.1, -0.2, 9.81], [-7.0, 0.0, 0.0]) == [], "a second swing inside the cooldown is ignored")
    ev = c.update(2600, [0.1, -0.2, 9.81], [0.0, -7.0, 0.0])
    check(len(ev) == 1 and ev[0]["axis"] == "Y" and ev[0]["dir"] == "negative", "a swing after the cooldown counts")
    c = ThresholdClassifier()
    for i in range(300): c.update(i * 5, [0, 0, 9.81], [0, 0, 0])
    ev = c.update(1600, [0, 0, 9.81 + 8.0], [0, 0, 0])
    check(len(ev) == 1 and ev[0]["evt"] == "move" and ev[0]["axis"] == "Z" and ev[0]["dir"] == "positive", "a sharp push is a move")
    c = ThresholdClassifier()
    for i in range(300): c.update(i * 5, [0, 0, 9.81], [0, 0, 0])
    ev = c.update(1600, [8.0, 0, 9.81], [5.0, 0, 0])
    check(len(ev) == 1 and ev[0]["evt"] == "swing", "a swing is not also reported as a push")

def qnx_tests():
    print("qnx module")
    lat = qnx.Latency(size=5)
    for v in (1, 2, 3, 4, 100, 6): lat.add(v)
    s = lat.summary()
    check(s["count"] == 6 and s["max_ever"] == 100 and len(lat.samples) == 5, "latency tracker keeps a window and the worst case")
    check(qnx.Latency().summary() == {"count": 0}, "empty latency summary is safe")
    if not qnx.IS_QNX:
        check(qnx.set_realtime(20) == "not changed" and qnx.lock_memory() == "not changed" and qnx.tick_ns() is None,
              "QNX features are no-ops off QNX")
    qnx.log(qnx.SLOG_INFO, "test message", every=60, key="t"); qnx.log(qnx.SLOG_INFO, "suppressed", every=60, key="t")
    check(True, "logging works without slog2")

async def end_to_end():
    print("end to end (brain + backend + fake Unity)")
    py = sys.executable
    brain = subprocess.Popen([py, str(ROOT / "brain/server.py"), "--mode", "fallback", "--port", "18765"],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    back = subprocess.Popen([py, str(ROOT / "backend/server.py"), "--classifier", "threshold", "--practice", "--port", "18766", "--udp-port", "19000",
                             "--brain", "ws://127.0.0.1:18765"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        await asyncio.sleep(2)
        sock = None
        for _ in range(20):
            try: sock = await ws.connect("ws://127.0.0.1:18766"); break
            except OSError: await asyncio.sleep(0.3)
        check(sock is not None, "Unity can connect")
        q = asyncio.Queue()
        async def pump():
            try:
                while True:
                    for part in unpack(json.loads(await sock.recv())): await q.put(part)
            except ws.ConnectionClosed: pass
        pump_task = asyncio.ensure_future(pump())
        hello = await asyncio.wait_for(q.get(), 3); check(hello["type"] == "hello", "server says hello")
        for bad in ('{"type":"room","landingSpots":[{"position":[0,null,2]}],"head":[0,1.6,0]}',
                    '{"type":"pose","head":[0,1],"aim":[0,0,1]}', '[1,2]', '{"type":"swing"}', 'not json', '{"type":"pose"}'):
            await sock.send(bad)
        await sock.send(json.dumps({"type": "room", "landingSpots": SPOTS, "head": [0, 1.6, 0]}))
        fly = None; t0 = time.time(); frames = 0
        while time.time() - t0 < 1.5:
            m = await asyncio.wait_for(q.get(), 2)
            if m["type"] == "fly": fly = m; frames += 1
        check(fly is not None and frames >= 30, f"fly state streams after malformed messages (~{frames/1.5:.0f} Hz)")
        check(back.poll() is None, "backend is still running after malformed messages")
        # Swing the swatter at the fly rapidly and bring the tip close to provoke an escape.
        pos = fly["pos"]; events = []; t0 = time.time(); tip_z = 0.2
        while time.time() - t0 < 4:
            tip_z = 0.2 if tip_z > 1.0 else tip_z + 0.15
            await sock.send(json.dumps({"type": "pose", "head": [pos[0], pos[1] + 0.3, pos[2] - 1.2],
                                        "aim": [0, -0.2, 1], "reach": 1.2 + tip_z}))
            try:
                end = time.time() + 0.05
                while time.time() < end:
                    m = await asyncio.wait_for(q.get(), 0.05)
                    if m["type"] == "event": events.append(m)
                    if m["type"] == "fly": pos = m["pos"]
            except asyncio.TimeoutError: pass
        check(any(e["kind"] == "escape" for e in events), "approaching swatter triggers an escape via the brain")
        await sock.send(json.dumps({"type": "swing", "origin": [pos[0], pos[1], pos[2] - 1], "dir": [0, 0, 1], "strength": 0.9}))
        got = None
        for _ in range(30):
            m = await asyncio.wait_for(q.get(), 1)
            if m["type"] == "swing_result": got = m; break
        check(got is not None and got["hit"], "swing at the fly reports a hit")
        u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        u.sendto(json.dumps({"evt": "swing", "axis": "X", "dir": "positive", "rate": 6.2}).encode(), ("127.0.0.1", 19000))
        sw = None
        for _ in range(60):
            m = await asyncio.wait_for(q.get(), 1)
            if m["type"] == "swatter": sw = m; break
        check(sw is not None and sw["evt"] == "swing", "ESP32 UDP swing is forwarded to Unity")
        await sock.send(json.dumps({"type": "stats"}))
        st = None
        for _ in range(60):
            m = await asyncio.wait_for(q.get(), 1)
            if m["type"] == "stats": st = m; break
        check(st is not None and st["tick_lateness_ms"].get("count", 0) > 30, "stats reports 30 Hz tick timing")
        check(st is not None and st["brain_rtt_ms"].get("count", 0) > 0 and st["brain_connected"], f"stats reports brain round trip {st and st['brain_rtt_ms']}")
        check(st is not None and "scheduling" in st["qnx"], "stats reports the QNX setup")
        pump_task.cancel(); await sock.close()
    finally:
        back.terminate(); brain.terminate()

async def e2e_rounds():
    print("end to end: rounds, stimulus, sound, brain mode")
    py = sys.executable
    brain = subprocess.Popen([py, str(ROOT / "brain/server.py"), "--mode", "auto", "--port", "18775"],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    back = subprocess.Popen([py, str(ROOT / "backend/server.py"), "--classifier", "threshold", "--port", "18776", "--udp-port", "19010",
                             "--countdown-seconds", "1", "--round-seconds", "4", "--brain", "ws://127.0.0.1:18775"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        await asyncio.sleep(2)
        sock = None
        for _ in range(20):
            try: sock = await ws.connect("ws://127.0.0.1:18776"); break
            except OSError: await asyncio.sleep(0.3)
        q = asyncio.Queue()
        async def pump():
            try:
                while True:
                    for part in unpack(json.loads(await sock.recv())): await q.put(part)
            except ws.ConnectionClosed: pass
        task = asyncio.ensure_future(pump())
        async def until(pred, secs):
            end = time.time() + secs
            while time.time() < end:
                try: m = await asyncio.wait_for(q.get(), 0.3)
                except asyncio.TimeoutError: continue
                if pred(m): return m
            return None
        await sock.send(json.dumps({"type": "room", "landingSpots": SPOTS, "head": [0, 1.6, 0]}))
        m = await until(lambda m: m["type"] == "fly", 2)
        check(m is not None and m["phase"] == "countdown", "a round starts in the countdown")
        m = await until(lambda m: m["type"] == "event" and m["kind"] == "round_start", 3)
        check(m is not None, "the backend announces the start of the hunt")
        # Unity's own looming and a sound both reach the brain and make the fly react
        pos = (await until(lambda m: m["type"] == "fly", 2))["pos"]
        got = None
        for _ in range(40):
            await sock.send(json.dumps({"type": "stimulus", "kind": "looming", "value": 1.0, "side": "left"}))
            await sock.send(json.dumps({"type": "stimulus", "kind": "sound", "value": 1.0, "side": "right"}))
            got = await until(lambda m: m["type"] == "event" and m["kind"] == "escape", 0.12)
            if got: break
        check(got is not None, "stimulus from Unity (looming and sound) makes the fly escape")
        for bad in ('{"type":"stimulus"}', '{"type":"stimulus","value":"x"}', '{"type":"stimulus","value":1,"side":"up"}'):
            await sock.send(bad)
        await sock.send(json.dumps({"type": "brain_mode", "mode": "fallback"}))
        await sock.send(json.dumps({"type": "habituation", "enabled": False}))
        m = await until(lambda m: m["type"] == "fly" and m.get("brain") == "fallback", 6)
        check(m is not None, "brain_mode fallback shows up in the fly state (for a failover demo)")
        await sock.send(json.dumps({"type": "brain_mode", "mode": "auto"}))
        m = await until(lambda m: m["type"] == "event" and m["kind"] == "round_end", 8)
        check(m is not None and m["result"] == "lost", "the round ends as a loss when time runs out")
        await sock.send(json.dumps({"type": "reset"}))
        m = await until(lambda m: m["type"] == "fly" and m["phase"] == "countdown", 2)
        check(m is not None, "reset starts a new round")
        check(back.poll() is None, "backend still running after all of that")
        task.cancel(); await sock.close()
    finally:
        back.terminate(); brain.terminate()

async def stream_esp(sock, seconds, swing_at=None, t0=0):
    """Play the ESP32: steady samples at about 200 Hz, with swing bursts at the given offsets (s)."""
    n = int(seconds * 200); swing_at = swing_at or []
    for i in range(n):
        now = i / 200
        gx = 6.0 if any(0 <= now - s < 0.025 for s in swing_at) else 0.0
        await sock.send(json.dumps({"t": t0 + i * 5, "ax": 0.1, "ay": -0.2, "az": 9.81, "gx": gx, "gy": 0.0, "gz": 0.0}))
        await asyncio.sleep(0.005)
    return t0 + n * 5

async def e2e_swatter():
    print("end to end: swatter stream over WebSocket")
    py = sys.executable
    back = subprocess.Popen([py, str(ROOT / "backend/server.py"), "--classifier", "threshold", "--practice", "--port", "18795", "--esp-port", "18796",
                             "--udp-port", "19030", "--brain", "ws://127.0.0.1:18799"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        await asyncio.sleep(1.5)
        unity = None
        for _ in range(20):
            try: unity = await ws.connect("ws://127.0.0.1:18795"); break
            except OSError: await asyncio.sleep(0.3)
        q = asyncio.Queue()
        async def pump():
            try:
                while True:
                    for part in unpack(json.loads(await unity.recv())): await q.put(part)
            except ws.ConnectionClosed: pass
        task = asyncio.ensure_future(pump())
        async def until(pred, secs):
            end = time.time() + secs
            while time.time() < end:
                try: m = await asyncio.wait_for(q.get(), 0.3)
                except asyncio.TimeoutError: continue
                if pred(m): return m
            return None
        m = await until(lambda m: m["type"] == "fly", 2)
        check(m is not None and m.get("swatter") == "never", "before any swatter connects the link reads 'never'")
        esp = await ws.connect("ws://127.0.0.1:18796")
        for bad in ("not json", '{"ax":"a"}', "[1]", '{"t":1}'):
            await esp.send(bad)
        t = await stream_esp(esp, 1.2)
        m = await until(lambda m: m["type"] == "event" and m.get("kind") == "swatter_link" and m["state"] == "ok", 2)
        check(m is not None, "the link comes up once samples arrive")
        t = await stream_esp(esp, 2.0, swing_at=[0.2, 0.3, 1.2], t0=t)
        swings = []
        while not q.empty():
            m = q.get_nowait()
            if m["type"] == "swatter" and m.get("evt") == "swing": swings.append(m)
        check(len(swings) == 2 and swings[0]["axis"] == "X" and swings[0]["dir"] == "positive",
              f"swings reach Unity once each, cooldown respected ({len(swings)} events)")
        await unity.send(json.dumps({"type": "stats"}))
        st = await until(lambda m: m["type"] == "stats", 2)
        check(st is not None and st["swatter"]["bad_samples"] == 4 and 100 < st["swatter"]["samples_per_s"] < 400,
              f"stats show the stream: {st and st['swatter']}")
        m = await until(lambda m: m["type"] == "event" and m.get("kind") == "swatter_link" and m["state"] == "lost", 3)
        check(m is not None, "the link is reported lost when samples stop")
        await stream_esp(esp, 0.8, t0=t + 5000)
        m = await until(lambda m: m["type"] == "event" and m.get("kind") == "swatter_link" and m["state"] == "ok", 3)
        check(m is not None, "the link recovers when samples resume")
        check(back.poll() is None, "backend still running after bad samples")
        task.cancel(); await esp.close(); await unity.close()
    finally:
        back.terminate()

async def collector_test():
    print("data collector")
    py = sys.executable
    out = os.path.join(tempfile.mkdtemp(), "data", "train.csv")
    col = subprocess.Popen([py, str(ROOT / "backend/collect.py"), "--auto", "--port", "18797", "--out", out, "--person", "tester",
                            "--labels", "swing left,idle", "--trials", "2", "--seconds", "0.6"],
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    try:
        await asyncio.sleep(1.0)
        esp = None
        for _ in range(20):
            try: esp = await ws.connect("ws://127.0.0.1:18797"); break
            except OSError: await asyncio.sleep(0.3)
        try: await stream_esp(esp, 4.5)
        except (ws.ConnectionClosed, OSError): pass          # the collector exits when its trials are done
        for _ in range(40):
            if col.poll() is not None: break
            await asyncio.sleep(0.25)
        check(col.poll() == 0, "the collector finishes")
        rows = list(csv.DictReader(open(out)))
        check(list(rows[0].keys()) == ["timestamp", "ax", "ay", "az", "gx", "gy", "gz", "label", "trial_id", "person"], "CSV has the expected columns")
        labels = {r["label"] for r in rows}; trials = {r["trial_id"] for r in rows}
        check(labels == {"swing left", "idle"} and len(trials) == 4 and all(r["person"] == "tester" for r in rows),
              f"4 trials of 2 actions saved ({len(rows)} rows)")
        check(all(abs(float(r["az"]) - 9.81) < 0.01 for r in rows), "samples are stored unchanged")
        try: await esp.close()
        except Exception: pass
    finally:
        if col.poll() is None: col.terminate()

unit_tests()
regression_tests()
round_tests()
gesture_tests()
qnx_tests()
asyncio.run(end_to_end())
asyncio.run(e2e_rounds())
asyncio.run(e2e_swatter())
asyncio.run(collector_test())
print("FAILED: " + ", ".join(FAIL) if FAIL else "all passed"); sys.exit(1 if FAIL else 0)
