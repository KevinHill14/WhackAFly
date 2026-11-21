"""Fly brain server: JSON over WebSocket.

  in : {"type":"looming","value":0-1,"side":"left"|"right"|"center"}
  out: {"type":"escape","strength":0-1,"side":"left"|"right","spikes":[counts]}

Extras (all optional):
  {"type":"ping"}   -> {"type":"pong"}                 (health / latency)
  {"type":"status"} -> {"type":"status", mode, source, group_names, ...}
  {"type":"reset"}  -> brain state cleared (new round), {"type":"reset_ok"}
  {"type":"mode","mode":"live"|"fallback"|"auto"} -> switch at runtime
  Any request may carry "id"; the reply echoes it (for round-trip timing).

Brains:
  live     - the connectome LIF simulation (brain.py), in its own worker thread.
  fallback - precomputed lookup from a sweep of the live brain (fallback.py,
             stdlib only). Same interface, same reply shape.
  auto     - (default) live, with a watchdog: a live decision that misses its
             deadline is answered from the fallback, FAIL_N misses in a row
             switch to fallback, RECOVER_N on-time live answers switch back.
             If NumPy / the subnetwork can't load at all, runs on fallback.

The asyncio loop only parses and queues. Looming requests are coalesced per
connection: if a client sends faster than the brain decides, only its newest
value is simulated (the superseded request gets no reply), so latency can't
pile up.

Run:
  python brain/server.py                         # listen on 0.0.0.0:8765
  python brain/server.py --hub ws://HUB:PORT/x   # also dial out to the hub (auto-reconnect)
  python brain/server.py --mode fallback         # lookup only
"""
import argparse
import asyncio
import json
import math
import sys
import threading
import time
from collections import OrderedDict, deque
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import ws  # noqa: E402

SIDES = ("left", "right", "center")
MODES = ("live", "fallback", "auto")
FAIL_N = 2          # consecutive live misses -> fallback
RECOVER_N = 5       # consecutive on-time live answers -> back to live
SUPERSEDED = object()


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def rss_mb():
    """Resident memory of this process in MB (Linux/QNX/macOS/Windows), or None."""
    try:
        with open("/proc/self/statm") as f:                 # Linux
            import os
            return int(f.read().split()[1]) * os.sysconf("SC_PAGE_SIZE") / 2**20
    except (OSError, ValueError, AttributeError):
        pass
    if sys.platform == "win32":
        import ctypes
        from ctypes import wintypes

        class PMC(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                        ("PeakWorkingSetSize", ctypes.c_size_t),
                        ("WorkingSetSize", ctypes.c_size_t),
                        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                        ("QuotaPagedPoolUsage", ctypes.c_size_t),
                        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                        ("PagefileUsage", ctypes.c_size_t),
                        ("PeakPagefileUsage", ctypes.c_size_t)]
        c = PMC()
        c.cb = ctypes.sizeof(c)
        k32 = ctypes.WinDLL("kernel32")
        k32.GetCurrentProcess.restype = wintypes.HANDLE
        gpmi = k32.K32GetProcessMemoryInfo
        gpmi.argtypes = [wintypes.HANDLE, ctypes.POINTER(PMC), wintypes.DWORD]
        gpmi.restype = wintypes.BOOL
        if gpmi(k32.GetCurrentProcess(), ctypes.byref(c), c.cb):
            return c.WorkingSetSize / 2**20
        return None
    try:
        import resource                                     # QNX/macOS: peak RSS
        r = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
        return r / 2**20 if sys.platform == "darwin" else r / 1024
    except (ImportError, OSError):
        return None


