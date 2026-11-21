# Fly brain server

The fly's escape reflex, run on a slice of the real fruit-fly connectome
(FlyWire v783). The fly has two senses: the game sends how strongly the
swatter is **looming** (eyes) and how loud a nearby **sound** is (ears:
glass breaking, a swatter whoosh or impact). The brain simulates its
looming-detector + ear → Giant Fiber circuit for 50 ms and answers how hard,
and which way, the fly escapes, plus spike counts for the neuron panel.

Runs on plain Python + NumPy (the WebSocket layer is stdlib only), so it can
run on the QNX Raspberry Pi. If NumPy or the live brain isn't available, it
answers from a lookup table with the same interface.

## Protocol (JSON over WebSocket)

```
in : {"type":"looming","value":0-1,"side":"left"|"right"|"center"}   eyes
     {"type":"sound",  "value":0-1,"side":"left"|"right"|"center"}   ears (new)
out: {"type":"escape","strength":0-1,"side":"left"|"right","spikes":[22 counts]}
```

- `side` in = which eye sees the swatter / which ear hears the sound
  (`center` = both).
- `side` out = **the direction the fly jumps** (away from the threat). Left-eye
  looming or a sound on the left → `"right"`.
- `spikes` = spikes per group in that 50 ms window, in this order (also sent
  as `group_names` in `status` and in the hub `hello`):
  - 0–3 `LPLC2_L, LPLC2_R, LC4_L, LC4_R` (eye inputs)
  - 4–8 `LPLC1, LHAD1g1, PVLP010, PVLP151, other_inter` (interneurons)
  - 9–18 `DNp01_L, DNp01_R, DNp02_L, DNp02_R, DNp04_L, DNp04_R, DNp06_L,
    DNp06_R, DNp11_L, DNp11_R` (escape neurons; DNp01 = Giant Fiber)
  - 19–21 **new**: `JO_L, JO_R` (ear inputs), `sound_inter` (interneurons
    that are only in the network because of the ears)

  Indices 0–18 are unchanged (same names, same neurons). On `subnet_t10` /
  `t20` the names at 4–7 differ, as before; always read `group_names`.
- **Sending both senses:** each decision simulates the latest looming *and*
  the latest sound together. Messages that arrive within 3 ms of each other
  (`--gather-ms`), e.g. a looming and a sound sent in the same game frame,
  become **one** 50 ms simulation and **one** reply, carrying the `id` of the
  last message. A value counts for 100 ms after it arrives (`--hold-ms`), then
  it's treated as 0. So keep sending looming at ~20 Hz as now; for a sound,
  send it in the same frame as that frame's looming, and resend it each frame
  for as long as it lasts (a one-shot `sound` affects the next ~2 windows).
- Send at ~20 Hz (one per 50 ms window = real time). Sending faster is
  safe: requests are coalesced, only the newest is simulated, and superseded
  ones get no reply. Don't send sound in *different* frames from looming at
  20 Hz each: that's 40 decisions/s, and the brain runs at 2× real time.
- **Bad input** never crashes the server or drops the connection.
  Non-numeric, boolean, null or non-finite `value`, invalid JSON, a
  non-object message or an unknown `type` → `{"type":"error","error":...}`
  (with the `id` if there was one). Out-of-range values are clamped to 0..1,
  an unknown or missing `side` counts as `center`, and a missing `value`
  counts as 0 (all as before). Numeric strings like `"0.5"` are accepted, as
  before.

Optional extras: `{"type":"ping"}` → `pong` · `{"type":"status"}` →
mode/source/timings/group names (plus `sound_live`, `sound_fallback`,
`hold_ms`, `gather_ms`, `merged`, `bad_requests`) · `{"type":"reset"}` (new
round; also clears both senses) → `reset_ok` ·
`{"type":"mode","mode":"live"|"fallback"|"auto"}` → `mode_ok`. Any request
may carry `"id"`; the reply echoes it.

## For the Unity side: adding sound

Nothing already working has to change: `looming` and the `escape` reply are
the same, and spike indices 0–18 mean what they did.

