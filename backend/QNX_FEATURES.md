# QNX features used by the backend

Tested on the loaner Raspberry Pi 5 (QNX 8.0, Python 3.14, user `qnxuser`) on 2026-11-21.
The code is in `backend/qnx.py`, `backend/server.py` and `backend/start.sh`. Every feature
falls back to plain behaviour on a laptop, so the same code runs and tests on Linux.

## Used, and verified on the Pi

| QNX feature | What we do with it | Where | How it was verified |
|---|---|---|---|
| Priority-based real-time scheduler (`SCHED_RR`) | The backend raises itself to priority 20 (QNX's default is 10), so the 30 Hz fly loop pre-empts ordinary background work. Priority 20 is high enough to win, low enough not to starve the OS. | `qnx.set_realtime()`, `--rt-priority` | `pidin` shows the backend at `20r`; the backend reports `SCHED_RR priority 20` |
| `on -p 20r` (QNX launcher) | The brain server is started under the same real-time priority without changing its code | `start.sh` | `pidin` shows the brain at `20r`; the live, old servers started by hand show `10r` |
| `slog2` system logger | Start-up, Unity and brain connect/disconnect, slow brain replies and late ticks go to the system log, rate-limited | `qnx.log()` (calls `slogf`) | Lines tagged `flyai:` appear in `slog2info` |
| `slog2info` | `sh backend/start.sh logs` prints the recent backend events from the system log | `start.sh` | Ran it; showed the events above |
| `pidin` | `sh backend/start.sh status` shows each server's QNX priority and policy | `start.sh` | Showed `20r` for both |
| `ClockPeriod` (kernel timer tick) | Read at start-up and reported. It is 1 ms on this Pi, which is the floor for timing accuracy: a `sleep(1 ms)` took about 2 ms. | `qnx.tick_ns()` | Reported `timer_tick_ns=1000000` |
| Monotonic clock (`CLOCK_MONOTONIC`) | Nanosecond timestamps for deadline checks | `qnx.now_ms()` | Used in the numbers below |
| Deadline monitoring | A `stats` message returns how late each 30 Hz tick ran and the brain's round-trip time. A tick more than half a period late, or a brain reply over 100 ms, is logged as a warning. | `Backend.stats()`, `qnx.Latency` | Ran 10 s of play against the live brain |

### Measured on the Pi (live connectome brain, 10 s of simulated play)

| Measure | Result |
|---|---|
| Fly loop, 30 Hz (33.3 ms period), lateness over 706 ticks | median 1.05 ms, 99th percentile 2.06 ms, worst 2.07 ms |
| Brain round trip, request to escape reply, 456 replies | median 20.4 ms, 95th percentile 33.7 ms, worst 40.9 ms (deadline 50 ms) |

This was a short run with a test client, not a long run under Unity. The old live servers
were also running on the same Pi. Treat it as a first measurement, not a guarantee. A
`stats` message at any time gives the current figures: send `{"type":"stats"}` to port 8766.

## Tried, not available or not used

| Feature | Result |
|---|---|
| `mlockall` (keep memory out of swap) | **Denied** for `qnxuser` ("Operation not permitted"). It needs elevated rights. We try it and log the result. Running the whole backend as root to get it was not worth the risk. |
| High Availability Manager (`ham`) | Not installed on this image, so no automatic process restart. The watchdog that does exist is in the brain server (it fails over to a lookup table). |
| Changing the timer tick (`ClockPeriod` write) | Not attempted. It needs elevated rights and affects the whole system. |
| `tracelogger` and `hogs` | Present on the Pi, but not integrated. `tracelogger` would show kernel-level scheduling during a demo; `hogs` shows CPU use per process. Both can be run by hand. |
| QNX message passing / resource managers | Not used. The backend is Python, and these need C. |

## Open-source AI modules from QNX: not used yet

The Pi's package repository (`apk`) offers `opencv` (including a deep-learning module and
`python3-opencv`), `llama.cpp`, `ncnn` and `mediapipe`. **None is installed and none is used.**
The fly brain is custom NumPy code, so the project does not yet meet a "uses an open-source
AI module from QNX" requirement unless the organizers count something already here. Ask
them, then see "Other branches" below for the natural place to add one.

## Other branches that could use these

I read the file lists of the other branches and the Pi code on `firmware`, not their
full code, so treat these as pointers.

- **`firmware` (teammate's Pi communication work).** Most useful. `pi_server/collect_training_data.py`
  runs on the Pi and receives about 200 IMU samples a second over WebSocket.
  - It could call `qnx.set_realtime()` and `qnx.log()` so sample gaps are logged, and
    `qnx.Latency` to measure the sample interval. Jitter in training data matters.
  - **Port clash:** it listens on 8765, which is also the brain's port. One of them has to move
    (the brain takes `--port`, and `start.sh` takes `BRAIN_PORT`).
  - This is also where a gesture classifier would go, and where `ncnn`, `opencv` (dnn) or
    `mediapipe` from the QNX repository could be the open-source AI module.
- **`brain-server`.** The brain server has the 50 ms deadline and failover logic. It could call
  `qnx.set_realtime()` itself (instead of relying on `on -p` in `start.sh`) and log failovers to
  slog2. This branch's `brain/` is already merged into the backend branch.
- **`unity_and_lidar`, `themes`, `sfx`, `unity_testing_gyro_accel`.** These are Unity or laptop
  code (scanning, themes, sound, a serial IMU bridge). None runs on the Pi, so QNX features do
  not apply unless something moves there. The LiDAR scripts are plain Python and could run on
  the Pi, but nothing needs real-time behaviour.
- **ESP32 firmware (`firmware/src`).** Runs on the ESP32, not on QNX, so no.
