# Unity Game

## What it does

`WhackAFlyGame` loads the latest scanned room and rebuilds it as a dark, playable space. The player walks it in first person with a swatter and flashlight. The room has fog, unreliable lights, generated sound, timed events and breakable props. Swings add to a damage bill.

**The fly is not in this code yet.** See [Where the fly plugs in](#where-the-fly-plugs-in).

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

## Where the fly plugs in

**Required: report distance every frame.**

```csharp
if (RoomTension.Current != null)
    RoomTension.Current.SetFlyDistance(
        Vector3.Distance(fly.position, Camera.main.transform.position));
```

Tension starts at 5 m and peaks at 0.8 m. If calls stop for 0.25 s, tension fades out.

**Landing spots.** `room.json` has `landingSpots`, each with a position, surface normal and surface kind. Convert with `RoomLoader.ToUnity(...)`. The spots keep the fly in bounds and within swatter reach; the brain chooses among them.

These hooks do not exist yet and will need small additions:

| Need | Current state |
|---|---|
| Get room data | `RoomLoader` keeps it private; add a public property, or read `room.json` |
| Know when a room loads | No event; add one to `RoomLoader` |
| Detect a swat on the fly | `Swing` ignores colliders without `RoomPart`, so a plain fly collider is never hit |
| Swing details | `PlayerController.Swung` has a hit point but no origin or direction, and no point on a miss |
| Damage | Tagging the fly as a `RoomPart` would add it to the damage bill unless handled |

## Known limitations

- The fly and win/lose flow are not implemented.
- There is no menu, and only one theme.
- `L` swaps props, sounds and markers only. Lights stay from the layout that was loaded.
- Landing spots do not update when props are knocked over or shattered.
- Candles and scanned-window lights ignore blackouts and surges.
- Only the room's main window can smash. Any other scanned window is a normal object.
- Generated doors have no collider, so swings hit the wall behind them.
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
| Low frame rate | Press `F`, set `LampSway.MaxDegrees` to 0, or lower `MaxShadowLights` |
