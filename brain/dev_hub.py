"""Stand-in hub for testing the brain without the real hub (dev only).

Relays JSON between clients: whoever sends {"type":"hello","role":"brain"} is
the brain; "looming"/"reset"/"status" from anyone else go to the brain, and the
brain's replies go back to whoever asked (by id) or to every other client.

Run:  python brain/dev_hub.py --port 8800
      python brain/server.py --hub ws://127.0.0.1:8800/brain --no-listen
      then point a game/test client at ws://127.0.0.1:8800/game
"""
import argparse
import asyncio
import itertools
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ws  # noqa: E402

TO_BRAIN = {"looming", "reset", "status", "ping"}


class Hub:
    def __init__(self):
        self.brain = None
        self.clients = set()
        self.ids = itertools.count()
        self.routes = {}        # hub id -> (client, original id or None)

    async def handle(self, sock, path):
        self.clients.add(sock)
        try:
            while True:
                raw = await sock.recv()
                try:
                    msg = json.loads(raw)
                except ValueError:
                    continue
                if msg.get("type") == "hello" and msg.get("role") == "brain":
                    self.brain = sock
                    print("brain attached", flush=True)
                elif sock is self.brain:
                    await self.from_brain(msg)
                elif msg.get("type") in TO_BRAIN:
                    await self.to_brain(sock, msg)
        except ws.ConnectionClosed:
            pass
        finally:
            self.clients.discard(sock)
            if sock is self.brain:
                self.brain = None
                print("brain detached", flush=True)

    async def to_brain(self, sock, msg):
        if self.brain is None:
            await sock.send(json.dumps({"type": "error", "error": "no brain",
                                        **({"id": msg["id"]} if "id" in msg else {})}))
            return
        hid = next(self.ids)
        self.routes[hid] = (sock, msg.get("id"))
        if len(self.routes) > 1000:      # forget replies that never came
            for k in list(self.routes)[:500]:
                del self.routes[k]
        msg["id"] = hid
        try:
            await self.brain.send(json.dumps(msg))
        except ws.ConnectionClosed:
            pass

    async def from_brain(self, msg):
        route = self.routes.pop(msg.get("id"), None)
        if route:
            client, orig = route
            if orig is None:
                msg.pop("id", None)
            else:
                msg["id"] = orig
            targets = [client]
        else:
            targets = [c for c in self.clients if c is not self.brain]
        for c in targets:
            try:
                await c.send(json.dumps(msg))
            except ws.ConnectionClosed:
                pass


async def amain(port):
    hub = Hub()
    server = await ws.serve(hub.handle, "0.0.0.0", port)
    print(f"dev hub on ws://0.0.0.0:{port}", flush=True)
    await server.serve_forever()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8800)
    try:
        asyncio.run(amain(ap.parse_args().port))
    except KeyboardInterrupt:
        pass
