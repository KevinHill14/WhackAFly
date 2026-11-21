# Fly brain server

The fly's escape reflex, run on a slice of the real fruit-fly connectome
(FlyWire v783). The game sends how strongly the swatter is looming; the brain
simulates its looming-detector → Giant Fiber circuit for 50 ms and answers how
hard, and which way, the fly escapes, plus spike counts for the neuron panel.

Runs on plain Python + NumPy (the WebSocket layer is stdlib only), so it can
run on the QNX Raspberry Pi. If NumPy or the live brain isn't available, it
answers from a lookup table with the same interface.

## Protocol (JSON over WebSocket)

```
in : {"type":"looming","value":0-1,"side":"left"|"right"|"center"}
out: {"type":"escape","strength":0-1,"side":"left"|"right","spikes":[19 counts]}
```

- `side` in = which eye sees the swatter (`center` = both).
- `side` out = **the direction the fly jumps** (away from the threat). Left-eye
  looming → `"right"`.
- `spikes` = spikes per group in that 50 ms window, in this order (also sent
  as `group_names` in `status` and in the hub `hello`):
  `LPLC2_L, LPLC2_R, LC4_L, LC4_R` (inputs) · `LPLC1, LHAD1g1, PVLP010,
  PVLP151, other_inter` (interneurons) · `DNp01_L, DNp01_R, DNp02_L, DNp02_R,
  DNp04_L, DNp04_R, DNp06_L, DNp06_R, DNp11_L, DNp11_R` (escape neurons;
  DNp01 = Giant Fiber).
- Send looming at ~20 Hz (one per 50 ms window = real time). Sending faster is
  safe: requests are coalesced, only the newest is simulated, and superseded
  ones get no reply.

Optional extras: `{"type":"ping"}` → `pong` · `{"type":"status"}` →
mode/source/timings/group names · `{"type":"reset"}` (new round) →
`reset_ok` · `{"type":"mode","mode":"live"|"fallback"|"auto"}` → `mode_ok`.
Any request may carry `"id"`; the reply echoes it.

## Run it

```sh
pip install numpy                       # laptop extraction also needs: pandas pyarrow
python brain/server.py                  # ws://0.0.0.0:8765, mode auto
python brain/server.py --hub ws://HUB_IP:PORT/brain   # also dial out to the hub (auto-reconnect)
python brain/server.py --mode fallback  # lookup table only
```

Flags: `--subnet brain/subnet/subnet_t10.npz` (smaller network), `--port`,
`--no-listen` (hub only), `--timeout-ms 200` (live deadline in auto mode),
`--spin-ms 100` (see below), `--debug` (enables a stall command for testing).

`brain/subnet/` is committed (≈150 KB), so the server and the Pi need **no
connectome download**. Rebuilding it needs the raw data (≈135 MB):
`sh brain/get_data.sh`, then `connectome.py` → `extract.py` → `calibrate.py`
→ `build_lookup.py`.

## Modes and failover

- **live**: the connectome simulation, in its own worker thread. The asyncio
  loop only parses and queues, so a slow step never blocks messages (ping
  stays ~1 ms while the brain is busy).
- **fallback**: `subnet/lookup.json`, built by sweeping the live brain
  (looming 0→1 in 0.05 steps, per side): strength, jump-side probability,
  mean spikes per group. Stdlib only.
- **auto** (default): live with a watchdog. A live decision that misses its
  200 ms deadline is answered from the fallback; 2 misses in a row switch to
  fallback; the live brain keeps getting requests as probes, and 5 on-time
  answers switch back. Switching syncs the readout state, so strength doesn't
  jump. If NumPy or the subnetwork fails to load, it starts on fallback.

## How the brain works