1. **When a loud event happens**, map it to a value 0..1 and the side of the
   fly it's on, e.g. glass breaking 1.0, swatter impact 0.8, swatter whoosh
   0.5, a loud noise 0.3–1.0 by distance. Side = which side of the fly the
   source is on (`left` / `right` relative to the fly's heading); `center` if
   it's in front/behind or unknown.
2. **Send it in the same frame as that frame's looming message**, right after
   it: `{"type":"sound","value":0.8,"side":"left","id":124}`. For a sound that
   lasts (a buzz, a whoosh over several frames), send it every frame it's
   audible; a single message only counts for ~100 ms.
3. **Expect one reply per frame**, not two. When looming and sound arrive
   together the reply carries the **sound's** `id` (the last message's), and
   the looming message gets no reply of its own, the same as an already
   superseded looming. If you match replies by id, match against the last id
   you sent that frame.
4. **Read the reply as now**: `strength`, `side` (direction to jump), `spikes`.
   `spikes` now has 22 entries; 19–21 are `JO_L`, `JO_R` (ears) and
   `sound_inter`. Use `group_names` from `{"type":"status"}` for labels rather
   than hard-coding, since t10/t20 name groups 4–7 differently.
5. **Remove the old fake** (sound sent as a looming pulse). Otherwise a sound
   counts twice: once through the eyes, once through the ears.
6. **Errors**: a malformed message gets `{"type":"error","error":"..."}`
   (with your `id`). Ignore or log it; don't treat it as an escape.

## Run it

```sh
pip install numpy                       # laptop extraction also needs: pandas pyarrow
python brain/server.py                  # ws://0.0.0.0:8765, mode auto
python brain/server.py --hub ws://HUB_IP:PORT/brain   # also dial out to the hub (auto-reconnect)
python brain/server.py --mode fallback  # lookup table only
```

Flags: `--subnet brain/subnet/subnet_t10.npz` (smaller network), `--port`,
`--no-listen` (hub only), `--timeout-ms 200` (live deadline in auto mode),
`--spin-ms 100` (see below), `--hold-ms 100` (how long a looming/sound value
keeps counting), `--gather-ms 3` (messages this close together share one
decision; 0 = decide on every message), `--debug` (enables a stall command
for testing).

`brain/subnet/` is committed (≈360 KB), so the server and the Pi need **no
connectome download**. Rebuilding it needs the raw data (≈135 MB):
`sh brain/get_data.sh`, then `connectome.py` → `extract.py` → `calibrate.py`
→ `build_lookup.py` (≈9 min, mostly the looming × sound grid).

## Modes and failover

- **live**: the connectome simulation, in its own worker thread. The asyncio
  loop only parses and queues, so a slow step never blocks messages (ping
  stays ~1 ms while the brain is busy).
- **fallback**: `subnet/lookup.json`, built by sweeping the live brain:
  strength, jump-side probability, mean spikes per group for looming only and
  sound only (0→1 in 0.05 steps, per side), plus looming × sound together on
  an 11 × 11 grid for each of the 9 eye-side/ear-side pairs (interpolated).
  Stdlib only. An old looming-only table still loads (sound is then ignored).
- **auto** (default): live with a watchdog. A live decision that misses its
  200 ms deadline is answered from the fallback; 2 misses in a row switch to
  fallback; the live brain keeps getting requests as probes, and 5 on-time
  answers switch back. Switching syncs the readout state, so strength doesn't
  jump. If NumPy or the subnetwork fails to load, it starts on fallback.

## How the brain works

