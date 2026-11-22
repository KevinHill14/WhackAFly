# Swing classifier: record, train, export

Record labelled swings from the real swatter, train a small classifier, export it as TensorFlow Lite
for the Raspberry Pi, and run it in the game backend in place of the threshold rules.

Classes: `idle`, `aiming`, `swing`, `flick`, `bump`, plus a **strength** estimate from 0 to 10.

```
ESP32 --ws:8767--> python -m swatter_ml.record (laptop)   record takes  ->  data/takes/
python -m swatter_ml.train (laptop)                        train        ->  models/latest/
ESP32 --ws:8767--> backend/server.py --classifier model (Pi)  ->  "swatter" actions to Unity
```

## Set up (laptop)

```powershell
py -3.12 -m venv .venv
.venv\Scripts\pip install -r swatter_ml/requirements.txt
```

TensorFlow 2.18 needs Python 3.9 to 3.12. Only training needs it; recording needs only `aiohttp`.

## 1. Record (guided terminal screen)

```powershell
.venv\Scripts\python -m swatter_ml.record --person kevin        # --rounds 2 for two passes
```

The ESP32 streams to `ws://<laptop IP>:8767` (set `PI_HOST` in `firmware/src/main.cpp` to the laptop's
address on the hotspot, and stop the backend, which uses the same port).

The screen shows one prompt at a time, such as **SWING from the RIGHT across to the LEFT, strength 6**.
Get ready, press `SPACE`, count 3-2-1, and do it; the take is saved and the next prompt appears.
`S` skips a prompt, `U` deletes the last take (and repeats that prompt), `Q` quits.

One round is 26 prompts, shuffled so the labels are mixed:
- **18 swings:** six directions (right to left, left to right, top to middle, bottom to middle, and the
  two diagonals) at strengths 3 (gentle), 6 (a normal swat) and 10 (as hard as you can).
- **8 things that are not swings:** holding still, aiming slowly, a small wrist flick, a bump.

It warns you if a swing barely moved the sensor or if a "hold still" take moved. Fix the sensor to the
swatter in its final position before the first take, run several rounds, and use more than one person
if you can. Each take is one JSON file in `swatter_ml/data/takes/` (git-ignored), with the swing's
direction saved as extra information. Takes from `backend/collect.py` (CSV) also work for training.

## 2. Train and export

Click **Train model** in the UI, or:

```powershell
.venv\Scripts\python -m swatter_ml.train                                   # every take in data/takes
.venv\Scripts\python -m swatter_ml.train --data swatter_ml/data/takes data/training_data.csv --holdout-person alice
```