| Piece | What it is |
|---|---|
| Data | `Completeness_783.csv`, `Connectivity_783.parquet` (Shiu et al. 2024), cell types from `Supplemental_file1_neuron_annotations.tsv` (FlyWire annotations). |
| Neurons | Inputs LPLC2 (210: 108 L / 102 R) + LC4 (104: 54 L / 50 R); outputs DNp01, DNp02, DNp04, DNp06, DNp11 (one per side each). Counts and sides are checked against the annotations at extraction (`connectome.py`) and it stops if anything's off. |
| Subnetwork | Every neuron on an input→output path of ≤3 synapses, connections ≥5 synapses: **1,602 neurons, 34,814 connections** (`subnet_t5`). Also `subnet_t10` (882 / 7,913) and `subnet_t20` (450 / 1,375) for the Pi. |
| Model | Leaky integrate-and-fire, Shiu et al. constants: v₀ = v_reset = −52 mV, v_th = −45 mV, τ_m = 20 ms, τ_syn = 5 ms, refractory 2.2 ms, delay 1.8 ms, w = 0.275 mV/synapse, Poisson kick 250·w. dt = 0.1 ms, 500 steps per decision, NumPy only. State carries over between decisions. |
| Input | Looming value × max rate → Poisson rate of that eye's LPLC2/LC4 neurons (`center` = both eyes). Max rate (15 Hz) is calibrated so that at full looming the Giant Fiber fires in bursts (≈1 spike per window), not nonstop. |
| Strength | Giant Fiber spikes + 0.5 × other escape-DN spikes, through a 100 ms low-pass (like a motor integrator), normalised so one-eye full looming ≈ 1. |
| Side | DNp02/04/11 on each side vote; the fly jumps away from the side that votes more. A tie keeps the last choice. |

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
- **Spin-wait.** When the CPU idles between 20 Hz requests, Windows drops its
  clock and the next decision takes ~2× longer (22 → 42 ms measured). The
  worker busy-waits up to `--spin-ms` (default 100) for the next request. That
  costs one core while the game is sending; `--spin-ms 0` turns it off.

## Tests

`bench.py` plays the game's side of the protocol (`--url` defaults to
`ws://127.0.0.1:8765`; point it at a hub to test through the hub).

```sh
python brain/bench.py all        # smoke, looming sweep, latency, reconnect, 90 s run
python brain/bench.py long --seconds 600
python brain/bench.py modes      # fallback vs live, mode switches, failover (server --debug)
python brain/bench.py speed      # no server: time each subnetwork (run this on the Pi)
python brain/dev_hub.py          # stand-in hub on :8800, for testing the --hub path
```

Laptop results (Windows, Python 3.10, NumPy 1.26):

| Test | Result |
|---|---|
| Looming sweep | Strength rises smoothly 0 → ~1 on each side, no dips |
| Side | Left looming → jumps right 100%; right → left 100% (where strength ≥ 0.3) |
| Speed | 9–15 ms per decision (65–110 decisions/s); target ≥ 10 |
| Latency at 20 Hz | 300/300 answered, round-trip p50 16 ms / p95 22 ms; ping while busy p95 1 ms |
| Through the dev hub | 200/200 answered, p50 22 ms |
| Reconnect | Server fine after 10 abrupt client crashes; brain re-attaches to a restarted hub |
| Fallback vs live | Strength curves within 0.05–0.10; same jump sides |
| Mode switches mid-game | No dropped replies, no gaps over 100 ms, no strength jump beyond normal noise |
| Live brain stalls 1.5 s | Replies keep coming (longest silence ~250 ms), fails over, recovers |
| No NumPy | Starts on fallback, same replies |
| Long run (10 min at 20 Hz) | Memory flat at 46–47 MB; round trip 14 → 13 ms (rose to ~28 ms mid-run along with the laptop's CPU, then recovered) |

## On the QNX Pi

1. Check NumPy: `python3 -c "import numpy; print(numpy.__version__)"`.
2. Copy `brain/` (with `subnet/`) to the Pi.
3. `python3 brain/bench.py speed` shows which subnetwork meets 10 decisions/s.
   Use the largest one that does: `python3 brain/server.py --subnet brain/subnet/subnet_t10.npz`.
   Cost is mostly per-step Python overhead, so the smaller networks are only
   slightly faster. If even `t20` is too slow, run `--mode fallback` on the Pi
   (still on-device), or host the live brain on the laptop behind the same
   interface.
4. `subnet_t20` is weak: the left Giant Fiber never fires there. Prefer `t10`.