class BrainWorker(threading.Thread):
    """Owns the live FlyBrain; runs decisions off the event loop."""

    def __init__(self, brain, spin_ms=0.0):
        super().__init__(daemon=True, name="brain")
        self.brain = brain
        self.spin_s = spin_ms / 1000.0
        self.cond = threading.Condition()
        self.pending = OrderedDict()        # conn key -> (job, deliver)
        self.running = True
        self.decisions = 0
        self.coalesced = 0
        self.recent = deque(maxlen=200)
        self.last_ms = 0.0

    def submit(self, key, job, deliver):
        with self.cond:
            old = self.pending.pop(key, None)
            self.pending[key] = (job, deliver)
            self.cond.notify()
        if old:
            self.coalesced += 1
            old[1](SUPERSEDED)

    def drop(self, key):
        with self.cond:
            self.pending.pop(key, None)

    def stop(self):
        with self.cond:
            self.running = False
            self.cond.notify()

    def _spin(self):
        """Stay busy briefly waiting for the next request. When the core
        idles between 20 Hz requests the OS drops its clock and the next
        decision takes ~2x longer; spinning keeps it hot. sleep(0) yields the
        GIL every pass, so the event loop isn't starved."""
        deadline = time.monotonic() + self.spin_s
        while not self.pending and self.running and time.monotonic() < deadline:
            time.sleep(0)

    def run(self):
        while True:
            if self.spin_s:
                self._spin()
            with self.cond:
                while self.running and not self.pending:
                    self.cond.wait()
                if not self.running:
                    return
                _, (job, deliver) = self.pending.popitem(last=False)
            t0 = time.perf_counter()
            try:
                op = job["op"]
                if op == "reset":
                    self.brain.reset()
                    out = {"type": "reset_ok"}
                elif op == "sync":      # resume from the fallback's last output
                    self.brain.drive_ema = job["strength"] * self.brain.full_drive
                    self.brain.jump = job["side"]
                    out = {"type": "sync_ok"}
                elif op == "stall":                     # --debug only
                    time.sleep(job["ms"] / 1000.0)
                    out = {"type": "stall_ok"}
                else:
                    r = self.brain.decide(job["value"], job["side"])
                    out = {"type": "escape", "strength": r["strength"],
                           "side": r["side"], "spikes": r["spikes"]}
                    if "active" in r:
                        out["active"] = r["active"]
                    self.decisions += 1
                    dt = time.perf_counter() - t0
                    self.last_ms = dt * 1000
                    self.recent.append(dt)
            except Exception as e:  # never let one bad step kill the thread
                log("brain error:", repr(e))
                out = {"type": "error", "error": f"brain: {e}"}
            deliver(out)

    def avg_step_ms(self):
        """Mean of the last 200 real decisions (not resets/stalls; robust to
        one giant step if the laptop slept mid-decision)."""
        r = sorted(self.recent)
        if not r:
            return 0.0
        r = r[: max(1, int(len(r) * 0.99))]       # drop the top 1%
        return 1000 * sum(r) / len(r)


class Request:
    __slots__ = ("t0", "answered", "timer", "value", "side", "rid", "outbox", "probe")

    def __init__(self, value, side, rid, outbox, probe):
        self.t0 = time.perf_counter()
        self.answered = False
        self.timer = None
        self.value, self.side, self.rid = value, side, rid
        self.outbox, self.probe = outbox, probe