| Piece | What it is |
|---|---|
| Data | `Completeness_783.csv`, `Connectivity_783.parquet` (Shiu et al. 2024), cell types from `Supplemental_file1_neuron_annotations.tsv` (FlyWire annotations). |
| Neurons | Eye inputs LPLC2 (210: 108 L / 102 R) + LC4 (104: 54 L / 50 R). Ear inputs: Johnston's organ **JO-A + JO-B** neurons annotated `mechanosensory` / `auditory` (341: 200 L / 141 R, 12 cell types). Outputs DNp01, DNp02, DNp04, DNp06, DNp11 (one per side each). Counts per type and side are checked against the annotations at extraction (`connectome.py`) and it stops if anything's off. |
| Subnetwork | Every neuron on an input→output path of ≤3 synapses, connections ≥5 synapses, from either sense: **2,264 neurons, 44,662 connections** (`subnet_t5`). Its vision part is exactly the old 1,602-neuron / 34,814-connection network; sound adds 341 ear neurons and 321 interneurons. Also `subnet_t10` (1,392 / 10,378; vision part 882) and `subnet_t20` (835 / 1,631; vision part 450) for the Pi. |
| Ear pathway | Ear → escape neuron in 1 synapse at ≥5: left ear → DNp01_L, DNp02_L, DNp11_L; right ear → DNp01_R, DNp02_R, DNp11_R. **Strictly same-side**; the other side is ≥2 hops. Main relays: CB2556, CB2789, DNg29, CB1076, DNg24, SAD052/053 (AMMC/SAD). |
| Model | Leaky integrate-and-fire, Shiu et al. constants: v₀ = v_reset = −52 mV, v_th = −45 mV, τ_m = 20 ms, τ_syn = 5 ms, refractory 2.2 ms, delay 1.8 ms, w = 0.275 mV/synapse, Poisson kick 250·w. dt = 0.1 ms, 500 steps per decision, NumPy only. State carries over between decisions. |
| Input | Looming value × max rate → Poisson rate of that eye's LPLC2/LC4 neurons (`center` = both eyes). Max rate (15 Hz) is calibrated so that at full looming the Giant Fiber fires in bursts (≈1 spike per window), not nonstop. Sound value × sound rate → Poisson rate of that ear's JO neurons (`center` = both ears); both senses can be on in the same window. |
| Strength | Giant Fiber spikes + 0.5 × other escape-DN spikes, through a 100 ms low-pass (like a motor integrator), normalised so one-eye full looming ≈ 1. Same scale for sound: the sound rate is calibrated so one-ear full sound gives the same drive as one-eye full looming. Both together add up (capped at 1). |
| Side | DNp02/04/11 on each side vote; the fly jumps away from the side that votes more. A tie keeps the last choice. Same rule for sound. |
| Sound calibration | `subnet_t5`: right ear 105 Hz, left ear ×15.8; full sound → strength 0.95 (L) / 1.00 (R), jumps away 100%. `subnet_t10`: 168 Hz, left ×13.7; full sound only reaches **0.75 / 0.78** (the Giant Fiber burst limit binds first). `subnet_t20`: **sound doesn't work** (0.06 / 0.07 even at 400 Hz): at ≥20 synapses only 14 ear neurons keep a connection. |

### Decisions worth knowing about

- **DNp01 transmitter conflict.** The annotations predict acetylcholine for the
  left Giant Fiber and glutamate for the right one, both at low confidence
  (0.32 / 0.27) and with no known transmitter. **Both are treated as
  excitatory**: the Giant Fiber is known to be cholinergic (plus electrical),
  Shiu et al.'s connectivity already signs both that way, and an asymmetry here
  would bias which way the fly jumps. `extract.py` enforces it explicitly.
- **Side comes from DNp02/04/11, not DNp01.** In this chemical-synapse model
  both Giant Fibers respond *more to right-eye looming*. The right fiber gets
  ~50% more direct LPLC2/LC4 synapses, and the left fiber gets strong
  same-side inhibition (PVLP010, LHAD1g1), so DNp01 L vs R can't tell the
  side. DNp02, DNp04 and DNp11 fire **only** for looming on their own side, at
  every rate and threshold tested, so they decide. DNp06 flips laterality with
  input rate, so it doesn't vote. The decision is still the brain's: game
  code only sees `side`.
- **Per-eye gain.** The right-eye pathway drives the escape neurons ~1.6× harder
  than the left. `calibrate.py` turns down the stronger eye's input rate
  (right × 0.57 on `subnet_t5`) so both eyes give the same full-looming
  strength. Stored in `subnet_tN.calib.json`.
- **Center looming** (both eyes) saturates by looming ≈ 0.5 and always jumps
  right (the left-eye vote wins). That's the brain's call; reported as-is.
