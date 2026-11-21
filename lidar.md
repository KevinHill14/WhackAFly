# LiDAR Pipeline

## What it does

`lidar/` takes a Polycam Floorplan export (`.glb` or `.zip`) from a LiDAR iPhone and produces two files for Unity: `scan.glb` (the room mesh, recentred) and `room.json` (measurements plus where lights, sounds, props and fly landing spots go). It then updates `data/latest.json`, which Unity checks once per second.

## Flow

```text
iPhone (Polycam Floorplan)
        |  export GLB or zip
        v
server.py (QR upload)   or   ingest.py (local file)
        |
        v
process_scan.py   read GLB, find floor/walls/objects, recentre
        |
        v
placement.py      find surfaces, place items and landing spots
        |
        v
ai_cleanup.py     optional, only with --ai
        |
        v
data/scans/<id>/  scan.glb, room.json, room.rules.json
        |
        v
data/latest.json  Unity sees the new id and loads the room
```

## Setup

The project uses Python 3.12. Run everything from the repo root.

```powershell
py -3.12 -m venv .venv
.venv\Scripts\activate
pip install -r lidar/requirements.txt
```

The commands below assume the environment is active. Otherwise, replace `python` with `.\.venv\Scripts\python.exe`.

## Running it

### Option A: phone upload

```powershell
python lidar/server.py
```

1. Put the phone and laptop on the same Wi-Fi or hotspot.
2. Scan the QR code in the terminal.
3. Choose the Polycam GLB or zip and press Upload.
4. The page shows a summary when done. Unity loads the room by itself.

| Flag | Default | Purpose |
|---|---|---|
| `--port` | `8080` | Server port |
| `--token` | random | Fixed upload token, for testing |
| `--ai` | off | Run the AI cleanup on each upload |

The token changes on each start unless you set `--token`, so rescan the QR code after a restart.

### Option B: a file already on the laptop

```powershell
python lidar/ingest.py path\to\scan.zip
python lidar/ingest.py path\to\scan.glb --ai
```

This processes the file, copies it to `data/uploads/`, and makes it the current room.

### Option C: process only

```powershell
python lidar/process_scan.py path\to\scan.glb --out some\folder
```

This writes the output but does **not** update `latest.json`, so Unity will not load it. Use it to test the pipeline.

## Optional AI cleanup

The AI step suggests small layout edits: remove, move or swap an item.

| Item | Current state |
|---|---|
| Provider | Anthropic (Claude), through the `anthropic` Python package |
| Model | `claude-opus-5-5`, set by `MODEL` in `ai_cleanup.py` |
| Key | Read only from the `ANTHROPIC_API_KEY` environment variable |
| `.env` | **Not loaded by this code.** A key stored there has no effect unless you export it |
| Default | Off. Runs only with `--ai` |
| Planned | Switch to Gemini using `GEMINI_API_KEY`. Not implemented yet |

```powershell
$env:ANTHROPIC_API_KEY = "your-key"
python lidar/server.py --ai
```

With `--ai`, a summary of the room is sent to Anthropic: dimensions, object labels and positions. The mesh is not sent.

Every suggested edit is rechecked against the placement rules. If anything fails, the rule-based layout is kept and the scan still works.

The result is saved in `ai_report.json`, and the upload summary shows it as `aiCleanup`:

| Status | Meaning |
|---|---|
| `off` | `--ai` was not used (no report file is written) |
| `no_key` | `ANTHROPIC_API_KEY` is not set |
| `sdk_missing` | `anthropic` package is not installed |
| `api_error` | The API call failed |
| `invalid_response` | The reply was not in the expected format |
| `no_valid_edits` | No edit passed the checks, or none were proposed |
| `invalid_result` | The final edited layout failed validation |
| `applied` | Edits were used, and `room.ai.json` was written |

## Files