It holds out 20% of the takes per class (or one person's), trains, and prints:

- the accuracy and confusion matrix for the Keras model, the float TFLite model and the int8 TFLite model;
- the strength error;
- a check that the plain-Python network gives the same output as Keras.

It writes to `swatter_ml/models/latest/` (git-ignored; use `git add -f` to commit a real model):

| File | What |
|---|---|
| `model_int8.tflite` | int8 weights and activations (about 11 KB), float32 input and output. The default. |
| `model_float.tflite` | The same network in float32. It becomes the default if int8 loses more than 3 points of accuracy. |
| `weights.json` | Float weights for the plain-Python fallback (no TFLite or NumPy needed). |
| `labels.json` | `{"labels": [...]}`: output index → class. Index `len(labels)` is strength / 10. |
| `meta.json` | The preprocessing spec, metrics, confusion matrices and which file is the default. |
| `report.txt` | The training printout. |

**Model:** 160 features → Dense 32 (ReLU) → Dense 16 (ReLU) → softmax over the classes, plus a sigmoid strength head. That is about 5,800 parameters.
- **I/O:** input float32 `[1, 160]`, output float32 `[1, classes + 1]` (the class probabilities, then strength / 10).

## 3. Preprocessing: what the Pi must match exactly

All of this lives in one file, `swatter_ml/features.py` (standard library only). Recording,
training and inference all call it. Each model's `meta.json` stores the spec, and `infer.py`
refuses a model whose spec differs from the code.

1. **Input:** the ESP32's samples: `t` in ms, raw `ax, ay, az` in m/s² (gravity included), and `gx, gy, gz` in rad/s (bias removed).
2. **Resample** to exactly 200 Hz (5 ms) by linear interpolation on `t`. A gap over 100 ms, or time going backwards, starts a fresh stream.
3. **Motion energy** = |gyro| + 0.3 · | |accel| − 9.80665 |.
4. **Window:** 80 samples (0.40 s) with the energy peak at index 52, so 260 ms before the peak and 135 ms after it. Edges are padded with the nearest sample.
   - **Live**, energy ≥ 2.0 starts a peak search. The peak is the maximum over the next 30 samples. The window is classified once its 27 post-peak samples have arrived, then 40 samples are ignored.
   - **Training**, the peak is the take's highest-energy sample. A test checks that live and training windows are identical.
5. **Features:** 20 blocks of 4 samples. Each block gives 8 values, flattened block-major into 160 numbers:
   - the mean of ax, ay and az, each divided by g;
   - the mean of gx, gy and gz, each divided by 10;
   - the mean |gyro| / 10;
   - the maximum of | |accel| − g | / g.
   Each value is clipped to ±8.

**Latency:** the model answers about 135 ms after the swing's peak rotation, because it waits for
the window to finish. Inference itself is far smaller: on this laptop it takes about 0.03 ms
with TFLite, or about 0.6 ms in plain Python including feature extraction. The threshold rules
fire earlier, as the swing crosses 4 rad/s. To answer sooner, raise `PRE_PEAK` in `features.py`
(fewer samples after the peak), then retrain. That trades some accuracy for speed.

## 4. Run it

**Quick inference test** (prints the class and strength for each detected motion):

```powershell
python -m swatter_ml.infer --takes swatter_ml/data/takes        # replay takes through the live path, with a confusion matrix
python -m swatter_ml.infer --synthetic --seconds 30             # synthetic stream; prints the truth next to each prediction
python -m swatter_ml.infer --csv data/training_data.csv         # replay a collect.py CSV
python -m swatter_ml.infer --live --port 8767                   # the real swatter (stdlib WebSocket, runs on the Pi)
```

Add `--backend python` to force the plain-Python network, or `--backend tflite` to require TFLite.

**In the game:** the backend's single swap point is `make_classifier()` in `backend/gesture.py`:

```sh
python3 backend/server.py --classifier model                 # or SWATTER_CLASSIFIER=model sh backend/start.sh
python3 backend/server.py --classifier model --model /path/to/model_dir
```

The default is still `threshold`. If the model cannot load (missing files, a different
preprocessing version), the backend logs why and falls back to the threshold rules, so the game
keeps working. Each swing reaches Unity as a `swatter` message:

```json
{"evt": "swing", "kind": "swing" | "flick", "strength": 0-1, "conf": 0-1, "axis": "X", "dir": "negative", "rate": 12.7, "source": "model"}
```

This keeps the threshold version's `axis`, `dir` and `rate`. The model emits nothing for
`idle`, `aiming` or `bump` (that is the point: a bump or an aim is not a swat). It also emits
nothing when its confidence is below 0.5.

## 5. On the QNX Raspberry Pi 5

Copy these to the Pi, keeping the repo layout:

```
swatter_ml/__init__.py  swatter_ml/features.py  swatter_ml/infer.py
swatter_ml/models/latest/{model_int8.tflite, model_float.tflite, labels.json, meta.json, weights.json}
backend/  brain/            (already there)
```

`infer.py` looks for a Python TFLite interpreter, in this order: `ai_edge_litert`, `tflite_runtime`, `tensorflow`.

- If QNX's AI module from oss.qnx.com provides one under those names (with NumPy), the backend runs `model_int8.tflite`.
- If not, it runs the same network from `weights.json` in plain Python. That needs only the
  standard library, so the game works either way. The log line `backend: tflite:model_int8.tflite`
  or `backend: python` says which one ran.
- If the AI module is C/C++ only, load `model_int8.tflite` with the TFLite C++ API. Use the I/O
  in section 2 and port the steps in section 3 exactly; the Python version is the reference to
  test against.

Check on the Pi with:

```sh
python3 -m swatter_ml.infer --takes swatter_ml/data/takes    # needs some takes copied over
python3 -m swatter_ml.infer --live --port 8767               # with the backend stopped
```

## The firmware

`firmware/src/main.cpp` streams every sample as `{"t","ax","ay","az","gx","gy","gz"}`.

- To record on the laptop, set `PI_HOST` to the laptop's IP on the hotspot and `PI_PORT` to `8767`.
  Or keep the firmware as it is and start the UI with `--esp-port 8765` (the brain must not be running on that machine then).
- The UI also accepts the ESP32 on any path.
- The Wi-Fi password is still written in that file; see `backend/README.md`.

`backend/collect.py` records with different labels (`swing left`, `rotate clockwise`, ...).

- Its CSVs load here as they are, one take per trial, and keep their labels.
- Train on one label set at a time, or relabel takes in the UI.

## Tests

```powershell
python -m unittest swatter_ml.test_swatter_ml -v     # standard library; TFLite checks run if TensorFlow is installed
python -m swatter_ml.synthetic --per-class 40        # write synthetic takes to swatter_ml/data/synthetic_takes
```

The tests cover resampling, live and training windows being identical, feature shape, take
storage, collect.py CSV import, the model interface, rejection of a model with different
preprocessing, the backend falling back to threshold, and TFLite agreeing with plain Python.

**Tested on the laptop with synthetic data only.** A model trained on synthetic takes scores about
95% on held-out synthetic takes. That proves the pipeline, not the real swatter. Real swings need
real recordings.