class BrainServer:
    def __init__(self, live=None, fallback=None, mode="auto", spin_ms=0.0,
                 timeout_ms=200.0, debug=False):
        if live is None and fallback is None:
            raise SystemExit("no brain available: neither live nor fallback loaded")
        self.live = live
        self.fallback = fallback
        self.worker = BrainWorker(live, spin_ms) if live else None
        self.mode = mode
        self.timeout_s = timeout_ms / 1000.0
        self.debug = debug
        self.healthy = live is not None
        self.ok_streak = 0
        self.miss_streak = 0
        self.live_misses = 0
        self.fallback_replies = 0
        self.switches = 0
        self.started = time.monotonic()
        self.clients = 0
        self.inflight = {}          # conn key -> newest unanswered live Request
        names = (live or fallback).group_names
        if live and fallback and list(fallback.group_names) != list(live.group_names):
            log("WARNING: lookup table groups differ from the live subnetwork; "
                "rebuild it with build_lookup.py")
        self.group_names = names

    # --- which brain answers -------------------------------------------------
    def source(self):
        if self.mode == "fallback" and self.fallback:
            return "fallback"
        if self.live is None:
            return "fallback"
        if self.mode == "live" or self.fallback is None:
            return "live"
        return "live" if self.healthy else "fallback"

    def _set_healthy(self, ok, why):
        if ok != self.healthy:
            self.healthy = ok
            self.switches += 1
            log(f"live brain {'recovered' if ok else 'NOT RESPONDING'} ({why}); "
                f"now answering from {self.source()}")

    def status(self):
        w = self.worker
        st = {"type": "status", "mode": self.mode, "source": self.source(),
              "live_available": self.live is not None,
              "fallback_available": self.fallback is not None,
              "live_healthy": self.healthy, "switches": self.switches,
              "live_misses": self.live_misses, "fallback_replies": self.fallback_replies,
              "group_names": self.group_names, "decision_ms": 50.0,
              "clients": self.clients, "rss_mb": round(rss_mb() or -1, 1),
              "uptime_s": round(time.monotonic() - self.started, 1)}
        if self.live:
            st.update(subnet=self.live.path.name, neurons=self.live.n,
                      threshold=self.live.threshold, max_rate_hz=self.live.max_rate_hz,
                      decisions=w.decisions, coalesced=w.coalesced,
                      last_step_ms=round(w.last_ms, 2),
                      avg_step_ms=round(w.avg_step_ms(), 2),
                      neuron_groups=self.live.group_id.tolist())   # group index of every neuron (for the game's brain view)
        return st

    # --- answering -----------------------------------------------------------
    def _fallback_reply(self, req):
        out = self.fallback.decide(req.value, req.side)
        self.fallback_replies += 1
        return out

    def _send(self, req, out):
        req.answered = True
        if req.timer:
            req.timer.cancel()
        if req.rid is not None:
            out = {**out, "id": req.rid}
        req.outbox.put_nowait(out)

    def _live_done(self, req, out):
        """Event-loop thread: a live decision finished (or was superseded)."""
        if out is SUPERSEDED:       # its deadline already moved to the newer request
            return
        on_time = (time.perf_counter() - req.t0) <= self.timeout_s
        if out.get("type") == "escape" and on_time:
            self.miss_streak = 0
            self.ok_streak += 1
            if self.fallback:
                self.fallback.sync(out["strength"], out["side"])
            if not self.healthy and self.ok_streak >= RECOVER_N:
                self._set_healthy(True, f"{RECOVER_N} on-time decisions")
        elif out.get("type") == "escape":
            self.ok_streak = 0
        else:
            self._miss(f"error: {out.get('error')}")
        if req.answered or req.probe:
            return
        if out.get("type") == "error" and self.fallback:
            self._send(req, self._fallback_reply(req))
        else:
            self._send(req, out)

    def _miss(self, why):
        self.live_misses += 1
        self.ok_streak = 0
        self.miss_streak += 1
        if self.miss_streak >= FAIL_N:
            self._set_healthy(False, why)

    def _deadline(self, req):
        if req.answered:
            return
        self._miss(f"no answer in {self.timeout_s * 1000:.0f} ms")
        if self.fallback:
            self._send(req, self._fallback_reply(req))
        # Without a fallback (mode live, or no table) the reply just comes late.

    def looming(self, key, value, side, rid, outbox, loop):
        src = self.source()
        use_live_worker = self.live is not None and self.mode != "fallback"
        req = Request(value, side, rid, outbox, probe=(src == "fallback"))
        prev = self.inflight.get(key)
        if prev is not None and not prev.answered and not prev.probe:
            # Superseded before the brain got to it: no reply for prev, but the
            # newer request inherits prev's deadline. Otherwise a 20 Hz client
            # would keep pushing the deadline back and a stalled brain would
            # never be noticed.
            prev.answered = True
            if prev.timer:
                prev.timer.cancel()
            req.t0 = prev.t0
        if src == "fallback":
            self._send(req, self._fallback_reply(req))
        else:
            self.inflight[key] = req
            if self.fallback and self.mode == "auto":
                left = self.timeout_s - (time.perf_counter() - req.t0)
                req.timer = loop.call_later(max(0.0, left), self._deadline, req)
        if use_live_worker:
            # In fallback-due-to-failure the live brain still gets the request
            # as a probe, so we notice when it recovers. Coalescing keeps a
            # hung brain from accumulating a queue.
            self.worker.submit(key, {"op": "looming", "value": value, "side": side},
                               lambda out: loop.call_soon_threadsafe(self._live_done, req, out))

    # --- connection handling -------------------------------------------------
    async def handle(self, sock, path="/"):
        loop = asyncio.get_running_loop()
        outbox = asyncio.Queue()
        key = object()
        self.clients += 1
        log(f"client connected {sock.peer} {path} ({self.clients} total)")

        async def sender():
            while True:
                msg = await outbox.get()
                await sock.send(json.dumps(msg, separators=(",", ":")))

        send_task = asyncio.ensure_future(sender())
        try:
            while True:
                raw = await sock.recv()
                reply = self.on_message(raw, key, outbox, loop)
                if reply is not None:
                    outbox.put_nowait(reply)
        except ws.ConnectionClosed:
            pass
        finally:
            if self.worker:
                self.worker.drop(key)
            req = self.inflight.pop(key, None)
            if req and req.timer:
                req.timer.cancel()
            send_task.cancel()
            self.clients -= 1
            log(f"client left {sock.peer} ({self.clients} total)")

    def on_message(self, raw, key, outbox, loop):
        """Parse one message. Returns an immediate reply, or None if queued."""
        try:
            msg = json.loads(raw)
            if not isinstance(msg, dict):
                raise ValueError("not an object")
        except ValueError:
            return {"type": "error", "error": "bad json"}
        t = msg.get("type")
        rid = msg.get("id")
        tag = {"id": rid} if "id" in msg else {}
        if t == "looming":
            try:
                v = float(msg.get("value", 0.0))
            except (TypeError, ValueError):
                return {"type": "error", "error": "value must be a number", **tag}
            if math.isnan(v) or math.isinf(v):
                return {"type": "error", "error": "value must be finite", **tag}
            side = msg.get("side", "center")
            if side not in SIDES:
                side = "center"
            self.looming(key, min(1.0, max(0.0, v)), side, rid if "id" in msg else None,
                         outbox, loop)
            return None
        if t == "reset":
            if self.fallback:
                self.fallback.reset()
            if self.worker and self.source() == "live":
                def done(out):
                    if out is not SUPERSEDED:
                        loop.call_soon_threadsafe(outbox.put_nowait, {**out, **tag})
                self.worker.submit(key, {"op": "reset"}, done)
                return None
            if self.worker:      # unhealthy: reset without waiting on it
                self.worker.submit(object(), {"op": "reset"}, lambda out: None)
            return {"type": "reset_ok", **tag}
        if t == "mode":
            m = msg.get("mode")
            if m not in MODES:
                return {"type": "error", "error": f"mode must be one of {MODES}", **tag}
            if m != self.mode:
                log(f"mode {self.mode} -> {m}")
            before = self.source()
            self.mode = m
            if before == "fallback" and self.source() == "live" and self.fallback:
                # The live brain wasn't stepped while on fallback; pick up where
                # the fallback left off so strength doesn't jump.
                self.worker.submit(object(), {"op": "sync", "strength": self.fallback.strength,
                                              "side": self.fallback.jump}, lambda out: None)
            return {"type": "mode_ok", "mode": m, "source": self.source(), **tag}
        if t == "ping":
            return {"type": "pong", **tag}
        if t == "status":
            return {**self.status(), **tag}
        if t == "debug_stall" and self.debug and self.worker:
            self.worker.submit(object(), {"op": "stall", "ms": float(msg.get("ms", 1000))},
                               lambda out: None)
            return {"type": "stall_queued", **tag}
        if t in ("hello", "escape"):    # hub chatter we don't need to answer
            return None
        return {"type": "error", "error": f"unknown type {t!r}", **tag}

    async def hub_link(self, url):
        """Keep a client connection to the hub open, reconnecting with backoff."""
        delay = 0.5
        while True:
            try:
                sock = await ws.connect(url)
                log(f"connected to hub {url}")
                delay = 0.5
                await sock.send(json.dumps({"type": "hello", "role": "brain",
                                            "group_names": self.group_names}))
                await self.handle(sock, "hub")
                log("hub connection closed")
            except (OSError, ConnectionError, asyncio.TimeoutError, ValueError) as e:
                log(f"hub unreachable ({e.__class__.__name__}); retry in {delay:.1f}s")
            await asyncio.sleep(delay)
            delay = min(2.0, delay * 2)


