# Mission-critical framing and improvement ideas

Written 2026-11-21 on branch `backend-split`. A reference for later: nothing here is built
unless it says so. Related file: `backend/QNX_FEATURES.md` (what is used on QNX, with measurements).

## The pitch

A time-critical control loop with failover: the system senses a threat, decides within a fixed
deadline, and acts, and it keeps working when parts fail. Here the threat is a swatter
approaching a fly. The pattern is the same one used in collision avoidance and industrial
safety systems. Describe it as that pattern, not as a certified safety system.

## What supports it today (built and tested)

- **Hard decision deadline.** The brain has 50 ms to answer. If a live decision is late, the
  brain server answers from its lookup table; two misses in a row switch to the fallback, and
  five on-time answers switch back. Measured on the Pi: median 20 ms, worst 41 ms.
- **Graceful degradation.** No brain: the backend uses a simple escape rule. No NumPy: the brain
  uses the lookup table. A malformed message is ignored and the backend keeps running.
- **Measured timing.** The 30 Hz loop ran at most 2.07 ms late over 706 ticks. The `stats`
  message (send `{"type":"stats"}` to port 8766) reports it live. Late ticks and slow replies go
  to the QNX system log (`sh backend/start.sh logs`).
- **QNX real-time scheduling.** Backend and brain run at priority 20 (default is 10).
- **Recovery.** Unity and the brain connection reconnect on their own; `backend/start.sh`
  restarts everything with one command.
- **Tests.** 29 automated checks in `backend/test_backend.py` pass on the laptop and on the Pi.

## Limits to be honest about

- The ESP32 swatter does not send over Wi-Fi yet, and nothing handles that link dropping.
- The Pi image is not a safety-certified QNX product as far as we know. Do not say "certified".
- Python is not a real-time language; a garbage-collection pause could cause an occasional miss.
  The only timing run so far was 10 seconds, so there is no long-term guarantee.
- No automatic restart: the Pi has no High Availability Manager, and nothing starts the servers
  at boot. If the backend crashes, it stays down.
- One Pi and one network link: no redundancy.
- `mlockall` was denied (needs elevated rights), so a page fault could still delay a response.

## Ideas to strengthen the story

Suggested order: 1 and 3 first (quick, strongest visual), then 2, 4, 5.

1. **Live failover demo.** Stop the brain mid-game and show the fly keep moving, with the
   neuron panel switching to "fallback" and back. Best evidence for "reliability matters".
   Needs: a way to stop the brain on cue (for example `sh backend/start.sh stop` for the brain
   only, or the brain's `--debug` stall command) and a visible mode label in Unity.
2. **Swatter link watchdog.** If the swatter goes silent for a moment, flag "input lost", hold
   a safe state, and recover when it returns. Needs: a heartbeat from the ESP32 firmware and a
   timeout check in `backend/server.py`.
3. **Health display in Unity.** Show the `stats` figures on screen: tick lateness, brain round
   trip, deadline misses, current brain mode (live or fallback), link state.
4. **Auto-restart supervisor.** A loop in `start.sh` that restarts a crashed server and logs it
   to slog2. A startup hook so it survives a reboot needs a look at how this QNX image starts
   services (not investigated).
5. **Long run.** An hour-long run with the `stats` figures recorded, so claims rest on more than
   10 seconds. Run it with the real Unity client and the real swatter, not a test client.

## Related plan: gesture model (not built)

Train a small classifier on labelled swatter motions so the Pi, not fixed thresholds, decides
which gesture happened. It would run on the Pi under QNX, which also answers the "use an
open-source AI module from QNX" requirement.

- Data: `pi_server/collect_training_data.py` (on the `firmware` branch) records labelled IMU
  windows: swing left/right/up/down, rotate clockwise/counterclockwise, idle.
- Model: small 1D CNN or MLP, trained on a laptop, exported to ONNX.
- Runtime on the Pi: OpenCV's `cv2.dnn` (`python3-opencv` in the Pi's package list) or `ncnn`.
  Neither is installed, and nothing has been run on QNX yet.
- Keep the old threshold rules as the fallback, so a model failure degrades the same way the
  brain does.
- First check, before collecting data: export a dummy ONNX model and confirm OpenCV loads and
  runs it on the Pi. Installing packages changes the shared Pi, so get agreement first.
- Risks: too little data from too few people; the sensor mounting must match the final
  swatter; the 200 Hz stream must stay steady.

## Questions to settle with the organizers and the team

- Which open-source AI modules from QNX count (the Pi's package list has `opencv`, `llama.cpp`,
  `ncnn`, `mediapipe`; none is installed)?
- The `firmware` branch's collector listens on port 8765, the same port as the brain. One must
  move (`BRAIN_PORT` in `start.sh`, or `--port` on the server).
- Which network will the demo use (hotspot or the Pi's own access point), and how will the Pi's
  address be found (it comes from DHCP and may change)?
