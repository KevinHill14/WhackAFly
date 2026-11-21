# Unity Game

## What it does

`WhackAFlyGame` loads the latest scanned room and rebuilds it as a dark, playable space. The player walks it in first person with a swatter and flashlight. The room has fog, unreliable lights, generated sound, timed events and breakable props. Swings add to a damage bill.

The fly is in: `FlyAgent` models it and `FlyBrainClient` talks to the brain server (`brain/server.py`). See [The fly and the brain](#the-fly-and-the-brain).

## Open and run

Requires Unity `6000.0.85f2`. The project uses URP `17.0.4`, glTFast `6.21.0` and Input System `1.20.0` (new input only).

1. Open `WhackAFlyGame` in Unity Hub.
2. Open `Assets/Scenes/SampleScene`.
3. Make sure a scan exists (see `lidar.md`).
4. Press Play.

No scene setup is needed. `RoomLoader` creates itself on Play in any scene, and creates the camera and player if needed.

## Project location

The game looks for its data in this order:

1. The `FLYAI_DATA` environment variable, if set. Set it before launching Unity.
2. A `data/latest.json` in any parent folder of `Assets`.
3. Otherwise it waits on `data/` next to the Unity project folder.

Keep the normal layout:

```text
HackWestern/
  data/              scans and latest.json (not in git)
  lidar/
  unity-scripts/FlyAI/         source of truth for scripts
  WhackAFlyGame/
    Assets/FlyAI/              copy that Unity compiles
    Assets/FlyAI/Art/Resources/  models and textures (only here)
```

Check the Console for `[FlyAI] Watching <path>` to confirm the folder.

## Copying scripts

Edit scripts in `unity-scripts/FlyAI`, then copy them into the project. There is no sync script. From the repo root:

```powershell
Copy-Item -Recurse -Force unity-scripts\FlyAI\* WhackAFlyGame\Assets\FlyAI\
```

- Copy over the top. **Do not mirror or delete the target folder.** `Art/` and the `.meta` files exist only in `Assets/FlyAI`.
- Unity recompiles when it regains focus.
- Commit both copies so they stay in step.

## Load flow

```text
Press Play
  -> RoomLoader starts, finds data folder, sets dark environment
  -> every 1 second: read data/latest.json
  -> new id? (or R pressed)
       -> read room.json and scan.glb
       -> glTFast builds the mesh
       -> RoomVisuals: materials, shell, trim, lights, post-processing,
          atmosphere, events, window, wear, door, ambience
       -> colliders for walls, boundary, floor, ceiling, furniture
       -> RoomProps: props, sound emitters, wall decor
       -> debug boxes and markers (hidden)
       -> place the player at playerStart
       -> remove the previous room
```

If a load fails, the error is logged once as `[FlyAI] Scan load failed` and the previous room keeps running. It retries when `latest.json` changes or you press `R`. A successful reload resets the player position and damage bill.

## Scripts by system

| System | Script | Purpose |
|---|---|---|
| Loading | `RoomLoader` | Watches for scans, loads them, builds colliders and markers; owns coordinate conversion |
| | `RoomData` | C# classes matching `latest.json` and `room.json` |
| | `RoomPart` | Tags a collider so a swing knows what it hit |
| Look | `RoomTheme` | All colours and mood values |
| | `RoomVisuals` | Materials, gap-closing shell, lights, post-processing |
| | `RoomSurfaces` | Textures, generated UVs, baseboards, crown molding |
| | `RoomWindow` | Breakable window with night sky and rain |
| | `RoomDoor` | Closed panel doors |
| | `RoomWear` | Stains, scuffs, cobwebs |
| Atmosphere | `RoomAtmosphere` | Fog, haze, dust, lamp flicker, ping |
| | `RoomEvents` | Blackouts, surges, headlights, lightning |
| | `RoomTension` | Grain and pulsing vignette when the fly is near |
| | `FlashlightFx` | Visible beam and occasional flicker |
| Props | `RoomProps` | Builds props, sound props, wall decor |
| | `PropMeshes` | Generated shapes (phone, fork, swatter parts) |
| | `PropDamage` | Knock, wobble, shatter, squish; also holds lamp sway |
| | `SwatMarks` | Free cosmetic swatter imprints, up to 512 per object |
| Audio | `RoomAudio` | Positional sounds, room tone, rain |
| | `FlySounds` | Generates every sound in code; no audio files |
| Player | `PlayerController` | Movement, aim, flashlight, swings, damage bill, HUD |
| | `SwatterInput` | Keyboard/mouse input; a real swatter can implement `ISwatterInput` |
| | `SwatterModel` | The swatter's 3D model |
| Fly | `FlyAgent` | Fly model, perching and hopping between landing spots, looming from the swatter, escape, swat kill |
| | `FlyBrainClient` | WebSocket client for the brain server; auto-reconnects |
| Scores | `Leaderboard` | Times each round, asks for a name after a swat, shows the top 10, and logs game events to `leaderboard/server.py` |
| Tools | `FlyCamera` | Free debug camera and all debug keys |
| | `Editor/FlyAITextureImport` | Keeps texture import settings correct |

## Controls

### Player

| Input | Action |
|---|---|
| `W` / `S` | Walk forward / back |
| `A` / `D` | Turn left / right |
| Mouse | Aim swatter and flashlight |
| Left click | Swing; faster mouse movement means a stronger hit. Also recaptures the cursor |
| `Q` | Flashlight on/off |
| Hold `Left Ctrl` | Sneak: 40% speed, near-silent footsteps, and the fly is much less likely to notice you |
| `Space` tap | Ping: briefly clears the fog |
| `Space` double tap | Turn 180 degrees |
| `Space` hold 0.6 s | Recentre aim |
| `Tab` | Switch to or from the debug camera |

**Debug camera (after `Tab`):** hold right mouse to look, `WASD` to move, `Q`/`E` down/up, left `Shift` for speed.

### Debug keys (work in both modes)

| Key | Action |
|---|---|
| `F` | Fog, haze and dust on/off |
| `B` | Green object outlines |
| `M` | Prop and light markers |
| `G` | Fly landing spots |
| `V` | Sound source markers |
| `L` | Switch rule-based / AI layout, if the scan has both |
| `C` | Ceiling on/off |
| `R` | Reload the current scan |
| `N` | Trigger blackout |
| `U` | Trigger power surge |
| `H` | Trigger headlights |
| `K` | Trigger lightning (needs a window) |
| Hold `T` | Preview fly-nearby tension |

## Timed events

Timing is seeded from the room outline, so a room gets the same sequence of delays each run.

| Event | First | Repeats after | Length | Effect |
|---|---|---|---|---|
| Blackout | 20-40 s | 30-60 s | about 5-8 s | Lights die, electric sounds stop, lights stutter back |
| Power surge | 35-60 s | 45-85 s | about 9 s | Build to white, slam to black, slow recovery |
| Headlights | 6-14 s | 12-28 s | 5 s | Window-shaped light sweeps the room |
| Lightning | 14-35 s | 22-55 s | about 1.4 s, then thunder | Flash through the window |

- Blackouts and surges never overlap, and keep at least 8 s between them.
- Headlights are skipped during a surge.
- Lightning waits for blackouts, surges and headlights to finish. The `K` key skips this check.
- One lamp per room (when there are 2 or more) is "failing" and flickers more often.

## How to change things

### Theme

Edit `RoomThemes.Main` in `RoomTheme.cs`. To add a theme, add it to `RoomThemes.All`, then change the `theme` field in `RoomLoader`. There is no theme menu yet.

### Lighting

| To change | Edit |
|---|---|
| Overall lamp brightness | `lightDim` or `lightScale` in the theme |
| Ambient light | `ambient` and `ambientDim` in the theme |
| Exposure, contrast, saturation, bloom, vignette | Theme values |
| One lamp type | `LIGHT_TYPES` in `lidar/placement.py`, then re-ingest |
| Lamp multipliers | `BuildLights` in `RoomVisuals.cs` |
| Flashlight | `Build` in `PlayerController.cs` |
| Flicker | `NextGap` and `UpdateLights` in `RoomAtmosphere.cs` |
| Lamp sway | `LampSway.MaxDegrees` in `PropDamage.cs` (0 turns it off) |

### Fog distance

In the theme:

- `visibleChairLengths`: clear distance, measured in the room's typical chair length (0.6 m if no chairs were scanned).
- `fogFadeDistance`: minimum distance over which things fade to dark.
- `fog`: fog colour.
- `hazeOpacity`: strength of the drifting haze.

With no chairs and default values, fog starts at 1.8 m and is solid at 5.4 m.

### Event frequency

All in `RoomEvents.cs`, in seconds. First-run delays are in `Setup`. Repeat delays are in `UpdatePower` (blackout), `UpdateSurge` (surge), `Update` (headlights) and `TriggerLightning` (lightning).

`MinEventGap` sets the quiet time after a blackout or surge. There is no on/off switch. To disable an event, set its first delay in `Setup` very high.

### Props

1. Add `name.glb` to `Assets/FlyAI/Art/Resources/FlyAIProps/`.
2. Add `name` to `PROP_TYPES` in `lidar/placement.py` with its surfaces, radius, height and limit.
3. If it goes on tables, add it to `TABLE_ITEMS` or `CENTERPIECES`. Table-only items not in those lists are never placed.
4. Optional: set its reaction and sound in `PropDamage.cs`, and its cost in `DamageCost` in `PlayerController.cs`.
5. Re-ingest the scan.

A model whose filename matches the type needs no extra code. A missing model logs `[FlyAI] Missing prop model` and is skipped.

### Textures

Replace the files in `Assets/FlyAI/Art/Resources/FlyAITextures/`. Sets are `Wall`, `Floor`, `Ceiling` and `Wood`, each with `_Color` and `_Normal`. Keep the names. Change the tile size in `TileSize` in `RoomSurfaces.cs`. A missing colour map falls back to the flat theme colour.

## The fly and the brain

Start the brain first (`pip install numpy`, then `python brain/server.py`; see `brain/README.md`), then press Play. It connects to `ws://127.0.0.1:8765`. Set `FLYAI_BRAIN` to another URL, or to `off` to skip the server. The Console logs `[FlyAI] Fly brain: connecting to ...`. The bottom-left text shows LIVE or stand-in, the looming value, the brain's escape strength and side, and Giant Fiber spikes. `J` hides it.

How it works: the brain controls the fly, and the game only supplies what the fly sees and forces where it may land.

1. **In:** `FlyAgent` turns the swatter, the player walking at it, the flashlight beam and occasional passing shadows into a looming value (0-1) and the eye that sees it. `FlyBrainClient` sends it to the brain at 20 Hz.
2. **Out:** the brain answers with an escape strength and a jump side.
   - Strength 0.4 or more: the fly takes off. The side sets its first turn, and the strength sets its speed and how many waypoints the flight has.
   - Strength 0.12 to 0.4: the fly walks away from the threat, toward the side the brain chose.
3. **Game rules:** the fly only lands on furniture spots (table, seat, ledge, storage, bed, object), never the ceiling. It walks between neighbouring spots on a surface, perches, grooms, and flies routes through open air. A swing lands 0.12 s after it starts, so a quick escape dodges it.
4. **No server, no reaction.** There is no stand-in. If the brain is off or disconnected the fly does not react, and the panel says so in red.
5. **Raspberry Pi:** run `brain/server.py` on the Pi and set `FLYAI_BRAIN=ws://<pi-ip>:8765` before launching Unity.

The brain only models the looming-to-escape circuit, so walking and grooming are game code. The brain decides when the fly takes off, which way it goes, and when it walks away.

The panel at the top right shows the connection, the stimulus, the brain's strength and side, and spike counts for the eye inputs, the Giant Fiber and the escape neurons. `J` hides it.

Light and events change what the fly sees. The panel's Light line shows the light at the fly, its vision (0.12 in the dark, 1.0 when well lit) and the flashlight beam on it.
- **Vision:** swatter, player and shadow stimuli are scaled by vision. The fly sees by the lamps and flashlight at its own spot, or the lamps on the threat. Walls and furniture block most lamp light. A blackout leaves it nearly blind.
- **Flashlight on the fly** is a restless stimulus (up to 0.4), and passing shadows come faster while it is lit.
- **Events:** a power surge floods it with looming that flicks between eyes. Lightning and headlights add looming from the window side. Lights cutting out give one startle.
- **Sounds:** a swing landing, a swing missing and glass breaking (`HearNoise`) become looming pulses from where the sound came from, scaled by distance (8 m, glass 14 m). The brain then decides whether the fly bolts. Sounds work in the dark.

Finding it: the buzz is loud in flight and muffled when the fly is behind you, so you can turn toward it by ear. A perched fly gives a short buzz every 3-6 s. About 1 s after it lands, a light blue marker and glow slowly brighten over 7 s; the marker sits on the fly, or at the screen edge with an arrow when it is off screen. It goes dark again when the fly takes off. `BeaconDelay` and `BeaconRamp` in `FlyAgent.cs` set the timing. The fly also reports its distance to `RoomTension` every frame. Tuning constants are at the top of `FlyAgent.cs`.

## Leaderboard

After the fly is swatted, a panel asks for a name (type it, `Enter` submits, `Esc` skips). The run goes to the leaderboard server, and the panel then shows the top 10 with your row in green. `R` starts a new round; `Esc` hides the board. While the name box is open, game keys are ignored so letters spell the name. The last name used is remembered.

**Score.** A round runs from the room loading (or `R`) to the swat. Lowest damage bill wins; equal bills go to the faster time. Damage done after the swat does not count. Time is real time, so hit-stop and the debug camera do not pause it.

**Why a server.** The game never holds database credentials. `Leaderboard.cs` only speaks HTTP to `leaderboard/server.py`, which holds the connection string and talks to Postgres on Tiger Cloud (Tiger Data, formerly Timescale). Requests are `UnityWebRequest` coroutines, so the game never waits on the network.

**Game log.** The same server also stores game events for a spectator dashboard (live feed, replays). Each round gets an id, and `Leaderboard.Log` queues events that are sent in batches about once a second:

| Event | When | Fields |
|---|---|---|
| `round_start` | Room loaded or `R` | theme |
| `swing` | Every swing | damage, data: hit, target, part, strength, x/y/z, running bill |
| `fly_escape` | The brain makes the fly take off | data: strength, side, stimulus source, fly x/y/z |
| `fly_flee` | The brain makes the fly walk away (once per flee) | data: strength, side, source |
| `fly_swatted` | The fly dies, named or not | damage, seconds, theme |
| `round_result` | A name is submitted (via `POST /scores`) | player name, damage, seconds, theme |

To log something new, call `Leaderboard.Log("kind", Leaderboard.Data("key", value, ...))` from any script. Kinds are lowercase, up to 32 characters.

**Optional by design.** With the server, Tiger Cloud or the internet down, the game plays normally. A failed event batch is dropped, the Console says so once, and the game retries after 15 s. The server itself starts even if the database is unreachable, and answers `503` within about 5 s until it comes back.

**Set up Tiger Cloud (once).**

1. Sign up at Tiger Data, create a service (the free tier is enough), and copy its connection string from the service's Connect page. It looks like `postgres://tsdbadmin:...@....tsdb.cloud.timescale.com:PORT/tsdb?sslmode=require`.
2. Copy `leaderboard/.env.example` to `leaderboard/.env` and paste it in as `LEADERBOARD_DATABASE_URL`. `.env` is git-ignored; never commit it.
3. Install the one dependency and start the server. On first connect it creates the `game_events` table (a hypertable on Tiger Cloud) and the `leaderboard` view.

```powershell
pip install -r leaderboard/requirements.txt
```

```powershell
python leaderboard/server.py
```

It listens on `http://127.0.0.1:8766` and prints `[leaderboard] listening on ...`. Then press Play. The Console logs `[FlyAI] Leaderboard at ...`.

- `FLYAI_LEADERBOARD` sets another server URL (for example a laptop on the LAN; also set `LEADERBOARD_HOST=0.0.0.0` on that server). `off` turns the leaderboard and the event log off, and a swat then just shows the message as before.
- If the server is down, the board says "Leaderboard offline" and the game carries on. That run is not saved.
- It prints `[leaderboard] database ready (game_events hypertable)` once it reaches Tiger Cloud.

**API** (all JSON):

| Request | Result |
|---|---|
| `GET /scores?limit=10` | `{"scores": [{"name", "damage", "seconds", "theme", "at"}, ...]}`, best first (limit 1-100) |
| `POST /scores` with `{"round_id", "name", "damage", "seconds", "theme"}` | `201 {"rank": 3, "scores": [top 10]}`. One result per round. Names are trimmed to 20 characters. Bad input is `400` |
| `POST /events` with `{"round_id", "events": [{"kind", "t", "damage"?, "seconds"?, "theme"?, "data"?}]}` | `201 {"stored": n}`. Up to 500 events per batch; `t` is seconds since the round started |
| `GET /health` | `{"ok": true, "database": true}` (`database` is false while Tiger Cloud is unreachable) |

**Tables.** One time-series table holds everything. On Tiger Cloud it is a TimescaleDB hypertable on `time`:

`game_events(time, round_id, kind, round_time, player_name, damage, seconds, theme, data jsonb)`

The leaderboard is a view over the named `round_result` rows:

```sql
SELECT player_name, damage, seconds, theme, created_at
FROM leaderboard
ORDER BY damage, seconds, created_at
LIMIT 10;
```

Dashboard queries read `game_events` directly. For example, a live feed is `SELECT * FROM game_events ORDER BY time DESC LIMIT 50`, and a replay is `SELECT * FROM game_events WHERE round_id = '...' ORDER BY round_time`.

**Test.** `python leaderboard/test_server.py` starts the server and runs 25 HTTP checks: ranking, ties, top 10, one result per round, the event log, validation, injection, and starting with the database down. It uses `LEADERBOARD_TEST_DATABASE_URL` if set (`game_events` there is dropped first, so never point it at the real board), otherwise a temporary local Postgres from `pip install pgserver`. Plain Postgres has no TimescaleDB, so the hypertable step only runs on Tiger Cloud.

## Known limitations

- Win/lose flow is minimal: a squashed fly shows a message and the leaderboard, and `R` reloads the room.
- Anyone who can reach the leaderboard server can post any score; there is no anti-cheat. Keep it on `127.0.0.1` or a trusted LAN.
- The fly has one hop-and-perch behaviour and no crawling. Its swat check ignores walls and furniture in between.
- There is no menu, and only one theme.
- `L` swaps props, sounds and markers only. Lights stay from the layout that was loaded.
- Landing spots do not update when props are knocked over or shattered.
- Candles and scanned-window lights ignore blackouts and surges.
- Only the room's main window can smash. Any other scanned window is a normal object.
- Generated doors have no collider, so swings hit the wall behind them.
- The swatter (reach, width and model) grows 10% of the original for every $1000 of damage, with no cap. `SwatterGrowthStep` in `PlayerController.cs` sets the step.
- Every hit on an object is billed, so repeat swings add up. Imprints never add to the bill.
- Standalone builds are untested. Shaders are looked up by name at runtime and may be stripped from a build.

## Troubleshooting

| Problem | Check |
|---|---|
| Nothing loads, empty dark view | Does `data/latest.json` exist? Read the `[FlyAI] Watching` path |
| `Scan load failed` in the Console | The scan is bad or from an older version. Re-run ingest, then Unity loads it automatically |
| Script changes have no effect | Copy from `unity-scripts` and let Unity recompile |
| Compile errors about glTFast, URP or Input System | Let Package Manager restore packages; confirm the Unity version |
| Pink materials | The project must use URP |
| Props missing | Look for `Missing prop model`; confirm `Art/Resources/FlyAIProps` exists |
| Walls look flat | Check `Art/Resources/FlyAITextures`; keep `_Normal` in normal map filenames |
| Too dark to test | Press `F`, tap `Space`, or raise `lightDim` and `ambientDim` |
| Mouse or keys do nothing | Click the Game view to give it focus and capture the cursor |
| Player stuck | Press `R` to respawn, or `Tab` to inspect |
| `L` says no AI layout | The scan needs both `room.ai.json` and `room.rules.json` |
| "Leaderboard offline" | Is `python leaderboard/server.py` running? Check `FLYAI_LEADERBOARD`. The server prints database errors itself |
| Server says set `LEADERBOARD_DATABASE_URL` | Create `leaderboard/.env` from `.env.example` |
| Low frame rate | Press `F`, set `LampSway.MaxDegrees` to 0, or lower `MaxShadowLights` |