def load_brains(args):
    live = fallback = None
    if args.mode != "fallback":
        try:
            from brain import FlyBrain      # needs NumPy
            live = FlyBrain(args.subnet, seed=args.seed)
            live.decide(0.0, "center")      # warm up NumPy
            live.reset()
            log(f"live brain: {live.path.name}, {live.n} neurons, "
                f"max rate {live.max_rate_hz} Hz")
        except Exception as e:
            log(f"live brain unavailable ({e.__class__.__name__}: {e}); using fallback")
    try:
        from fallback import FallbackBrain
        fallback = FallbackBrain(args.lookup, seed=args.seed)
        log(f"fallback: {fallback.path.name} (built from {fallback.table['subnet']})")
    except Exception as e:
        log(f"fallback unavailable ({e.__class__.__name__}: {e})")
    return live, fallback


async def amain(args):
    live, fallback = load_brains(args)
    srv = BrainServer(live, fallback, args.mode, args.spin_ms, args.timeout_ms, args.debug)
    if srv.worker:
        srv.worker.start()
    log(f"mode {srv.mode}, answering from {srv.source()}")
    tasks = []
    if not args.no_listen:
        server = await ws.serve(srv.handle, args.host, args.port)
        log(f"listening on ws://{args.host}:{args.port}")
        tasks.append(asyncio.ensure_future(server.serve_forever()))
    if args.hub:
        tasks.append(asyncio.ensure_future(srv.hub_link(args.hub)))
    if not tasks:
        raise SystemExit("nothing to do: give --hub or drop --no-listen")
    try:
        await asyncio.gather(*tasks)
    finally:
        if srv.worker:
            srv.worker.stop()


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--mode", choices=MODES, default="auto")
    ap.add_argument("--subnet", default=None, help="subnet .npz (default subnet_t5)")
    ap.add_argument("--lookup", default=None, help="fallback table (default subnet/lookup.json)")
    ap.add_argument("--hub", default=None, help="ws:// URL of the hub to dial out to")
    ap.add_argument("--no-listen", action="store_true", help="only dial out to the hub")
    ap.add_argument("--timeout-ms", type=float, default=200.0,
                    help="auto mode: live deadline before the fallback answers")
    ap.add_argument("--seed", type=int, default=None)
    ap.add_argument("--spin-ms", type=float, default=100.0,
                    help="busy-wait this long for the next request to keep the CPU "
                         "clock up (0 = off; costs one core while the game is sending)")
    ap.add_argument("--debug", action="store_true",
                    help="enable {'type':'debug_stall','ms':N} to test failover")
    args = ap.parse_args()
    # Let the event-loop thread grab the GIL quickly while the brain computes.
    sys.setswitchinterval(0.001)
    try:
        asyncio.run(amain(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