- **Which neurons are "sound".** JO-A and JO-B are the cell types FlyWire
  labels `auditory` (Johnston's organ, the antennal ear). 17 neurons with the
  same type names but a different sub-class (e.g. 9 `JO-B4_b` labelled
  `wind_gravity`) are left out, and so are the wind/gravity types JO-C/D/E
  (they reach the escape neurons only in ≥2 hops). One annotated left
  `JO-B1_a` (root 720575940616042508) isn't in `Completeness_783`, so it
  can't be simulated; `connectome.py` expects exactly that one to be missing.
- **Ear transmitters.** The per-neuron predictions are mixed and none is
  confirmed (183 ACh, 65 serotonin, 36 glutamate, 21 GABA, 5 octopamine, 1
  dopamine), but Shiu et al.'s connectivity signs all 5,004 JO connections as
  excitatory. That is kept, as for the Giant Fiber, and extraction stops if
  any JO connection ever turns inhibitory. The Giant Fiber rule above applies
  unchanged (in the new networks no DNp01 connection needed flipping).
- **The left ear is much weaker than the right.** In this connectome the
  right ear has 127 direct synapses onto its Giant Fiber, the left ear 14,
  and the left fiber also gets strong same-side inhibition. Even at 1,500 Hz
  the left ear never fires either Giant Fiber, DNp02 or DNp04; it drives the
  escape only through DNp06_L and DNp11_L. Calibration uses the same rule as
  the eyes, but the gain needed is large: the right ear runs at 105 Hz and the
  **left ear at ×15.8 (1,660 Hz)** to give the same full-sound strength. That
  rate is far above anything a real JO neuron does (it keeps the game
  symmetric, which is the point here). Consequences: a left-side sound
  escape uses no Giant Fiber (on the panel, DNp01 stays dark), and its jump
  side comes from DNp11_L alone. Whether this asymmetry is biological or a
  reconstruction/annotation effect isn't something the data can tell.
  To model the ears as-is instead, set `"ear_gain": {"left": 1.0, ...}` in
  `subnet_t5.calib.json`: left sounds then do almost nothing.
- **Center sound** (both ears) jumps right every time: the left vote wins.
  Like center looming, that's the brain's call, reported as-is.
- **No electrical synapses.** The literature reports JO → Giant Fiber
  gap-junction coupling; FlyWire has only chemical synapses, so the model
  has only the chemical path and the real reflex may be faster or stronger.
- **Calibration is now repeatable.** `calibrate.py` used to start from the eye
  gain saved by its previous run, so re-running it measured something
  different (25 Hz instead of 15 Hz on t5). It now measures with an explicit
  gain, and by default keeps the saved eye calibration so the looming curve
  can't move (`--vision` redoes it). With the fix, a full re-run reproduces
  the t5 and t20 eye values; t10's saved `full_drive` (4.712) is 2% above
  what a re-run gives (4.62) and is kept as-is.
- **Gather window and Windows timers.** Merging a same-frame looming + sound
  needs a short wait (3 ms) before deciding. Windows timers normally tick
  every ~15.6 ms, which made that wait ~15 ms and pushed looming round trips
  from 16 to 24 ms; the server now asks Windows for 1 ms timer resolution
  while it runs (`timeBeginPeriod(1)`), which brought it back to 16 ms. QNX
  and Linux don't need this. `--gather-ms 0` turns merging off (then a
  same-frame pair is usually two 50 ms decisions).
- **Spin-wait.** When the CPU idles between 20 Hz requests, Windows drops its
  clock and the next decision takes ~2× longer (22 → 42 ms measured). The
  worker busy-waits up to `--spin-ms` (default 100) for the next request. That
  costs one core while the game is sending; `--spin-ms 0` turns it off.

## Tests

`bench.py` plays the game's side of the protocol (`--url` defaults to
`ws://127.0.0.1:8765`; point it at a hub to test through the hub).