| File | What it does |
|---|---|
| `server.py` | Phone upload page, QR code, token check |
| `ingest.py` | Processes a local file; owns `data/` paths and writes `latest.json` |
| `process_scan.py` | Reads the GLB, builds the room model, writes the outputs |
| `placement.py` | Finds surfaces and places everything; holds most tunable values |
| `ai_cleanup.py` | Optional Claude edit pass and its validation |
| `requirements.txt` | Python dependencies |

Output for each scan, in `data/scans/<id>/`:

| File | Contents |
|---|---|
| `scan.glb` | Original meshes under a new `FlyAI_Root` node that recentres them |
| `room.json` | The layout Unity loads |
| `room.rules.json` | The rule-based layout |
| `room.ai.json` | The AI layout, only if edits were applied |
| `ai_report.json` | Accepted and rejected AI edits, only with `--ai` |

## Processing steps

1. **Read** the file. For a zip, use the largest `.glb` inside. Limit: 300 MB.
2. **Parse** the GLB and reject unsupported files.
3. **Collect parts** using Polycam's groups (`Walls`, `Floors`, `Ceilings`, `Furniture`, `Objects`). Names are only a fallback for ungrouped meshes, so a `Floor Lamp` in `Furniture` stays furniture. Invalid coordinates reject the scan.
4. **Find the floor**: the main flat height and its outer outline. If there are several floor areas, the largest is used and a warning is added. Holes in the floor are kept out of the walkable area.
5. **Recentre** so the floor top is `y = 0` and the floor is centred on `x = 0, z = 0`.
6. **Measure** walls, objects and ceiling height.
7. **Pick the player start**: the spot with the most clearance, facing the room centre.
8. **Detect surfaces**: floor, ceiling, walls, tabletops, seats, storage, beds, ledges.
9. **Map walkable floor** and keep the largest connected area. The start snaps to it. Tight rooms get a second pass with a narrower body, and a warning is added if nothing is walkable.
10. **Place** in this order: window, doors, lights, wall decor, sounds, props, landing spots.
11. **Write** `scan.glb` and `room.rules.json`.
12. **Run AI cleanup** if `--ai` was used.
13. **Write** `room.json`.
14. **Publish** `latest.json` (server and ingest only).

Placement is repeatable: the random seed comes from the file's contents, so the same file gives the same layout.

## Key `room.json` fields

Coordinates are metres, Y up, right-handed glTF. Unity converts with `(x, y, z) -> (-x, y, z)` and `rotationY -> -rotationY`, in `RoomLoader.cs` only.

| Field | Meaning |
|---|---|
| `floor.polygon` | Floor outline as `[x, z]` points |
| `floor.polygonFlat` | Same outline as a flat list; this is what Unity reads |
| `floor.area` | Floor area in m2 |
| `ceilingHeight` | Ceiling height in metres |
| `walls` | Wall start, end, height and thickness |
| `objects` | Scanned furniture: label, centre, size, rotation |
| `surfaces` | Usable surfaces with kind, height and area |
| `playerStart` | Eye position and yaw |
| `landingSpots` | Where the fly may land: position, normal, surface kind |
| `props` | Placed items: type, position, rotation, size |
| `lights` | Type, position, mount, intensity, range, colour |
| `soundAnchors` | Sound name, position, volume, range |
| `window` | One window, `scanned` or `generated` (can be `null`) |
| `doors` | Scanned doors, or one generated door |
| `wallDecor` | Pictures and mirrors |
| `lightCoverage` | Percent of floor within range of a light |
| `layoutSource` | `rules` or `ai` |
| `warnings` | Problems found while processing |

## Placement rules

**All items** must be inside the room, on an allowed surface, clear of walls and other items, and under the ceiling.

