"""Guided data collection for the swatter model. Standard library only, so it runs on the QNX Pi.

    python3 backend/collect.py --person alice --trials 5

Point the ESP32 at ws://<pi-ip>:8767 first (the same stream the game backend receives; stop
backend/server.py while collecting, or pass --port to use another port). The script asks for
each action in turn, counts down 3-2-1, records a short window of samples, and appends them to
a CSV. The order is shuffled every round so one action is not always recorded first.

CSV columns: timestamp, ax, ay, az, gx, gy, gz, label, trial_id, person
Record several people with the sensor fixed on the swatter in its final position, then
keep one person's rows aside to test the trained model on someone it has not seen.
"""
import argparse
import asyncio
import csv
import json
import os
import random
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "brain"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import ws  # noqa: E402
from gesture import parse_sample  # noqa: E402

LABELS = ["swing left", "swing right", "swing up", "swing down",
          "rotate clockwise", "rotate counterclockwise", "idle"]
FIELDS = ["timestamp", "ax", "ay", "az", "gx", "gy", "gz", "label", "trial_id", "person"]
MIN_SAMPLES = 20


class Collector:
    def __init__(self):
        self.rows = None            # a list while recording, else None
        self.connections = 0
        self.last = 0.0

    async def on_esp32(self, sock, path):
        self.connections += 1
        print(f"  swatter connected from {sock.peer}")
        try:
            while True:
                try:
                    t, accel, gyro = parse_sample(json.loads(await sock.recv()))
                except (ValueError, TypeError, RecursionError):
                    continue
                self.last = time.monotonic()
                if self.rows is not None:
                    self.rows.append([t if t is not None else int(time.monotonic() * 1000), *accel, *gyro])
        except ws.ConnectionClosed:
            pass
        finally:
            self.connections -= 1
            print("  swatter disconnected")


def next_trial_id(path):
    """Continue numbering after whatever is already in the file."""
    try:
        with open(path, newline="") as f:
            ids = [int(r["trial_id"]) for r in csv.DictReader(f) if r.get("trial_id", "").isdigit()]
        return max(ids) + 1 if ids else 1
    except (OSError, ValueError):
        return 1


async def main():
    p = argparse.ArgumentParser()
    p.add_argument("--port", type=int, default=8767)
    p.add_argument("--out", default="data/training_data.csv")
    p.add_argument("--person", default="unknown", help="who is performing the actions")
    p.add_argument("--labels", default=",".join(LABELS), help="comma separated action names")
    p.add_argument("--trials", type=int, default=5, help="repeats of every action")
    p.add_argument("--seconds", type=float, default=1.5, help="recording window per trial")
    p.add_argument("--auto", action="store_true", help="no Enter prompts or countdown (for tests)")
    a = p.parse_args()
    labels = [l.strip() for l in a.labels.split(",") if l.strip()]

    c = Collector()
    await ws.serve(c.on_esp32, "0.0.0.0", a.port)
    print(f"Waiting for the swatter on ws://0.0.0.0:{a.port} ...")
    while c.connections == 0:
        await asyncio.sleep(0.2)

    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    new_file = not os.path.exists(a.out) or os.path.getsize(a.out) == 0
    trial = next_trial_id(a.out)
    saved = {}
    with open(a.out, "a", newline="") as f:
        w = csv.writer(f)
        if new_file:
            w.writerow(FIELDS)
        for rnd in range(a.trials):
            order = labels[:]
            random.shuffle(order)
            for label in order:
                if not a.auto:
                    await asyncio.to_thread(input, f"\nRound {rnd + 1}/{a.trials}: '{label}'. Press Enter when ready...")
                    for n in (3, 2, 1):
                        print(f"  {n}...", flush=True)
                        await asyncio.sleep(1)
                    print("  GO", flush=True)
                if c.connections == 0:
                    print("  swatter not connected; skipping this trial")
                    continue
                c.rows = []
                await asyncio.sleep(a.seconds)
                rows, c.rows = c.rows, None
                if len(rows) < MIN_SAMPLES:
                    print(f"  only {len(rows)} samples, not saved (is the stream running at about 200 Hz?)")
                    continue
                for r in rows:
                    w.writerow([*r, label, trial, a.person])
                f.flush()
                saved[label] = saved.get(label, 0) + 1
                print(f"  saved trial {trial}: {len(rows)} samples ({len(rows) / a.seconds:.0f} per second)")
                trial += 1
    print("\nDone. Trials saved per action:", json.dumps(saved))
    print(f"Data: {a.out}")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nStopped.")
