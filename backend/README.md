# Game backend (QNX Pi)

Runs the fly: where it sits, when it hops, how it escapes, the score. Unity on
the laptop renders the room and sends the player's pose and swings. The brain
(`brain/server.py`) decides how the fly escapes. Stdlib-only Python 3.

```
ESP32 swatter --UDP :9000--> backend --WS :8766--> Unity (laptop)
                              backend --WS :8765--> brain/server.py
```

## Run

One command starts the brain and then the backend, and prints the address Unity needs:

```sh
sh backend/start.sh           # Ctrl+C stops both
sh backend/start.sh status    # what is running
sh backend/start.sh stop      # stop servers it left running
```

Options are environment variables: `SUBNET` (brain size, default `brain/subnet/subnet_t10.npz`),
`BRAIN_MODE` (`auto`, `live` or `fallback`), `BRAIN_PORT`, `BACKEND_PORT`, `UDP_PORT`.
Logs go to `backend/.run/`. To start by hand instead:

```sh
python3 brain/server.py --mode auto &     # the fly brain (add --subnet brain/subnet/subnet_t10.npz on the Pi)
python3 backend/server.py                 # Unity on :8766, ESP32 on UDP :9000
python3 backend/test_backend.py           # tests (starts both itself, uses fallback brain)
```

If the brain is not running, the backend still plays: the fly just escapes by a
simple rule. Unity finds the backend through `FLYAI_BACKEND` (default
`ws://127.0.0.1:8766`); set it to `ws://<pi-ip>:8766` before launching Unity.

QNX features used (real-time priority, `slog2` logging, deadline stats) are listed in
`backend/QNX_FEATURES.md`. Send `{"type":"stats"}` to get tick and brain timing figures.

## Ports

