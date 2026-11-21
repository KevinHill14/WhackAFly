"""WhackAFly game backend. Runs on the QNX Raspberry Pi (Python 3, stdlib only).

  Unity (laptop) --ws:8766-->  this server  --ws:8765--> brain/server.py (fly brain)
  ESP32 swatter  --udp:9000->  this server

Unity sends room landing spots, the player's pose and swings. This server runs
the fly, asks the brain how it escapes, and streams the fly state back at 30 Hz.
See backend/README.md for the protocol.
"""
import argparse
import asyncio
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "brain"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import ws  # noqa: E402  (brain/ws.py, stdlib WebSocket)
from game import FlyGame, TICK_HZ, SWAT_REACH  # noqa: E402

LOOMING_HZ = 20


class Backend:
    def __init__(self, brain_url):
        self.game = FlyGame()
        self.brain_url = brain_url
        self.clients = set()
        self.brain = None
        self.last_pose = time.monotonic()

    async def send_all(self, msg):
        text = json.dumps(msg, separators=(",", ":"))
        for c in list(self.clients):
            try:
                # A stalled client must not hold up the game for everyone else.
                await asyncio.wait_for(c.send(text), 0.25)
            except (ws.ConnectionClosed, asyncio.TimeoutError, OSError):
                self.clients.discard(c)
                await c.close()

    # ---- Unity connection ----------------------------------------------
    async def on_client(self, sock, path):
        self.clients.add(sock)
        print(f"[backend] Unity connected from {sock.peer}")
        await sock.send(json.dumps({"type": "hello", "tick_hz": TICK_HZ,
                                    "brain": self.brain is not None}))
        try:
            while True:
                try:
                    msg = json.loads(await sock.recv())
                    if isinstance(msg, dict):
                        await self.handle(msg)
                except json.JSONDecodeError:
                    continue
                except (ValueError, KeyError, TypeError, AttributeError) as e:
                    print(f"[backend] ignored bad message: {e!r}")
        except ws.ConnectionClosed:
            pass
        finally:
            self.clients.discard(sock)
            print("[backend] Unity disconnected")

    async def handle(self, m):
        t = m.get("type")
        if t == "room":
            self.game.set_room(m.get("landingSpots", []), m.get("head"))
            print(f"[backend] room loaded: {len(self.game.spots)} landing spots")
        elif t == "pose":
            now = time.monotonic()
            self.game.set_pose(m["head"], m["aim"], m.get("reach", SWAT_REACH),
                               max(now - self.last_pose, 1e-3))
            self.last_pose = now
        elif t == "swing":
            hit = self.game.swing(m["origin"], m["dir"], m.get("strength", 0.6),
                                  m.get("reach", SWAT_REACH))
            await self.send_all({"type": "swing_result", "hit": hit, "score": self.game.score})
        elif t == "reset":
            self.game.reset()
        elif t == "ping":
            await self.send_all({"type": "pong"})

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
                print(f"[backend] brain connected at {self.brain_url}")
                while True:
                    m = json.loads(await sock.recv())
                    if isinstance(m, dict) and m.get("type") == "escape":
                        self.game.spikes = m.get("spikes")
                        self.game.on_escape(float(m.get("strength", 0.0)), m.get("side", "right"))
            except Exception as e:      # a brain problem must never stop the game
                if not isinstance(e, (ws.ConnectionClosed, ConnectionError, OSError, asyncio.TimeoutError)):
                    print(f"[backend] brain error: {e!r}")
            finally:
                self.brain = None
                if sock is not None:
                    await sock.close()
            await asyncio.sleep(2)

    async def looming_loop(self):
        while True:
            await asyncio.sleep(1 / LOOMING_HZ)
            try:
                value, side = self.game.looming()
                if self.brain is not None:
                    await self.brain.send(json.dumps({"type": "looming", "value": value, "side": side}))
                elif value > 0.6:
                    # No brain: crude fallback so the game still plays.
                    self.game.on_escape(min(1.0, value), "left" if side == "right" else "right")
            except ws.ConnectionClosed:
                self.brain = None
            except Exception as e:
                print(f"[backend] looming loop error: {e!r}")

    async def tick_loop(self):
        period = 1 / TICK_HZ
        nxt = time.monotonic()
        while True:
            nxt += period
            try:
                self.game.tick(period)
                if self.clients:
                    await self.send_all(self.game.snapshot())
                    for e in self.game.events:
                        await self.send_all({"type": "event", **e})
            except Exception as e:
                print(f"[backend] tick error: {e!r}")
            self.game.events.clear()
            await asyncio.sleep(max(0.0, nxt - time.monotonic()))


async def main():
    p = argparse.ArgumentParser()
    p.add_argument("--port", type=int, default=8766, help="WebSocket port for Unity")
    p.add_argument("--udp-port", type=int, default=9000, help="UDP port for the ESP32")
    p.add_argument("--brain", default="ws://127.0.0.1:8765", help="brain server URL")
    p.add_argument("--host", default="0.0.0.0")
    a = p.parse_args()
    b = Backend(a.brain)
    await ws.serve(b.on_client, a.host, a.port)
    loop = asyncio.get_running_loop()
    await loop.create_datagram_endpoint(b.udp_protocol, local_addr=(a.host, a.udp_port))
    print(f"[backend] Unity ws://{a.host}:{a.port}  ESP32 udp://{a.host}:{a.udp_port}  brain {a.brain}")
    await asyncio.gather(b.brain_loop(), b.looming_loop(), b.tick_loop())


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
