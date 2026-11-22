"""WhackAFly game backend. Runs on the QNX Raspberry Pi (Python 3, stdlib only).

  Unity (laptop) --ws:8766-->  this server  --ws:8765--> brain/server.py (fly brain)
  ESP32 swatter  --udp:9000->  this server

Unity sends room landing spots, the player's pose and swings. This server runs
the fly, asks the brain how it escapes, and streams the state back at 30 Hz as one
"tick" message: the fly, the swatter's aim and thumbstick, the newest brain output and
any events. One packet per tick, because the Pi's Wi-Fi falls behind on many small ones.
See backend/README.md for the protocol.
"""
import argparse
import asyncio
import json
import math
import os
import socket
import sys
import time
from collections import deque
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "brain"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import ws  # noqa: E402  (brain/ws.py, stdlib WebSocket)
import qnx  # noqa: E402  (QNX scheduling, slog2 logging, timing)
import flytrace as trace  # noqa: E402  (brain/flytrace.py, FLYAI_TRACE=1 delay hunting)
from gesture import ControllerState, ThresholdClassifier, make_classifier, parse_controller, parse_sample  # noqa: E402
from game import TICK_HZ, SWAT_REACH  # noqa: E402
from fly import AdvancedGame  # noqa: E402

LOOMING_HZ = 20
MAX_CLIENT_BACKLOG = 32 * 1024   # bytes queued for one Unity client, about a second of messages
# Old positions are worthless, so when Wi-Fi falls behind a tick is skipped rather than queued. A small kernel send
# buffer makes the backlog show up here (instead of seconds of it hiding in the network stack), and more than
# STALE_BACKLOG still unsent means the previous tick has not gone out yet.
CLIENT_SNDBUF = 4096
STALE_BACKLOG = 2048
# Unity reports the number of the last message it received ("ack"). A message still unacknowledged after this long
# means Wi-Fi is behind wherever the bytes are queued (kernel, driver, the phone), so ticks are skipped until it
# catches up. A healthy link acks in about 100 ms (the trip, plus Unity acking every 50 ms).
MAX_UNACKED_MS = 250


