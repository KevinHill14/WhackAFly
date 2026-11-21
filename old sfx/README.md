# Old (generated) sfx

`FlySounds_generated_backup.cs.txt` is FlySounds.cs as it was before the recorded clips were added
(every sound synthesised in code). `GameRound`, `FlyAgent` and `RoomEvents` `*_before_sfx.cs.txt` are the
other scripts as they were before the change. Kept outside Assets so they are not compiled.

Sounds replaced by recordings in `WhackAFlyGame/Assets/FlyAI/Art/Resources/FlyAISfx/`:

| In-game sound | Recording (origin/sfx) |
|---|---|
| announce_3 / announce_2 / announce_1 | countdown_3 / _2 / _1 |
| fly_buzz | fly_buzz_loop |
| window_smash, impact_glass | glass_break |
| swing_whoosh | swatter_whoosh |
| impact_wall, impact_wood | swatter_slap |
| fly_splat (new; fly swatted, was impact_squish) | swatter_splat |
| announce_go (new; hunt starts, after the whistle) | countdown_go |
| surge_slam (new; power surge slam to black) | loud_bang |

The generated versions still exist as the fallback in FlySounds.cs. Delete a WAV from
Resources/FlyAISfx and the generated sound plays again (for the new names with no generated version, nothing plays,
except fly_splat which falls back to impact_squish). countdown_full is unused.
