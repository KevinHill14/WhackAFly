"""Generate WhackAFly sound effects with the ElevenLabs text-to-sound-effects API.

Usage:
    python generate_sfx.py              # generate any clip that is missing
    python generate_sfx.py --force      # regenerate every clip
    python generate_sfx.py glass_break  # (re)generate only the named clips

Output: clips/<name>.wav (16-bit PCM, Unity imports it directly).
Cost: measured at about 10-12 credits per second of audio on the Starter plan.

The API key is never read here. It comes from the existing client module
(elevenlabs_client.py, which loads ELEVENLABS_API_KEY from its .env.local).
Point ELEVENLABS_CLIENT_DIR at the folder holding that module if it moves.
Needs ffmpeg on PATH to convert the downloaded audio to WAV.
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

CLIENT_DIR = os.environ.get("ELEVENLABS_CLIENT_DIR", r"C:\Users\eesha\Downloads\claude")
OUT_DIR = Path(__file__).resolve().parent / "clips"

# name -> generation settings. Names match the brain's sound events.
EFFECTS = {
    "glass_break": dict(
        text="A drinking glass falls and shatters on a hard floor, sharp crash with tinkling shards, close up, dry room",
        duration=1.5, influence=0.6),
    "swatter_whoosh": dict(
        text="A single fast whoosh of a plastic fly swatter swung through the air, short swish, no impact",
        duration=0.6, influence=0.7),
    "swatter_slap": dict(
        text="Loud sharp slap of a fly swatter hitting a wooden tabletop, one crisp smack, very close, punchy",
        duration=0.6, influence=0.7),
    "swatter_splat": dict(
        text="A fly swatter smacks and squishes a fly, sharp slap with a small wet splat, single hit",
        duration=0.7, influence=0.6),
    "loud_bang": dict(
        text="A heavy object falls off a shelf and hits the floor nearby, loud thud and bang in a small quiet room",
        duration=1.2, influence=0.5),
    "fly_buzz_loop": dict(
        text="Steady continuous drone of a housefly's wings buzzing, constant volume, no pauses, no clicks, no other sounds",
        duration=3.0, influence=0.8, loop=True),
}


def load_client():
    sys.path.insert(0, CLIENT_DIR)
    from elevenlabs_client import client  # loads the key from .env.local
    return client


def peak_gain_db(path, target_db=-1.0):
    """Gain that brings the clip's peak to target_db, so all clips start equally loud."""
    r = subprocess.run(["ffmpeg", "-hide_banner", "-i", path, "-af", "volumedetect", "-f", "null", "-"],
                       capture_output=True, text=True)
    for line in r.stderr.splitlines():
        if "max_volume" in line:
            return target_db - float(line.split(":")[1].split()[0])
    return 0.0


def generate(client, name, spec):
    kwargs = dict(
        text=spec["text"],
        duration_seconds=spec["duration"],
        prompt_influence=spec["influence"],
        model_id="eleven_text_to_sound_v2",
    )
    loop = spec.get("loop", False)
    if loop:
        # Uncompressed WAV keeps the loop seamless (MP3 pads the ends).
        # 44.1 kHz WAV needs the Pro tier, so the loop is 24 kHz stereo.
        kwargs.update(loop=True, output_format="wav_24000")
        suffix = ".wav"
    else:
        kwargs.update(output_format="mp3_44100_128")
        suffix = ".mp3"

    audio = b"".join(client.text_to_sound_effects.convert(**kwargs))
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp.write(audio)
    out = OUT_DIR / f"{name}.wav"
    try:
        gain = peak_gain_db(tmp.name)
        filters = f"volume={gain:.2f}dB"
        if not loop:
            # Cut silence before the hit so it lines up with the game event.
            filters = "silenceremove=start_periods=1:start_threshold=-45dB," + filters
        subprocess.run(
            ["ffmpeg", "-y", "-loglevel", "error", "-i", tmp.name,
             "-af", filters, "-c:a", "pcm_s16le", str(out)],
            check=True)
    finally:
        os.unlink(tmp.name)
    return out


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("names", nargs="*", help="clips to generate (default: all missing)")
    p.add_argument("--force", action="store_true", help="regenerate even if the file exists")
    args = p.parse_args()

    if shutil.which("ffmpeg") is None:
        sys.exit("ffmpeg not found on PATH")
    unknown = [n for n in args.names if n not in EFFECTS]
    if unknown:
        sys.exit(f"unknown clip(s): {', '.join(unknown)}. Known: {', '.join(EFFECTS)}")

    OUT_DIR.mkdir(exist_ok=True)
    explicit = bool(args.names)
    todo = [n for n in (args.names or EFFECTS)
            if explicit or args.force or not (OUT_DIR / f"{n}.wav").exists()]
    if not todo:
        print("All clips exist. Use --force to regenerate.")
        return
    secs = sum(EFFECTS[n]["duration"] for n in todo)
    print(f"Generating {len(todo)} clip(s), {secs:.1f}s total, ~{secs * 12:.0f} credits")

    client = load_client()
    for n in todo:
        print(f"  {n} ...", end=" ", flush=True)
        print(generate(client, n, EFFECTS[n]).name)


if __name__ == "__main__":
    main()