- **Window:** uses the first scanned window. If there is none, one is fitted to the clearest wall, away from scanned doors.
- **Doors:** uses every scanned door. If there are none, one is fitted to a wall with open floor in front, away from windows.
- **Openings:** scanned doors, windows and openings are all kept clear of decor, clocks and fly spots.
- **Ceiling lamps:** 1 per 35 m2, rounded up, maximum 10, spread evenly.
- **Table lamps:** 1 per 40 m2, 1 to 6, on tables or storage of at least 0.8 m2.
- **Candles:** up to 2, on tables without a lamp.
- **Floor lamp:** only if no table lamp could be placed.
- **Wall decor:** about 1 per 4.5 m of wall, maximum 8, at least 1 m apart.
- **Sounds:** 2 or 3 sources. Real objects (TV, fridge, sink and so on) are used first, then a radio and a clock.
- **Props:** each large table gets a centrepiece, then smaller items. Other surfaces share a budget of 2 to 8 props. The area around the player start stays clear.
- **Landing spots:** on all surface kinds, up to 400. A spot is kept only if the player can reach it with the swatter: within 2 m of a walkable position, with a clear line, and nothing covering it.

## Tuning

All values below are in `placement.py` unless noted. Re-ingest the scan after changing them.

| To change | Edit |
|---|---|
| Which props exist, where they go, how many | `PROP_TYPES` |
| Items used on tables | `CENTERPIECES`, `TABLE_ITEMS` |
| How crowded surfaces get | `PROP_CAPACITY` |
| Props away from tables | `AREA_PER_OTHER_PROP`, `OTHER_PROP_BUDGET` |
| Light brightness, range, colour | `LIGHT_TYPES` |
| Number of lamps | `AREA_PER_CEILING_LIGHT`, `MAX_CEILING_LIGHTS`, `AREA_PER_LAMP`, `MAX_LAMPS`, `MAX_CANDLES` |
| Number of sounds | `AREA_PER_SOUND`, `SOUND_COUNT` |
| Sounds for scanned objects | `SCANNED_SOUNDS` |
| Wall decor | `WALL_DECOR`, `AREA_PER_WALL_DECOR`, `MAX_WALL_DECOR` |
| Fly spot density | `LANDING_RULES`, `MAX_LANDING_SPOTS` |
| Swatter reach used for spots | `SWAT_REACH` (keep equal to `swatReach` in `PlayerController.cs`) |
| Furniture label mapping | `SURFACE_CLASS` |
| Upload size limit | `MAX_GLB_BYTES` in `process_scan.py` |
| AI edit limits | `MAX_EDITS`, `MAX_COVERAGE_LOSS` in `ai_cleanup.py` |

## Common problems

| Problem | Likely cause | Fix |
|---|---|---|
| Phone cannot open the page | Different network or firewall | Use the same Wi-Fi or hotspot; allow Python through Windows Firewall on port 8080 |
| QR code has the wrong IP | VPN or virtual adapter chosen | Disconnect the VPN, or type the laptop's Wi-Fi IP with the same port and token |
| "Invalid upload link" | Server restarted | Scan the new QR code, or use `--token` |
| "No labelled floor" or "No labelled walls" | Not a Floorplan scan | Rescan in Polycam Floorplan mode and export GLB |
| "The zip has no .glb file" | Wrong export type | Export as GLB |
| "Unsupported GLB extensions" | Export uses compression or similar | Re-export a plain GLB from Polycam |
| "The file is too large" | Over 300 MB | Export smaller, or raise `MAX_GLB_BYTES` |
| `aiCleanup` is `no_key` | Key is not in the environment | Set `ANTHROPIC_API_KEY` in the same terminal; `.env` is not read |
| `aiCleanup` is `api_error` | Bad key, network or model access | See `error` in `ai_report.json`; the rule layout is still used |
| Few or no landing spots | Little walkable floor | Check the scan and `warnings`; toggle spots in Unity with `G` |
| Room is split or too small | Scan has separate floor areas | Rescan as one connected room |
| Unity does not update | Used `process_scan.py`, or wrong data folder | Use `server.py` or `ingest.py`; check the `[FlyAI] Watching` log line |
| "Could not read this scan" or "invalid coordinates" | Damaged or unusual export | Re-export from Polycam; the terminal shows the details |
| "The scan appears to be upside down" | Export has a flipped axis | Re-export from Polycam |
| "Processing failed" on the phone | Unexpected error | Read the traceback in the laptop terminal |
