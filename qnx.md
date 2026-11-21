# QNX Track

## The issue we're solving: a reflex for machines

Drones, small delivery robots and cars all need a "flinch" reflex: *something is about to hit me, move*. Today that is usually camera feeds plus large neural networks. That needs powerful, power-hungry hardware, and it isn't guaranteed to answer in time. A reflex that answers late is useless.

A fruit fly solves this with a few thousand neurons:

- **Looming detectors** in its eyes (LPLC2, LC4) fire when something expands fast in view, meaning it's coming straight at the fly.
- **The Giant Fiber** (DNp01) turns that into an escape jump in milliseconds.
- **Habituation** stops it overreacting to things that keep happening but aren't real threats.

Our brain server (`brain/`) runs that real circuit: 2,264 neurons copied from the FlyWire v783 connectome. It decides in under 50 ms per 50 ms window, and it is built to run on a Raspberry Pi.

## Why QNX

QNX is the real-time operating system inside many cars' safety systems. Its point is predictable timing: a task gets its turn on time, every time. A reflex is exactly that kind of task, because a late answer is as bad as no answer.

The brain server already behaves like a safety system:

| Safety behaviour | In the brain server |
|---|---|
| Hard deadline | Every decision must arrive within 200 ms (`--timeout-ms`) |
| Failover | 2 missed deadlines in a row switch to the lookup-table fallback, so the fly still reacts |
| Recovery | 5 on-time answers switch back to the live brain |
| Degrades, never stops | No NumPy or no network loaded: it starts on the fallback, which needs only the Python standard library |

## How the game demonstrates it

The swatter is the incoming object; the fly is the vehicle that has to dodge.

- **Looming:** the swing becomes a looming signal, and the brain decides whether and which way to escape.
- **Sound:** a crash nearby triggers the same reflex through the fly's ears (Johnston's organ). That's a second sense, like a car using both radar and a camera.
- **Habituation:** fake swings over and over and the fly stops reacting. That's how a reflex avoids false alarms, a real problem for collision systems.

## Pitch (about 20 seconds)

> Flies dodge swatters in milliseconds with a brain smaller than a grain of sand. We took the real wiring of a fly's escape circuit, 2,264 neurons from the FlyWire connectome, and run it as a reflex on a Raspberry Pi with QNX, the real-time OS in car safety systems. It has a hard deadline and fails over if it's late. It's a game, but it shows a cheap, predictable way to give machines a flinch reflex.

## Keep it honest

- **We didn't invent the idea.** Insect-inspired collision detection is a real research area, mostly built on locusts. We show it working with a real fly connectome; it is not a car product.
- **The eyes are simulated.** The looming signal comes from the swatter's position in the game, not from a camera. Say *"the reflex is real; the eyes are simulated"*.
- **Measure before quoting.** Only state QNX timing numbers once they've been measured on the Pi.

## What the judges look for, and our plan

The track asks for QNX running on the device doing real work, and AI running on the device with QNX's open-source AI module from [oss.qnx.com](https://oss.qnx.com).

1. **AI on the device (the core requirement).** The TFLite swing classifier (swing / flick / aiming / bump, plus strength) runs on the Raspberry Pi 5 under QNX 8.0, using the AI module from oss.qnx.com. The hub's single swap point sends every real swing from the IMU swatter to it.
2. **The fly brain on QNX too.**
   - It is plain Python, and the WebSocket layer uses only the standard library.
   - The live brain needs NumPy. Check early whether oss.qnx.com has a NumPy port (not verified yet). If not, the fallback (`--mode fallback`) runs on the standard library alone, so the brain still runs on the device.
   - Use `brain/subnet/subnet_t10.npz` on the Pi. Its full-sound strength tops out at about 0.78; `subnet_t20` barely reacts to sound.
   - First thing on the Pi: `python3 brain/bench.py speed` shows which network size keeps up.
3. **Make the real-time part visible.** This is what separates us from "we installed QNX".
   - **Deadlines and failover:** show the switch to the fallback and back. Start the server with `--debug` and send `{"type":"debug_stall","ms":1500}`, or run `python brain/bench.py failover`.
   - **Separate processes:** run the IMU reader, the classifier and the brain as separate QNX processes. Kill the brain live on stage (`kill -9`) and the game keeps going on the fallback. QNX's High Availability Manager can restart the killed process.
   - **Proof on screen:** show *swing → classified → fly decided, in X ms*, with the timing spread, and compare QNX's spread against the laptop's.

| Priority | Goal |
|---|---|
| Must have | Swing classifier running on QNX on the Pi |
| Big win | Brain on QNX, plus the failover and kill demo |
| Polish | Latency numbers on screen |

## Open checks (need the Pi)

- Does oss.qnx.com have NumPy for QNX 8.0 on the Pi 5? If not, use `--mode fallback` on the Pi.
- `bench.py speed` result for each subnetwork on the Pi.
- End-to-end latency, swing → classifier → brain → Unity, measured on the real setup.
