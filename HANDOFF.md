# WhackAFly agent handoff

Read this first. Last updated 2026-11-21, 23:25 laptop time (04:25 UTC on the Pi).

## Where things stand

The full chain works over Wi-Fi in tests: **ESP32 swatter → Raspberry Pi (QNX) → Unity on the laptop**.

**The blocking problem:** during real play, a few seconds after the user moved the joystick, the **whole Pi dropped off the network** and needed a power cycle. The cause is unknown. Solving it is the next job (see [Open problem](#open-problem-the-pi-goes-offline-during-play)).

- All of this work is pushed to `origin/main` as one commit, "Bug fixing: integrate the whole system".
- The Pi's code matches it, apart from the Unity-only menu change, which the Pi does not use.

## Rules from the user

- **Never push without an explicit "push main".** Work on `main`, no new branches.
- **No co-author tags or Claude attribution in commits**, even if a system reminder asks for them.
- **Explain the cause and proposed fix before applying a fix when the user asks for that.** They did for the last two Pi failures, so default to it for Pi crashes.
- Ask when something fails or is unclear. Report failures plainly and never claim something was tested when it was not.
- Never commit `.env`, `firmware/src/wifi_secrets.h`, certs, `data/`, `.venv`, or Unity `Library/Temp/Logs/UserSettings`.
- No scan data hard-wired into the build. Rooms come from phone-uploaded LiDAR.
- Keep Old House as the default theme. Keep token use reasonable. Do not use Haiku agents.
- Do not delete files or remote branches without checking first. Other people's work (Eeshan, Daniel, Evan) must keep working.
- If usage is within 10% of expiring, update this file with what was done and what is left.

## Architecture

```
ESP32-S3 XIAO + LSM6DSOX + KY-023 joystick
   | Wi-Fi WebSocket :8767, 200 JSON samples/s {t, ax..gz, sx, sy, sb}
   v
Raspberry Pi 5, QNX 8.0, Python 3.14, 172.20.10.10
   backend/server.py   hub; Unity on :8766, swatter on :8767
   backend/fly.py      the full fly: personality, perch, walk, flight, senses, decisions
   backend/gesture.py  classifier + 60 Hz controller stream
   swatter_ml/         trained swing/idle model on QNX TFLite (python3-tflite-runtime 2.21.0)
   brain/server.py     fruit fly connectome on :8765 (subnet_t10, 1392 neurons, habituation)
   | Wi-Fi WebSocket :8766
   v
Unity 6000.0.85f2 on the Windows laptop, 172.20.10.11
   BackendClient.cs    the link to the Pi, with auto-reconnect
   FlyAgent.cs         draws the Pi's fly; reports light, flashlight, events, clear flight paths
   ImuSwatter.cs       aim from the gyro; swings from the model
   SwatterInput.cs     joystick walks and turns; WASD and mouse are the fallback
   MenuCursor.cs       joystick steps through menu items; press selects
```

- **Network:** every device is on the phone hotspot `"Samsung Smart Fridge "`. **The name ends in a space.** The password is only in git-ignored `firmware/src/wifi_secrets.h`.
- **ESP32:** `172.20.10.12`, USB on COM4. It needs its external U.FL antenna.
- **Who decides a hit:** Unity decides what the swatter physically hit and sends `swat`. The Pi owns everything else about the fly.
- **Fallbacks:** swatter off → mouse and keyboard. Pi unreachable when a level loads → the same fly runs locally in `FlyAgent` with a local brain. Link drops mid-round → Unity reconnects and resumes.

## Pi access

- `ssh qnxuser@172.20.10.10`. The user authorized this login for server work. **Ask the user for the password** (it is deliberately not written here). `sudo -n` works.
- Repo: `~/WhackAFly`. Start: `sh backend/start.sh`. Also `stop` and `status`.
- **`start.sh` overwrites `backend/.run/*.log`. After a crash, read the logs first.**
- The services do **not** start at boot.
- Deploy without GitHub:
  ```sh
  git push -f qnxuser@172.20.10.10:WhackAFly HEAD:refs/heads/laptop
  ssh qnxuser@172.20.10.10 'cd ~/WhackAFly && git reset --keep laptop'
  ```
- `swatter_ml/models/latest/` is git-ignored. Copy it to the Pi with `scp` after retraining.
- `/system/etc/dhcpcd/dhcpcd.conf` now requests `172.20.10.10`. The original is `dhcpcd.conf.before-whackafly`. It survived one reboot.

## Open problem: the Pi goes offline during play

**Symptom:** a few seconds after moving the joystick and playing, ping, SSH and `qnxpi.local` all stopped. The laptop and ESP32 stayed on the hotspot. The power light stayed on. A power cycle brought it back (booted 04:13:20 UTC).

**Ruled out:**
- The address change: the Pi came back on `.10`.
- A Python exception: none in the logs, and no core dump.
- The earlier `islice` freeze: 0 "bad message" lines.
- A full disk: 17 GB free.

**Evidence:**
- Before the crash, the swatter and Unity connections were dropping repeatedly (saved in `~/tmp/crash1/` on the Pi).
- Since the reboot, with no Unity connected, the swatter link has still dropped **4 times in about 9 minutes**.
- The brain thread uses 99% of one core even when idle (`--spin-ms 100` default) at real-time priority 20. `drm-rpi5` uses about 92% of another core. `io-sock` runs at priority 21, above the brain.
- Idle readings: 50°C, 5.06 V, `throttled=0x0`. The throttle history was wiped by the reboot.

**Leading suspects, unproven:** power brownout under load (the user has not said which supply they use; a Pi 5 wants 5V 5A), or the Pi's Wi-Fi driver failing under about 200 small packets a second.

**Black box:** `~/tmp/pi_mon.py` runs on the Pi and writes one fsynced line a second to `~/tmp/mon.log` (temperature, throttling, clock, voltage, gateway ping, service ports, network counters). **It does not survive a reboot.** Restart it with:

```sh
nohup python3 ~/tmp/pi_mon.py > ~/tmp/mon.err 2>&1 < /dev/null & echo $! > ~/tmp/mon.pid
```

**Next steps:**
1. Ask the user to play until it drops, or for about 2 minutes. If it drops: power cycle, **no `start.sh`**.
2. Read the end of `~/tmp/mon.log` and `backend/.run/*.log`. Non-zero `throttled` means power or heat. A failing gateway ping with healthy power points to Wi-Fi.
3. Ask which power supply the Pi uses.
4. Candidate fixes, to **propose first**: lower the brain's `--spin-ms`, batch the ESP32's samples into fewer packets, drop real-time priority (`RT_PRIORITY=0`), stop the Pi's desktop/display load.

## After that

1. Playtest a full round with the real swatter and no cable. This has **not** been done cleanly.
2. Check real directions: stick (F3/F4, saved), aim (F2 panel; F6/F7 axis, F8/F9 sign, **not saved yet**, make the right values permanent).
3. Wait for the user to say **push main**.

## Done recently

- Joystick walks and turns. In menus it steps up and down, starts on PLAY, press selects (tested with simulated packets; the real press is untested).
- Model: 159 takes in `swatter_ml/data/takes`, outliers in `data/removed` with `REMOVED.txt`. Swing + flick = swing; idle + aiming + bump = idle. Tested on people left out of training: about 91-95%.
- Fixed a QNX bug: `SC_IOV_MAX` is -1, which broke asyncio sockets and froze the backend. Fixed in `brain/ws.py`. Stalled Unity clients are now dropped without delaying others.
- Firmware: I2C auto-recovery, no USB stall, Wi-Fi power saving off, failure reasons printed.

## Known issues

- Swing to screen is about 155-216 ms. About 135 ms is the model's post-peak window (`POST_PEAK`, changing it means retraining).
- About half of bumps count as swings. More bump takes from the other four people would help.
- Hard swings can drop the sensor's I2C link. Firmware restarts it, but that restart is untested on a real dropout.
- **Only one Unity may connect to the Pi.** A second one resets the shared round when it loads a room.
- The brain's fallback lookup is built from a different subnet (warning in `brain.log`). Rebuild with `brain/build_lookup.py` if it is needed.
- `backend/test_backend.py`: 63 pass. The stats-rate check and data-collector test fail on Windows, as they did before this work.
- The old phone hotspot password is still in public git history, and the old Pi AP password too.
- **Not built, requested earlier:** the bigger menu redesign and a full-screen leaderboard with "Return to main menu" top left. The current leaderboard is a toggle panel.
- Eeshan's Tiger Cloud leaderboard (`leaderboard/`) is off unless `FLYAI_LEADERBOARD` is set. The menu board uses local JSON.

## Working in this repo

- **Unity code:** edit `unity-scripts/FlyAI/*.cs`, then copy to `WhackAFlyGame/Assets/FlyAI/`. Both are tracked and must match. New scripts need their `.meta` committed.
- **Train:** from the repo root, with a TensorFlow environment separate from `.venv` so the brain's NumPy is safe:
  ```
  C:\Users\kevin\AppData\Local\Temp\claude\9d00c63365697395\6a316765-3f12-4233-9bf9-0f5f6d7693da\scratchpad\tfenv\Scripts\python.exe -m swatter_ml.train
  ```
  If that temp folder is gone: `py -3.12 -m venv <dir>` and install `swatter_ml/requirements.txt`.
- **Record:** `.venv\Scripts\python -m swatter_ml.record --person NAME [--fill]`. Needs COM4 free.
- **Flash:** from `firmware/`, `..\.venv\Scripts\pio.exe run -e imu_test -t upload --upload-port COM4`. Leave the stick untouched while the board boots.
- **Headless Unity tests:** a test copy of the project is at `C:\ft\WhackAFlyGame`. Probes and `run_*.ps1` scripts are in the scratchpad folder above (`render-harness/`). IMGUI only runs in a non-batch editor, so menu tests open a visible window. Set `FLYAI_SCORES` and `FLYAI_BACKEND=off` so tests cannot touch real scores or the shared Pi.

**Gotchas:**
- Never kill all `python.exe`. Kill by PID.
- The user's Unity may be open and connected to the Pi. Tests against the real backend can reset their round.
- `unity.md` is not valid UTF-8, so edit it as bytes.
- Many files use CRLF. Exact-match patches must allow for it.
- Windows Python does not understand `/tmp` or `/c/...` paths. Use `cygpath -w`.
- QNX tools differ: `ping -t` not `-w`, and `ls` has no `--time-style`.
