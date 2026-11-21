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
| `ping`, `stats` | none | `stats` returns tick timing, brain round trip, brain mode and source, and the round. |

Positions are Unity world metres. Bad or missing fields are ignored, never fatal.

Backend to Unity:

| Message | Fields |
|---|---|
| `hello` | `tick_hz`, `brain` (is it connected) |
| `fly` (30 Hz) | `state` (landed, flying, fleeing, dead, idle), `pos`, `heading`, `distance`, `score`, `swings`, `phase` (countdown, playing, won, lost), `time_left`, `round_seconds`, `brain` (`live`, `fallback` or null), `spikes` (the brain's neuron counts, 22 groups) |
| `event` | `kind`: `squish`, `escape`, `round_start` or `round_end` (with `result` won or lost) |
| `swing_result` | `hit`, `score` |
| `stats`, `pong` | as above |
| `swatter` | an ESP32 datagram forwarded as is; `evt` is `swing` or `move` |

ESP32 to backend: one UDP datagram per event on port 9000, for example
`{"evt":"swing","axis":"X","dir":"positive","rate":6.2}`. `rate` is rad/s.

## Not done yet

- **The fly's behaviour is simple.** It has landed, flying and fleeing states, but not Kevin's
  walking between landing spots, perching, flight routes or `HearNoise` logic. Porting `FlyAgent.cs`
  is the main job left; Unity's sensing (lighting, flashlight) stays in Unity and arrives as `stimulus`.
- **The Unity scripts have not been compiled or run** (there is no Unity here). They are built on
  an older Unity snapshot and conflict with the newer Unity branches in `PlayerController.cs`.
- **Swatter input has three paths that do not match:** this backend takes UDP on port 9000,
  `ImuSwatter` expects UDP packets on port 9100, and the ESP32 code on the `firmware` branch
  connects by TCP to port 5000. The ESP32 firmware does not send over Wi-Fi to this backend yet.
- **Tested on the Raspberry Pi 5 up to the previous commit.** The round, stimulus and brain-mode
  changes pass all tests on the laptop but have not been run on the Pi (it was offline).
