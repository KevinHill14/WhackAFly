# Game backend (QNX Pi)

Runs the fly: where it sits, when it hops, how it escapes, the score. Unity on
the laptop renders the room and sends the player's pose and swings. The brain
(`brain/server.py`) decides how the fly escapes. Stdlib-only Python 3.

```
ESP32 swatter --UDP :9000--> backend --WS :8766--> Unity (laptop)
                              backend --WS :8765--> brain/server.py
```

## Run

```sh
python3 brain/server.py --mode auto &     # the fly brain (add --subnet brain/subnet/subnet_t10.npz on the Pi)
python3 backend/server.py                 # Unity on :8766, ESP32 on UDP :9000
python3 backend/test_backend.py           # tests (starts both itself, uses fallback brain)
```

If the brain is not running, the backend still plays: the fly just escapes by a
simple rule. Unity finds the backend through `FLYAI_BACKEND` (default
`ws://127.0.0.1:8766`); set it to `ws://<pi-ip>:8766` before launching Unity.

## Messages (JSON text frames)

Unity to backend: `room` (`landingSpots`, `head`; sent on every scan load and
reconnect), `pose` (`head`, `aim`, `reach`, about 20 Hz), `swing` (`origin`,
`dir`, `strength`, `reach`), `reset`, `ping`. Positions are Unity world metres.

Backend to Unity: `hello`, `fly` (30 Hz: `state` landed/flying/fleeing/dead/idle,
`pos`, `heading`, `distance`, `score`, `swings`, `spikes`), `event`
(`kind` squish or escape), `swing_result` (`hit`, `score`), `pong`, and
`swatter` (an ESP32 datagram forwarded as is; `evt` is `swing` or `move`).

ESP32 to backend: one UDP datagram per event, for example
`{"evt":"swing","axis":"X","dir":"positive","rate":6.2}`. `rate` is rad/s.

## Not done yet

- The Unity scripts have not been compiled or run: there is no Unity here.
- The ESP32 firmware does not send over Wi-Fi yet (it lives on the `firmware` branch).
- The live brain has not been tested on this machine (no NumPy), only the fallback.
- Nothing has run on QNX. Check Python 3 and NumPy there first.