```sh
python brain/bench.py all        # smoke, validate, looming sweep, sound, mixed 20 Hz, latency, reconnect, 90 s run
python brain/bench.py long --seconds 600
python brain/bench.py modes      # fallback vs live (looming, sound, mixed), mode switches, failover (server --debug)
python brain/bench.py speed      # no server: time each subnetwork (run this on the Pi)
python brain/dev_hub.py          # stand-in hub on :8800, for testing the --hub path
```

Laptop results with sound (Windows, Python 3.10, NumPy 1.26, `subnet_t5`, 2,264 neurons):

| Test | Result |
|---|---|
| Looming curve vs before sound | Same seeds, 120 decisions per level: left and right **bit-identical** to the 1,602-neuron network; center differs at 2 of 11 levels by ≤ 0.003. Eye calibration values unchanged |
| Looming sweep (bench) | Rises 0 → ~1 per side; left → jumps right, right → left 100% (strength ≥ 0.3). The "no dips > 0.05" check is noisy at 20 decisions per level: it failed 2 of 15 runs here vs 0 of 12 on the old code (in-process, both networks fail it 1 in 30) |
| Sound sweep | Rises 0 → 0.96 (left ear) / 1.00 (right); left sound → jumps right, right → left 100% (strength ≥ 0.3); center → right |
| Looming + sound | Left 0.4 each: looming 0.28, sound 0.34, both 0.59. Same-frame pair → one reply (24/24) |
| One-shot sound | Strength 0.92, down to 0.15 after ~0.25 s and 0.02 after ~0.5 s once it stops (hold 100 ms + 100 ms readout filter) |
| Bad input (25 cases) | Every malformed message → error reply with its id (or clamped); connection and server keep working |
| Mixed 20 Hz (looming every frame + sound bursts) | 400/400 frames answered, 0 double replies, round trip p50 21 / p95 28 ms |
| Latency at 20 Hz, looming only | 401/401 answered, p50 16 / p95 21 ms (before sound: 16 / 22); ping while busy p95 1 ms |
| Speed (20 Hz paced) | Looming: t5 15 ms (was 15), t10 14, t20 15. Full sound adds ~6 ms (left ear's high rate): t5 23 ms median / 27 p95 |
| Through the dev hub | Sound smoke + 10 s mixed: 200/200, 0 double replies, p95 30 ms; brain re-attaches after hub restart |
| Reconnect | Fine after 10 abrupt crashes mid looming/sound |
| Fallback vs live | Looming ≤ 0.10, sound ≤ 0.09, mixed (16 points) ≤ 0.11 apart; tolerance 0.15 |
| Mode switches mid-game (looming + sound) | 160/160 answered, max gap 92 ms, strength step at a switch 0.23 (live noise 0.32) |
| Live brain stalls 1.5 s (looming + sound) | 134/140 answered, longest silence 216 ms, fails over, recovers |
| No NumPy | Starts on fallback; sound and mixed all pass |
| 90 s run | Memory 58 MB flat (was 46 MB with the smaller network); round trip 21 ms flat |
| Long run (10 min, before sound) | Memory flat at 46–47 MB; round trip 14 → 13 ms (not re-run with sound) |

## On the QNX Pi

1. Check NumPy: `python3 -c "import numpy; print(numpy.__version__)"`.
2. Copy `brain/` (with `subnet/`) to the Pi.
3. `python3 brain/bench.py speed` shows which subnetwork meets 10 decisions/s.
   Use the largest one that does: `python3 brain/server.py --subnet brain/subnet/subnet_t10.npz`.
   Cost is mostly per-step Python overhead, so the smaller networks are only
   slightly faster. If even `t20` is too slow, run `--mode fallback` on the Pi
   (still on-device), or host the live brain on the laptop behind the same
   interface.
4. `subnet_t20` is weak: the left Giant Fiber never fires there, and sound
   barely does anything (strength ≤ 0.07). Prefer `t10`; its full-sound
   strength tops out at ~0.78.
5. `lookup.json` is built from `subnet_t5`. If the Pi runs `t10` live, the
   server warns that the fallback's group names differ (positions 4–7);
   rebuild it with `python3 brain/build_lookup.py --subnet brain/subnet/subnet_t10.npz`
   (≈9 min on the laptop, longer on the Pi) if that matters.
