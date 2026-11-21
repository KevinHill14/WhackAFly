# WhackAFly sound effects

Short clips made with ElevenLabs, in `clips/`: six sound effects (text-to-sound-effects
API) and an announcer countdown (text-to-speech, the built-in "Adam" voice).
All are 16-bit WAV, peak-normalized to about -1 dBFS, with no dead air before the
sound starts, so they fire right on the game event.

## For the Unity developer

Drop `clips/*.wav` into `Assets/Audio/`. Suggested import settings:
one-shots **Decompress On Load**, `fly_buzz_loop` **Compressed In Memory**, all with
**Load In Background** off so the first play has no delay.

| File | Length | Play when | Notes |
|---|---|---|---|
| `glass_break.wav` | 1.5 s | A glass/breakable object is knocked over and breaks | 3D AudioSource at the object |
| `swatter_whoosh.wav` | 0.6 s | The IMU swatter swing passes the speed threshold | Play on the swatter. Randomize pitch 0.9–1.1 so repeats don't sound identical |
| `swatter_slap.wav` | 0.6 s | The swatter hits a surface but misses the fly | 3D at the hit point |
| `swatter_splat.wav` | 0.7 s | The swatter hits the fly (kill) | Play instead of `swatter_slap`, not on top of it |
| `loud_bang.wav` | 1.2 s | Any other loud nearby noise (object falls, player bumps furniture) | 3D at the source |
| `fly_buzz_loop.wav` | 3.0 s loop | Always while the fly is alive | AudioSource on the fly, **Loop on**, Spatial Blend 1 (3D), Logarithmic rolloff, Min ~0.3 m / Max ~8 m. Stop it on kill. Raising pitch slightly (1.0–1.3) with fly speed sells the escape. 24 kHz stereo (44.1 kHz WAV needs a higher ElevenLabs plan) |

### Round-start countdown

| File | Length | Play when |
|---|---|---|
| `countdown_3.wav` | 0.6 s | Round start, t = 0 s |
| `countdown_2.wav` | 0.6 s | t = 1 s |
| `countdown_1.wav` | 0.6 s | t = 2 s |
| `countdown_go.wav` | 0.6 s | t = 3 s, the moment the fly is released and swatting is enabled |
| `countdown_full.wav` | 3.6 s | Alternative: all four already spaced 1 s apart, one `PlayOneShot` at t = 0 |

Use the separate clips if the on-screen "3 / 2 / 1 / GO!" text is driven by a
coroutine (`yield return new WaitForSeconds(1f)` between them). Use
`countdown_full` if you just want one call. Play them 2D (Spatial Blend 0) and
don't send them to the brain; the fly shouldn't hear the announcer.

### Matching the fly brain

The brain server takes `{"type":"sound","value":0-1,"side":...}` for loud events.
Send that message at the same moment you play these clips:

| Clip | Suggested `value` |
|---|---|
| `glass_break` | 1.0 |
| `loud_bang` | 0.8 |
| `swatter_slap` / `swatter_splat` | 0.7 |
| `swatter_whoosh` | 0.3 (only if the swing is near the fly) |

`side` is whichever side of the fly the sound came from. The values are a starting
point; tune them with whoever owns `brain/`.

## Regenerating

```
python generate_sfx.py                  # make only the clips that are missing
python generate_sfx.py swatter_slap     # remake one clip
python generate_sfx.py countdown_go     # remake one countdown line (countdown_full is rebuilt)
python generate_sfx.py --force          # remake all
```

Edit the prompts, lengths and prompt influence in the `EFFECTS` dict at the top of
`generate_sfx.py`, and the spoken lines in `VOICE_LINES`; adding an entry adds a clip. Each run gives a different
take, so listen before replacing a clip you like.

Needs: the `elevenlabs` Python SDK, `python-dotenv`, `ffmpeg` on PATH, and
Eesha's `elevenlabs_client.py` (set `ELEVENLABS_CLIENT_DIR` if it isn't in
`C:\Users\eesha\Downloads\claude`). The API key never appears in this folder.
Cost is about 10–12 credits per second of audio; all six clips cost ~90 credits.