class Backend:
    def __init__(self, brain_url, rounds=True, countdown_s=3.0, round_s=60.0):
        self.game = AdvancedGame(rounds=rounds, countdown_s=countdown_s, round_s=round_s)
        self.controller = ControllerState()
        self.brain_groups = None               # neuron map for Unity's neural view
        self.brain_url = brain_url
        self.ext_looming = None                # (value, side, time) sent by Unity's own senses
        self.brain_status = {}                 # latest status reply from the brain
        self.classifier = ThresholdClassifier()    # stand-in for the trained model (same interface)
        self.esp_clients, self.esp_count, self.esp_bad = 0, 0, 0
        self.esp_last = None                   # monotonic time of the last good sample
        self.esp_link = "never"                # never, ok or lost
        self.esp_hz = 0.0
        self.clients = set()
        self.brain = None
        self.last_pose = time.monotonic()
        self.qnx_info = {}
        self.tick_lateness = qnx.Latency()     # how late each 30 Hz tick ran, ms
        self.brain_rtt = qnx.Latency()         # looming request to escape reply, ms
        self.sent_at = {}                      # looming request id -> send time
        self.next_id = 0
        self.out_seq = 0                       # numbers every message to Unity
        self.acked = 0                         # the newest of them Unity says it has
        self.ack_from = 0                      # out_seq when Unity connected: acks count only above it
        self.unacked = deque()                 # (seq, monotonic ms) of messages Unity has not acknowledged
        self.brain_out = None                  # newest brain decision, not yet sent to Unity
        self.pending_events = []               # events waiting for the next tick that goes out
        self.skipped = 0                       # ticks skipped because Unity's link was behind

    async def send_all(self, msg):
        if self.clients:
            self.out_seq += 1
            msg = {**msg, "seq": self.out_seq}                         # Unity acks it
            self.unacked.append((self.out_seq, qnx.now_ms()))
            if len(self.unacked) > 1000:                               # a Unity build that never acks
                self.unacked.popleft()
            if trace.ON:
                msg["pt"] = round(trace.now_ms(), 1)                   # so Unity can time the trip
        await self.send_text(json.dumps(msg, separators=(",", ":")), msg.get("type", "?"))

    async def send_text(self, text, kind="?"):
        clients = list(self.clients)
        if len(clients) == 1:
            await self.send_one(clients[0], text, kind)
        elif clients:
            await asyncio.gather(*(self.send_one(c, text, kind) for c in clients))   # A slow client delays nobody else's send.

    async def send_one(self, client, text, kind="?"):
        # Too much unsent data means this client stopped reading: drop it at once instead of waiting on it.
        backlog = client.writer.transport.get_write_buffer_size()
        if backlog > MAX_CLIENT_BACKLOG:
            trace.event(f"TX-DROP {kind} backlog={backlog}B: Unity stopped reading, dropping it")
            self.clients.discard(client)
            client.abort()
            return
        t0 = trace.now_ms() if trace.ON else 0
        try:
            await asyncio.wait_for(client.send(text), 0.25)
        except (ws.ConnectionClosed, asyncio.TimeoutError, OSError) as e:
            trace.event(f"TX-FAIL {kind} {e!r} after {trace.now_ms() - t0:.1f}ms backlog={backlog}B")
            self.clients.discard(client)
            client.abort()
            return
        if trace.ON:
            took = trace.now_ms() - t0
            after = client.writer.transport.get_write_buffer_size()
            trace.count(f"tx_{kind}", len(text))
            trace.peak("tx_backlog_B", after)
            trace.peak("tx_send_ms", took)
            trace.event(f"TX {kind} {len(text)}B send={took:.1f}ms backlog={after}B {trace.short(text, 140)}")

    # ---- Unity connection ----------------------------------------------
    async def on_client(self, sock, path):
        try:
            sock.writer.transport.get_extra_info("socket").setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, CLIENT_SNDBUF)
        except (AttributeError, OSError) as e:
            qnx.log(qnx.SLOG_WARNING, f"could not shrink the send buffer: {e!r}")
        self.clients.add(sock)
        self.acked = self.ack_from = self.out_seq
        self.unacked.clear()
        qnx.log(qnx.SLOG_INFO, f"Unity connected from {sock.peer}")
        await sock.send(json.dumps({"type": "hello", "tick_hz": TICK_HZ,
                                    "brain": self.brain is not None}))
        if self.brain_groups:
            await sock.send(self.brain_groups)
        trace.event(f"UNITY-CONNECT {sock.peer} clients={len(self.clients)}")
        try:
            while True:
                raw = await sock.recv()       # Outside the try: a failed connection is not a bad message.
                try:
                    msg = json.loads(raw)
                    if trace.ON and isinstance(msg, dict):
                        kind = msg.get("type", "?")
                        trace.count(f"rx_{kind}", len(raw))
                        # The room is many KB of spots; its size is enough.
                        trace.event(f"RX {kind} {len(raw)}B " + ("" if kind == "room" else trace.short(raw)))
                    ack = msg.get("ack") if isinstance(msg, dict) else None
                    if isinstance(ack, int) and not isinstance(ack, bool) and self.acked < ack <= self.out_seq:
                        self.acked = ack
                    if isinstance(msg, dict):
                        t0 = trace.now_ms() if trace.ON else 0
                        await self.handle(msg)
                        if trace.ON:
                            trace.peak("rx_handle_ms", trace.now_ms() - t0)
                except json.JSONDecodeError:
                    continue
                except (ValueError, KeyError, TypeError, AttributeError) as e:
                    print(f"[backend] ignored bad message: {e!r}")
        except ws.ConnectionClosed as e:
            trace.event(f"UNITY-CLOSED {e!r}")
        finally:
            self.clients.discard(sock)
            sock.abort()
            qnx.log(qnx.SLOG_INFO, "Unity disconnected")
            trace.event(f"UNITY-DISCONNECT clients={len(self.clients)}")

    async def send_brain(self, msg, track=False):
        """Send to the brain if connected. `track` gives it an id so the round trip is timed."""
        if self.brain is None:
            return False
        if track:
            self.next_id += 1
            self.sent_at[self.next_id] = qnx.now_ms()
            if len(self.sent_at) > 200:                    # unanswered (superseded) requests
                for k in sorted(self.sent_at)[:100]: self.sent_at.pop(k, None)
            msg = {**msg, "id": self.next_id}
        try:
            await self.brain.send(json.dumps(msg))
            return True
        except ws.ConnectionClosed:
            self.brain = None
            return False

    async def handle(self, m):
        t = m.get("type")
        if t == "room":
            fresh = self.game.set_room(m.get("landingSpots", []), m.get("head"), m.get("air"), m.get("links"),
                                       m.get("spotAir"), m.get("ceiling"), m.get("resume") is True, m.get("playing") is True,
                                       m.get("hold") is True)
            if fresh:
                await self.send_brain({"type": "reset"})    # a new room is a new round: clear the fly's senses
                self.ext_looming = None
            qnx.log(qnx.SLOG_INFO, f"room loaded: {len(self.game.spots)} landing spots")
        elif t == "pose":
            now = time.monotonic()
            self.game.set_pose(m["head"], m["aim"], m.get("reach", SWAT_REACH),
                               max(now - self.last_pose, 1e-3))
            self.last_pose = now
        elif t == "swing":
            hit = self.game.swing(m["origin"], m["dir"], m.get("strength", 0.6),
                                  m.get("reach", SWAT_REACH), m.get("dirs"), m.get("threat") is True)
            await self.send_all({"type": "swing_result", "hit": hit, "score": self.game.score})
        elif t == "hunt":                                    # Unity's 3-2-1 is over
            self.game.set_difficulty(m.get("level", 1))
            self.game.roll_personality()
            self.game.start_hunt()
            await self.send_brain({"type": "reset"})
            self.ext_looming = None
        elif t == "swat":                                    # Unity saw the swatter land on the fly
            self.game.swat()
        elif t == "senses":
            self.game.set_senses(m)
        elif t == "reset":
            self.game.reset()
            if m.get("start"):
                self.game.start_hunt()                      # Unity ran its own countdown; the hunt starts now
            await self.send_brain({"type": "reset"})        # clears the brain's senses and habituation
            self.ext_looming = None
        elif t == "difficulty":
            self.game.set_difficulty(m.get("level", 1))
        elif t == "stimulus":
            # What Unity's own senses (lighting, flashlight, shadows, noise) say the fly perceives.
            value, side = float(m["value"]), m.get("side", "center")
            if not math.isfinite(value) or side not in ("left", "right", "center"):
                raise ValueError("bad stimulus")
            value = max(0.0, min(1.0, value))
            if not self.game.active():
                pass
            elif m.get("kind", "looming") == "sound":
                self.game.hear(value, side)
            else:
                self.ext_looming = (value, side, time.monotonic())
        elif t == "brain_mode":                              # live / fallback / auto, e.g. for a failover demo
            if m.get("mode") in ("auto", "live", "fallback"):
                await self.send_brain({"type": "mode", "mode": m["mode"]})
                await self.send_brain({"type": "status"})
        elif t == "habituation":
            if isinstance(m.get("enabled"), bool):
                await self.send_brain({"type": "habituation", "enabled": m["enabled"]})
        elif t == "ping":
            await self.send_all({"type": "pong"})
        elif t == "tping":                                   # trace clock sync: Unity times the round trip
            await self.send_all({"type": "tpong", "ut": m.get("ut")})
        elif t == "stats":
            await self.send_all(self.stats())

    def stats(self):
        """Deadline evidence: how steady the 30 Hz loop and the brain round trip are."""
        return {"type": "stats", "qnx": self.qnx_info,
                "tick_period_ms": round(1000 / TICK_HZ, 2),
                "tick_lateness_ms": self.tick_lateness.summary(),
                "brain_rtt_ms": self.brain_rtt.summary(),
                "swatter": {"link": self.esp_link, "samples_per_s": round(self.esp_hz, 1),
                            "connections": self.esp_clients, "bad_samples": self.esp_bad,
                            "classifier": self.classifier.name},
                "brain_connected": self.brain is not None,
                "brain_source": self.brain_status.get("source"),
                "brain_mode": self.brain_status.get("mode"),
                "round": {"phase": self.game.phase, "time_left": round(max(0.0, self.game.phase_time), 1),
                          "rounds": self.game.rounds}}

    # ---- ESP32 sample stream over WebSocket -------------------------------
    async def on_esp32(self, sock, path):
        """The swatter streams IMU samples here; each one goes through the classifier."""
        self.esp_clients += 1
        qnx.log(qnx.SLOG_INFO, f"swatter connected from {sock.peer}")
        trace.event(f"ESP-CONNECT {sock.peer}")
        last_arrival, last_t = None, None
        try:
            while True:
                raw = await sock.recv()
                try:
                    sample = json.loads(raw)
                    t, accel, gyro = parse_sample(sample)
                except (ValueError, TypeError, RecursionError):
                    self.esp_bad += 1                # a bad sample is dropped, never fatal
                    trace.event(f"ESP-BAD {trace.short(str(raw), 80)}")
                    continue
                if trace.ON:
                    # A gap then a burst means Wi-Fi held samples back; the board's own clock (t) shows when they were taken.
                    arrival = trace.now_ms()
                    trace.count("esp_sample", len(raw))
                    if last_arrival is not None:
                        gap = arrival - last_arrival
                        trace.peak("esp_gap_ms", gap)
                        if gap > 30:
                            board_gap = (t - last_t) if (t is not None and last_t is not None) else -1
                            trace.event(f"ESP-GAP {gap:.0f}ms (board clock {board_gap:.0f}ms) {trace.short(str(raw), 120)}")
                    last_arrival, last_t = arrival, t
                self.esp_last = time.monotonic()
                self.esp_count += 1
                self.controller.update(t if t is not None else qnx.now_ms(), gyro, *parse_controller(sample), accel=accel)
                t0 = trace.now_ms() if trace.ON else 0
                actions = list(self.classifier.update(t if t is not None else qnx.now_ms(), accel, gyro))
                if trace.ON:
                    trace.peak("classifier_ms", trace.now_ms() - t0)
                for action in actions:
                    trace.event(f"SWATTER-ACTION {action}")
                    await self.send_all({"type": "swatter", **action})
        except ws.ConnectionClosed as e:
            trace.event(f"ESP-CLOSED {e!r}")
        finally:
            self.esp_clients -= 1
            qnx.log(qnx.SLOG_INFO, "swatter disconnected")
            trace.event("ESP-DISCONNECT")

    async def link_loop(self):
        """Watchdog for the swatter: no samples for half a second means the link is lost."""
        last_count, last_time = 0, time.monotonic()
        while True:
            await asyncio.sleep(0.25)
            now = time.monotonic()
            self.esp_hz = (self.esp_count - last_count) / max(now - last_time, 1e-3)
            last_count, last_time = self.esp_count, now
            if self.esp_last is None:
                state = "never"
            else:
                state = "ok" if now - self.esp_last < 0.5 else "lost"
            if state != self.esp_link:
                if state == "lost":
                    qnx.log(qnx.SLOG_WARNING, "swatter link lost (no samples for 0.5 s)")
                elif state == "ok":
                    qnx.log(qnx.SLOG_INFO, "swatter link up")
                    self.classifier.reset()          # the gravity estimate is stale after a gap
                    self.controller.reset()
                self.esp_link = state
                trace.event(f"ESP-LINK {state} ({self.esp_hz:.0f} samples/s)")
                await self.send_all({"type": "event", "kind": "swatter_link", "state": state})

    # ---- ESP32 over UDP -------------------------------------------------
    def udp_protocol(self):
        backend = self

        class Proto(asyncio.DatagramProtocol):
            def datagram_received(self, data, addr):
                try:
                    m = json.loads(data.decode())
                except (ValueError, UnicodeDecodeError):
                    return
                if not isinstance(m, dict):
                    return
                m["type"] = "swatter"       # forwarded to Unity unchanged otherwise
                asyncio.ensure_future(backend.send_all(m))
        return Proto()

    # ---- brain ----------------------------------------------------------
    async def brain_loop(self):
        while True:
            sock = None
            try:
                sock = await ws.connect(self.brain_url)
                self.brain = sock
                qnx.log(qnx.SLOG_INFO, f"brain connected at {self.brain_url}")
                await self.send_brain({"type": "status"})
                while True:
                    raw = await sock.recv()
                    m = json.loads(raw)
                    if trace.ON and isinstance(m, dict):
                        kind = m.get("type", "?")
                        trace.count(f"brain_rx_{kind}", len(raw))
                        if kind == "escape":
                            sent = self.sent_at.get(m.get("id"))
                            trace.event(f"BRAIN-RX escape id={m.get('id')} {len(raw)}B "
                                        f"rtt={(trace.now_ms() - sent) if sent else -1:.1f}ms strength={m.get('strength')} "
                                        f"side={m.get('side')} spikes={len(m.get('spikes') or [])} active={len(m.get('active') or [])}")
                        else:
                            trace.event(f"BRAIN-RX {kind} {len(raw)}B")
                    if isinstance(m, dict) and m.get("type") in ("status", "mode_ok"):
                        was = self.brain_status.get("source")
                        self.brain_status = {k: m.get(k) for k in ("mode", "source", "live_available", "live_healthy", "switches") if k in m}
                        if m.get("type") == "status":
                            if m.get("neuron_groups") and m.get("group_names") and not self.brain_groups:
                                self.brain_groups = json.dumps({"type": "brain_groups", "neuron_groups": m["neuron_groups"],
                                                                "group_names": m["group_names"]}, separators=(",", ":"))
                                await self.send_text(self.brain_groups)
                            e = m.get("efficacy") or {}
                            await self.send_all({"type": "brain_status", "mode": m.get("mode"), "source": m.get("source"),
                                                 "neurons": m.get("neurons", 0),
                                                 "efficacy": [e.get(k, 1.0) for k in ("eye_left", "eye_right", "ear_left", "ear_right")]})
                        if was and m.get("source") and was != m["source"]:
                            qnx.log(qnx.SLOG_WARNING, f"brain switched from {was} to {m['source']}")
                    if isinstance(m, dict) and m.get("type") == "escape":
                        sent = self.sent_at.pop(m.get("id"), None)
                        if sent is not None:
                            rtt = qnx.now_ms() - sent
                            self.brain_rtt.add(rtt)
                            if rtt > 100:
                                qnx.log(qnx.SLOG_WARNING, f"slow brain reply: {rtt:.0f} ms", every=5, key="slow-brain")
                        self.game.spikes = m.get("spikes")
                        self.game.on_escape(float(m.get("strength", 0.0)), m.get("side", "right"))
                        # Goes out with the next tick; a newer decision replaces one not yet sent.
                        self.brain_out = {"type": "brain", "strength": m.get("strength", 0.0), "side": m.get("side", "right"),
                                          "spikes": m.get("spikes") or [], "active": m.get("active") or []}
            except Exception as e:      # a brain problem must never stop the game
                if not isinstance(e, (ws.ConnectionClosed, ConnectionError, OSError, asyncio.TimeoutError)):
                    qnx.log(qnx.SLOG_ERROR, f"brain error: {e!r}", every=5, key="brain-error")
            finally:
                if self.brain is not None:
                    qnx.log(qnx.SLOG_WARNING, "brain disconnected, retrying", every=5, key="brain-down")
                self.brain = None
                self.brain_status = {}
                self.sent_at.clear()
                if sock is not None:
                    await sock.close()
            await asyncio.sleep(2)

    async def looming_loop(self):
        n = 0
        while True:
            await asyncio.sleep(1 / LOOMING_HZ)
            n += 1
            try:
                value, side = self.game.looming()
                ext = self.ext_looming
                origin = "pi"
                if ext is not None and time.monotonic() - ext[2] < 0.3 and self.game.active():
                    value, side = ext[0], ext[1]               # Unity's senses win while they keep arriving
                    origin = "unity"
                if n % (LOOMING_HZ * 2) == 0:                  # refresh live/fallback status every 2 s
                    await self.send_brain({"type": "status"})
                if self.brain is not None:
                    loud, ear = self.game.sound()                # sent together, so the brain makes one decision
                    await self.send_brain({"type": "sound", "value": loud, "side": ear})
                    await self.send_brain({"type": "looming", "value": value, "side": side}, track=True)
                    if trace.ON:
                        # What the fly perceives this step, and from what: the light Unity reported feeds `vision` here.
                        senses = {k: round(float(v), 3) for k, v in self.game.senses.items() if isinstance(v, (int, float))}
                        trace.count("brain_tx_looming")
                        trace.event(f"BRAIN-TX id={self.next_id} looming={value:.3f} side={side} from={origin} "
                                    f"cause={self.game.source} sound={loud:.3f}/{ear} senses={senses}")
                elif value > 0.6:
                    # No brain: crude fallback so the game still plays.
                    self.game.on_escape(min(1.0, value), "left" if side == "right" else "right")
            except ws.ConnectionClosed:
                self.brain = None
            except Exception as e:
                qnx.log(qnx.SLOG_ERROR, f"looming loop error: {e!r}", every=5, key="looming-error")

    async def tick_loop(self):
        period = 1 / TICK_HZ
        nxt = time.monotonic()
        while True:
            nxt += period
            try:
                t0 = trace.now_ms() if trace.ON else 0
                self.game.tick(period)
                if trace.ON:
                    trace.peak("fly_tick_ms", trace.now_ms() - t0)
                self.pending_events.extend({"type": "event", **e} for e in self.game.events)
                self.game.events.clear()
                if self.clients:
                    await self.send_tick()
                else:
                    self.pending_events.clear()
            except Exception as e:
                qnx.log(qnx.SLOG_ERROR, f"tick error: {e!r}", every=5, key="tick-error")
            await asyncio.sleep(max(0.0, nxt - time.monotonic()))
            late_ms = (time.monotonic() - nxt) * 1000
            self.tick_lateness.add(max(0.0, late_ms))
            if trace.ON:
                trace.peak("tick_late_ms", late_ms)
                if late_ms > 15:
                    trace.event(f"LATE fly tick {late_ms:.0f}ms")
            if late_ms > 1.5 * period * 1000:                   # missed a deadline by half a period
                qnx.log(qnx.SLOG_WARNING, f"fly tick {late_ms:.0f} ms late", every=5, key="late-tick")
                nxt = time.monotonic()

    async def send_tick(self):
        """Everything Unity needs this tick, in one message. Skipped while the previous one is still unsent: events
        wait for the next tick, the aim keeps adding up, and only stale positions are lost."""
        backlog = max(c.writer.transport.get_write_buffer_size() for c in self.clients)
        while self.unacked and self.unacked[0][0] <= self.acked:
            self.unacked.popleft()
        # How long the oldest message has gone unacknowledged; 0 until Unity acks at all (older builds never do).
        waiting = qnx.now_ms() - self.unacked[0][1] if self.unacked and self.acked > self.ack_from else 0.0
        trace.peak("unacked_ms", waiting)
        if (backlog > STALE_BACKLOG or waiting > MAX_UNACKED_MS) and not self.pending_events:
            self.skipped += 1
            trace.count("tick_skipped")
            trace.event(f"SKIP tick: {backlog}B unsent, oldest unacknowledged {waiting:.0f}ms")
            return
        snap = self.game.snapshot()
        snap["brain"] = self.brain_status.get("source")        # "live", "fallback" or None
        snap["swatter"] = self.esp_link                        # "never", "ok" or "lost"
        snap["classifier"] = self.classifier.name
        tick = {"type": "tick", "fly": snap}
        if self.esp_link == "ok":
            tick["ctl"] = self.controller.take()
        if self.brain_out is not None:
            tick["brain"], self.brain_out = self.brain_out, None
        if self.pending_events:
            tick["events"], self.pending_events = self.pending_events, []
        await self.send_all(tick)

    async def trace_loop(self):
        """FLYAI_TRACE only. Every 5 ms it checks how late it woke: a long late wake means something held the whole
        event loop (every message waits behind it). Once a second it writes the summary."""
        step, second = 0.005, time.monotonic() + 1.0
        while True:
            t0 = time.monotonic()
            await asyncio.sleep(step)
            stall = (time.monotonic() - t0 - step) * 1000
            trace.peak("loop_stall_ms", stall)
            if stall > 25:
                trace.event(f"STALL event loop held {stall:.0f}ms")
            if time.monotonic() >= second:
                second += 1.0
                backlogs = [c.writer.transport.get_write_buffer_size() for c in list(self.clients)]
                trace.flush_second(f"unity={len(self.clients)} backlog={backlogs} swatter={self.esp_link} "
                                   f"{self.esp_hz:.0f}/s brain={'up' if self.brain else 'down'} "
                                   f"phase={self.game.phase} fly={self.game.state} looming={self.game.loom:.2f} "
                                   f"cause={self.game.source} senses={ {k: round(float(v), 2) for k, v in self.game.senses.items() if isinstance(v, (int, float))} }")


