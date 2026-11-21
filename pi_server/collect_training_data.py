"""WebSocket server + guided data-collection CLI for the fly-swatter IMU.

Run on the Raspberry Pi 5 (on the same hotspot as the ESP32):
    pip install websockets
    python collect_training_data.py

The ESP32 streams one JSON line per IMU sample (~200 Hz):
    {"t": <millis>, "ax": .., "ay": .., "az": .., "gx": .., "gy": .., "gz": ..}

This script prompts a human tester to perform an action (e.g. "swing left"),
waits for them to press Enter when ready, counts down, then records whatever
samples arrive during a fixed window and tags them with that action's label.
Recorded rows are appended to data/training_data.csv.
"""

import asyncio
import csv
import json
import os
import time

import websockets

HOST = "0.0.0.0"
PORT = 8765

DATA_DIR = os.path.join(os.path.dirname(__file__), "data")
CSV_PATH = os.path.join(DATA_DIR, "training_data.csv")
CSV_FIELDS = ["timestamp", "ax", "ay", "az", "gx", "gy", "gz", "label", "trial_id"]

PROMPTS = [
    "swing left",
    "swing right",
    "swing up",
    "swing down",
    "rotate clockwise",
    "rotate counterclockwise",
    "idle",
]

RECORD_SECONDS = 1.5
COUNTDOWN_SECONDS = 3

# Shared state between the websocket reader task and the CLI loop.
connected_clients = set()
recording = False
current_label = None
current_trial_id = 0
recorded_rows = []


async def handle_client(websocket):
    connected_clients.add(websocket)
    print(f"ESP32 connected ({websocket.remote_address}).")
    try:
        async for message in websocket:
            if not recording:
                continue
            try:
                sample = json.loads(message)
            except json.JSONDecodeError:
                continue
            recorded_rows.append({
                "timestamp": sample.get("t"),
                "ax": sample.get("ax"),
                "ay": sample.get("ay"),
                "az": sample.get("az"),
                "gx": sample.get("gx"),
                "gy": sample.get("gy"),
                "gz": sample.get("gz"),
                "label": current_label,
                "trial_id": current_trial_id,
            })
    finally:
        connected_clients.discard(websocket)
        print("ESP32 disconnected.")


def append_rows_to_csv(rows):
    os.makedirs(DATA_DIR, exist_ok=True)
    file_exists = os.path.isfile(CSV_PATH)
    with open(CSV_PATH, "a", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=CSV_FIELDS)
        if not file_exists:
            writer.writeheader()
        writer.writerows(rows)


async def run_cli():
    global recording, current_label, current_trial_id, recorded_rows

    print(f"Waiting for the ESP32 to connect on ws://{HOST}:{PORT} ...")
    while not connected_clients:
        await asyncio.sleep(0.5)
    print("ESP32 is connected. Starting data collection.\n")

    while True:
        for label in PROMPTS:
            await asyncio.to_thread(
                input, f"\nNext action: '{label}'. Press Enter when ready..."
            )
            for i in range(COUNTDOWN_SECONDS, 0, -1):
                print(f"{i}...")
                await asyncio.sleep(1)

            print(f"GO: {label}")
            current_label = label
            current_trial_id += 1
            recorded_rows = []
            recording = True
            await asyncio.sleep(RECORD_SECONDS)
            recording = False

            rows = recorded_rows
            recorded_rows = []
            append_rows_to_csv(rows)
            print(f"Recorded {len(rows)} samples for '{label}' (trial {current_trial_id}).")

        again = await asyncio.to_thread(
            input, "\nFinished one full pass. Run through prompts again? [y/N] "
        )
        if again.strip().lower() != "y":
            break


async def main():
    async with websockets.serve(handle_client, HOST, PORT):
        print(f"Listening for ESP32 on ws://{HOST}:{PORT}")
        await run_cli()


if __name__ == "__main__":
    asyncio.run(main())