| Port | What | Who connects |
|---|---|---|
| 8765 | the fly brain (`brain/server.py`) | the backend (and Kevin's current Unity build, directly) |
| 8766 | the game backend (WebSocket) | Unity |
| 8767 | the swatter's IMU sample stream (WebSocket) | the ESP32 |
| 9000 | swatter events (UDP) | the ESP32 or any sender |

## The swatter

The ESP32 streams about 200 samples a second to `ws://<pi-ip>:8767`, one JSON object each:
`{"t":millis,"ax","ay","az":raw acceleration (gravity included),"gx","gy","gz":rotation in rad/s}`.
The backend validates every sample (a bad one is counted and dropped), runs it through a
classifier, and forwards each action to Unity as a `swatter` message. `backend/gesture.py` holds
the classifier: `ThresholdClassifier` is a stand-in that applies the firmware's own rules
(swing over 4 rad/s, push over 3 m/s2, with cooldowns). The trained model will implement the same
two methods (`update`, `reset`), so nothing else changes when it replaces it.

A watchdog reports the link: `never` (nothing has connected), `ok`, or `lost` (no samples for
0.5 s). It appears in every `fly` message as `swatter`, in `stats`, and as a `swatter_link` event.
When the link comes back the classifier restarts, since its gravity estimate is stale.

**Using the current ESP32 firmware** (on the `firmware` branch, which this branch does not change):
it connects to the Pi on port 8767, the backend's swatter-data receiver, and the Pi address is
written into the code. The Pi's address comes from DHCP and can change; reserve it on the hotspot
or router, or the swatter will silently fail to connect. That firmware also contains Wi-Fi
passwords in plain text (`firmware/src/main.cpp`); they should be moved out of the repository and
changed.

**Collecting training data** (standard library only, runs on the Pi; stop the backend first, or pass `--port`):

```sh
python3 backend/collect.py --person alice --trials 5
```

It prompts for each action (shuffled every round), counts down, records 1.5 s, and appends to
`data/training_data.csv` (columns: `timestamp, ax, ay, az, gx, gy, gz, label, trial_id, person`).
Record several people with the sensor fixed on the swatter in its final position, and keep one
person's rows out of training to test on someone the model has not seen.

## Rounds

By default the backend plays like Unity's `GameRound`: a 3 second countdown, a 60 second hunt,
then **won** (the fly is swatted) or **lost** (time up). Swings are ignored and the fly senses
nothing except during the hunt. A `room` or `reset` message starts a new round and also resets
the brain (its senses and habituation). `--practice` (or `PRACTICE=1` for `start.sh`) switches to
free play: no countdown or timer, and the fly respawns after a swat. `--round-seconds` and
`--countdown-seconds` change the lengths.

## Messages (JSON text frames)

Unity to backend:

| Message | Fields | Notes |
|---|---|---|
| `room` | `landingSpots` (`position`, `normal`), `head` | Send on every scan load and reconnect. Starts a new round. |
| `pose` | `head`, `aim`, `reach` | About 20 Hz. The backend works out the swatter tip and how threatening it looks. |
| `swing` | `origin`, `dir`, `strength`, `reach` | The backend decides whether it hit the fly. |
| `stimulus` | `kind` (`looming` or `sound`), `value` 0-1, `side` (`left`, `right`, `center`) | What Unity's own senses say the fly perceives (lighting, flashlight, shadows, noise). A looming stimulus replaces the backend's own estimate while it keeps arriving (within 0.3 s); a sound goes to the brain's ears straight away. |
| `brain_mode` | `mode`: `auto`, `live` or `fallback` | Switch the brain, for a failover demo. The fly state then reports `brain`. |
| `habituation` | `enabled`: true or false | Turns the brain's habituation on or off. |
| `reset` | none | New round. |
| `ping`, `stats` | none | `stats` returns tick timing, brain round trip, brain mode and source, the round, and the swatter link (samples per second, bad samples). |

Positions are Unity world metres. Bad or missing fields are ignored, never fatal.

Backend to Unity:

| Message | Fields |
|---|---|
| `hello` | `tick_hz`, `brain` (is it connected) |
| `fly` (30 Hz) | `state` (landed, flying, fleeing, dead, idle), `pos`, `heading`, `distance`, `score`, `swings`, `phase` (countdown, playing, won, lost), `time_left`, `round_seconds`, `brain` (`live`, `fallback` or null), `swatter` (`never`, `ok`, `lost`), `spikes` (the brain's neuron counts, 22 groups) |
| `event` | `kind`: `squish`, `escape`, `round_start`, `round_end` (with `result` won or lost) or `swatter_link` (with `state`) |
| `swing_result` | `hit`, `score` |
| `stats`, `pong` | as above |
| `swatter` | an action from the swatter: `evt` is `swing` (`axis`, `dir`, `rate` in rad/s) or `move` (`axis`, `dir`, `accel` in m/s2). Also used for UDP datagrams, forwarded as is. |

ESP32 to backend: one UDP datagram per event on port 9000, for example
`{"evt":"swing","axis":"X","dir":"positive","rate":6.2}`. `rate` is rad/s.

## Not done yet

- **The trained model.** `ThresholdClassifier` is the stand-in. No data has been collected, nothing
  is trained, and OpenCV or `ncnn` have not been tried on the Pi.
- **The fly's behaviour is simple.** It has landed, flying and fleeing states, but not Kevin's
  walking between landing spots, perching, flight routes or `HearNoise` logic. Porting `FlyAgent.cs`
  is the main job left; Unity's sensing (lighting, flashlight) stays in Unity and arrives as `stimulus`.
- **The Unity scripts have not been compiled or run** (there is no Unity here). They are built on
  an older Unity snapshot and conflict with the newer Unity branches in `PlayerController.cs`.
  Unity also needs to handle the new `swatter` actions, `swatter_link` and `brain` fields.
- **The ESP32 firmware is on another branch** and still needs its port and Pi address changed (see above).
- **Tested on the Raspberry Pi 5 only up to an earlier commit.** The rounds, stimulus, swatter stream
  and collector changes pass all 65 tests on the laptop but have not been run on the Pi (it was offline).