async def main():
    p = argparse.ArgumentParser()
    p.add_argument("--port", type=int, default=8766, help="WebSocket port for Unity")
    p.add_argument("--udp-port", type=int, default=9000, help="UDP port for ESP32 events")
    p.add_argument("--esp-port", type=int, default=8767, help="WebSocket port for the ESP32 sample stream")
    p.add_argument("--brain", default="ws://127.0.0.1:8765", help="brain server URL")
    p.add_argument("--host", default="0.0.0.0")
    p.add_argument("--practice", action="store_true", help="free play: no countdown or timer, the fly respawns")
    p.add_argument("--round-seconds", type=float, default=60.0, help="length of the hunt")
    p.add_argument("--countdown-seconds", type=float, default=3.0, help="length of the 3-2-1 countdown")
    p.add_argument("--rt-priority", type=int, default=20, help="QNX real-time priority, 0 to leave unchanged")
    p.add_argument("--classifier", default=os.environ.get("SWATTER_CLASSIFIER", "model"),
                   choices=["threshold", "model"], help="swing detection: the trained model (default) or the firmware's rules")
    p.add_argument("--model", default=os.environ.get("SWATTER_MODEL"), help="model folder (default swatter_ml/models/latest)")
    a = p.parse_args()
    b = Backend(a.brain, rounds=not a.practice, countdown_s=a.countdown_seconds, round_s=a.round_seconds)
    b.classifier, why = make_classifier(a.classifier, a.model)
    qnx.log(qnx.SLOG_WARNING if why else qnx.SLOG_NOTICE, why or f"swing classifier: {b.classifier.name}")
    b.qnx_info = qnx.startup(a.rt_priority)
    where = trace.start("backend")
    if where:
        qnx.log(qnx.SLOG_NOTICE, f"trace on: {where}")
    await ws.serve(b.on_client, a.host, a.port)
    await ws.serve(b.on_esp32, a.host, a.esp_port)
    loop = asyncio.get_running_loop()
    await loop.create_datagram_endpoint(b.udp_protocol, local_addr=(a.host, a.udp_port))
    qnx.log(qnx.SLOG_NOTICE, f"listening: Unity ws://{a.host}:{a.port}  ESP32 stream ws://{a.host}:{a.esp_port}  ESP32 events udp://{a.host}:{a.udp_port}  brain {a.brain}")
    loops = [b.brain_loop(), b.looming_loop(), b.tick_loop(), b.link_loop()]
    if trace.ON:
        loops.append(b.trace_loop())
    await asyncio.gather(*loops)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
