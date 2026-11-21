"""Tests: python3 backend/test_backend.py   (starts the brain and backend itself)."""
import asyncio, json, random, subprocess, sys, time, socket
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "brain")); sys.path.insert(0, str(ROOT / "backend"))
import ws
import qnx
from game import FlyGame

FAIL = []
def check(ok, what):
    print(f"  [{'PASS' if ok else 'FAIL'}] {what}")
    if not ok: FAIL.append(what)

SPOTS = [{"id": f"s{i}", "position": [i * 0.8, 1.0, 2.0], "normal": [0, 1, 0]} for i in range(6)]

def unit_tests():
    print("game logic")
    g = FlyGame(random.Random(1)); g.set_room(SPOTS, [0, 1.6, 0])
    check(g.state == "landed", "starts landed")
    g.swing([0, 1.0, 0.8], [0, 0, 1], 0.8)        # line passes through spot 0 (0,1,2)
    check(g.state == "dead" and g.score == 1, "swing along the line squishes the fly")
    g2 = FlyGame(random.Random(1)); g2.set_room(SPOTS, [0, 1.6, 0])
    check(not g2.swing([0, 1.0, 0], [1, 0, 0], 0.8) and g2.score == 0, "swing the wrong way misses")
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
    back = subprocess.Popen([py, str(ROOT / "backend/server.py"), "--port", "18766", "--udp-port", "19000",
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
                while True: await q.put(json.loads(await sock.recv()))
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

unit_tests()
regression_tests()
qnx_tests()
asyncio.run(end_to_end())
print("FAILED: " + ", ".join(FAIL) if FAIL else "all passed"); sys.exit(1 if FAIL else 0)
