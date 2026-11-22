r"""Guided swing recorder: a full-screen terminal UI that tells you what to do and saves each take as training data.

    .venv\Scripts\python -m swatter_ml.record --person kevin

The ESP32 streams samples to ws://<this computer>:8767 (set PI_HOST in firmware/src/main.cpp to this
computer's address on the hotspot, and stop the backend, which uses the same port).

The screen shows one prompt at a time, for example "SWING from the RIGHT across to the LEFT, strength 6".
Get ready, press SPACE, count down 3-2-1, and do it. The take is saved and the next prompt appears.

    SPACE  record this prompt      S  skip it      U  undo the last take      Q  quit

A session is a shuffled list of swings (six directions at strengths 3, 6 and 10) mixed with the things
that must NOT count as a swing: holding still, aiming slowly, a small wrist flick, a bump. Run it again
for more data (--rounds 2 for two passes). Takes land in swatter_ml/data/takes/ (git-ignored), with the
direction saved as extra information. Train with:  .venv\Scripts\python -m swatter_ml.train

Fill the gaps (only the non-swings the data is short of, hands-free):

    .venv\Scripts\python -m swatter_ml.record --person kevin --fill

Press SPACE once and it runs every prompt in a row by itself. A take that does not match its prompt is
not saved and the prompt repeats.   P pause   S skip   U undo the last take   Q quit
--target sets how many takes each non-swing class should end up with (default 20).
"""
import argparse
import asyncio
import json
import math
import os
import random
import re
import sys
import threading
import time

from aiohttp import web, WSMsgType

from . import takes as T

KEYS = ("ax", "ay", "az", "gx", "gy", "gz")
STRENGTH_WORDS = {3: "gentle", 6: "a normal swat", 10: "as hard as you can"}

# (direction id, what to do)
SWINGS = [
    ("left", "Swing from the RIGHT side across to the LEFT"),
    ("right", "Swing from the LEFT side across to the RIGHT"),
    ("overhead", "Swing from the TOP straight down to the MIDDLE"),
    ("upward", "Swing from the BOTTOM up to the MIDDLE"),
    ("diag_left", "Swing diagonally from TOP-RIGHT down to BOTTOM-LEFT"),
    ("diag_right", "Swing diagonally from TOP-LEFT down to BOTTOM-RIGHT"),
]
STRENGTHS = (3, 6, 10)
# Not swings: the model has to learn these are different.
OTHERS = [
    ("idle", "", 0, "Hold the swatter STILL, like you are waiting for the fly to land"),
    ("idle", "", 0, "Hold it still, then lower it slowly to your side"),
    ("aiming", "", 0, "AIM slowly: point it around the room, no swing"),
    ("aiming", "", 0, "AIM slowly: track an imaginary fly across the wall"),
    ("flick", "", 4, "A small quick WRIST FLICK: not a full swing"),
    ("flick", "", 4, "A small quick wrist flick, the other way"),
    ("bump", "", 5, "BUMP the swatter lightly against something (a table, your leg)"),
    ("bump", "", 5, "Bump it against something again, a bit harder"),
]

# --fill: more varied non-swings, including the near misses the model confuses with a swing.
FILL_PROMPTS = {
    "idle": [(0, "Hold the swatter STILL, ready to swing"),
             (0, "Hold it STILL, down at your side"),
             (0, "Hold it STILL, up by your shoulder"),
             (0, "Hold it still, then lower it SLOWLY")],
    "aiming": [(0, "AIM slowly: point it around the room"),
               (0, "AIM slowly: track a fly across the wall"),
               (0, "AIM slowly: from the floor up to the ceiling"),
               (0, "AIM slowly: draw a small circle in the air")],
    "flick": [(4, "Small WRIST FLICK upward, not a full swing"),
              (4, "Small WRIST FLICK downward, not a full swing"),
              (4, "Small WRIST FLICK to the left, not a full swing"),
              (4, "Small WRIST FLICK to the right, not a full swing")],
    "bump": [(5, "BUMP it lightly against the table"),
             (5, "BUMP it lightly against your leg"),
             (5, "TAP it twice, lightly, on the table"),
             (5, "BUMP it lightly against the wall or a chair")],
}

RESET, BOLD, DIM = "\x1b[0m", "\x1b[1m", "\x1b[2m"
RED, GREEN, YELLOW, CYAN, GREY = "\x1b[31m", "\x1b[32m", "\x1b[33m", "\x1b[36m", "\x1b[90m"
WIDTH = 72


class Stream:
    def __init__(self):
        self.rows = []
        self.recording = False
        self.last_at = 0.0
        self.peer = ""
        self.recent = []        # arrival times, to show the live rate
        self.last = None        # latest sample, for the live readout
        self.gpeaks = []        # (time, gyro magnitude) of the last half second

    def add(self, text):
        try:
            m = json.loads(text)
            row = [float(m["t"])] + [float(m[k]) for k in KEYS]
        except (ValueError, KeyError, TypeError):
            return
        self.add_row(row)

    def add_row(self, row):
        now = time.monotonic()
        self.last = row
        self.gpeaks.append((now, math.sqrt(row[4] ** 2 + row[5] ** 2 + row[6] ** 2)))
        if len(self.gpeaks) > 150:
            del self.gpeaks[:75]
        self.last_at = now
        self.recent.append(now)
        if len(self.recent) > 400:
            del self.recent[:200]
        if self.recording:
            self.rows.append(row)

    @property
    def live(self):
        return time.monotonic() - self.last_at < 0.5

    def gyro_peak(self):
        now = time.monotonic()
        return max([g for t, g in self.gpeaks if now - t < 0.5] or [0.0])

    @property
    def rate(self):
        now = time.monotonic()
        n = sum(1 for t in self.recent if now - t < 1.0)
        return n


LIVE = re.compile(r"gyro\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s*rad/s\s*\|\s*lin\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)")
PAIR = re.compile(r"\b(ax|ay|az|lx|ly|lz|gx|gy|gz):(-?[0-9.]+)")


def find_port():
    from serial.tools import list_ports
    ports = list(list_ports.comports())
    for p in ports:
        text = f"{p.description} {p.manufacturer} {p.hwid}".lower()
        if "303a" in text or "seeed" in text or "esp32" in text:    # Espressif USB
            return p.device
    return ports[0].device if ports else None


class UsbReader(threading.Thread):
    """Reads the ESP32 over its USB cable. The board prints  lx ly lz gx gy gz  lines once it is sent 'p'.
    If the line also has ax ay az (raw acceleration, gravity included) those are used; otherwise the
    linear acceleration (gravity already removed) stands in for them and `linear` is set."""

    def __init__(self, stream, port):
        super().__init__(daemon=True)
        self.stream, self.port = stream, port
        self.error = ""
        self.linear = False
        self.stop = False
        self.opened = False

    def run(self):
        try:
            import serial
            s = serial.Serial(self.port, 115200, timeout=0.2)
        except Exception as e:
            self.error = f"cannot open {self.port}: {e}  (close any serial monitor or bridge using it)"
            return
        self.opened = True
        time.sleep(1.0)
        s.reset_input_buffer()
        s.write(b"p")                        # toggles the raw stream on
        asked = time.monotonic()
        buf = b""
        t0 = time.monotonic()
        try:
            while not self.stop:
                buf += s.read(512)
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    text = line.decode("ascii", "ignore")
                    if "Raw streaming OFF" in text:        # we toggled it off by mistake: switch it back on
                        s.write(b"p")
                        asked = time.monotonic()
                    m = LIVE.search(text)          # "gyro gx gy gz rad/s | lin lx ly lz m/s2 | ..." (the combined test firmware)
                    if m:
                        v = [float(x) for x in m.groups()]
                        self.linear = True
                        self.stream.add_row([(time.monotonic() - t0) * 1000.0] + v[3:6] + v[0:3])
                        continue
                    kv = dict(PAIR.findall(text))
                    if not all(k in kv for k in ("gx", "gy", "gz")):
                        continue
                    if all(k in kv for k in ("ax", "ay", "az")):
                        acc = [float(kv[k]) for k in ("ax", "ay", "az")]
                    elif all(k in kv for k in ("lx", "ly", "lz")):
                        acc = [float(kv[k]) for k in ("lx", "ly", "lz")]
                        self.linear = True
                    else:
                        continue
                    self.stream.add_row([(time.monotonic() - t0) * 1000.0] + acc + [float(kv[k]) for k in ("gx", "gy", "gz")])
                if not self.stream.live and time.monotonic() - asked > 12:    # silent: 'p' may have toggled it off
                    s.write(b"p")
                    asked = time.monotonic()
        except Exception as e:
            self.error = f"serial port problem: {e}"
        finally:
            try:
                s.close()
            except Exception:
                pass


async def serve(stream, port):
    async def handler(request):
        ws = web.WebSocketResponse(heartbeat=10)
        await ws.prepare(request)
        stream.peer = request.remote or ""
        async for msg in ws:
            if msg.type == WSMsgType.TEXT:
                stream.add(msg.data)
        return ws

    app = web.Application()
    app.router.add_get("/", handler)
    runner = web.AppRunner(app)
    await runner.setup()
    await web.TCPSite(runner, "0.0.0.0", port).start()
    return runner


def build_session(rounds, seed=None):
    rng = random.Random(seed)
    items = []
    for _ in range(rounds):
        batch = [{"label": "swing", "direction": d, "strength": s, "text": text}
                 for d, text in SWINGS for s in STRENGTHS]
        batch += [{"label": l, "direction": d, "strength": s, "text": text} for l, d, s, text in OTHERS]
        rng.shuffle(batch)
        items += batch
    return items


def build_fill(found, target, seed=None):
    """Just enough of each non-swing to bring it up to `target` takes, shuffled so the labels mix."""
    rng = random.Random(seed)
    items = []
    for label, prompts in FILL_PROMPTS.items():
        order = list(prompts)
        rng.shuffle(order)
        for i in range(max(0, target - found.get(label, 0))):
            strength, text = order[i % len(order)]
            items.append({"label": label, "direction": "", "strength": strength, "text": text})
    rng.shuffle(items)
    return items


def fill_problem(label, rows):
    """Why this take does not match its prompt, or ''. The limits come from the takes already recorded."""
    gyro = max(math.sqrt(r[4] ** 2 + r[5] ** 2 + r[6] ** 2) for r in rows)
    jolt = max(abs(math.sqrt(r[1] ** 2 + r[2] ** 2 + r[3] ** 2) - 9.81) for r in rows)
    if label == "idle" and gyro > 2.6:
        return f"that moved too much ({gyro:.1f} rad/s). Hold it steadier."
    if label == "aiming" and gyro < 0.4:
        return "it barely moved. Keep it moving, slowly."
    if label == "aiming" and gyro > 3.0:
        return f"that was too fast for aiming ({gyro:.1f} rad/s). Slower."
    if label == "flick" and gyro < 5.0:
        return f"that flick was too small ({gyro:.1f} rad/s). A bit snappier."
    if label == "bump" and jolt < 8.0:
        return "I did not feel a bump. Tap it a little firmer."
    return ""


def counts():
    found = {}
    for t in T.load_all():
        label = t.get("label")
        found[label] = found.get(label, 0) + 1
    return found


def check(label, rows):
    """A plain-words warning if the take does not look like what was asked for, else ''."""
    if len(rows) < 50:
        return f"only {len(rows)} samples arrived: is the ESP32 streaming?"
    peak = max(math.sqrt(r[4] ** 2 + r[5] ** 2 + r[6] ** 2) for r in rows)   # rad/s
    if label in ("swing",) and peak < 2.0:
        return f"I barely saw any movement (peak {peak:.1f} rad/s). Swing harder, or press U to redo."
    if label == "idle" and peak > 1.5:
        return f"that moved (peak {peak:.1f} rad/s). Press U to redo it, holding still."
    return ""


class Screen:
    def __init__(self):
        os.system("")                       # lets Windows terminals draw colours
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
        sys.stdout.write("\x1b[?25l\x1b[2J")

    def draw(self, lines):
        out = ["\x1b[H"]
        for line in lines:
            out.append(line + "\x1b[K\n")
        out.append("\x1b[J")
        sys.stdout.write("".join(out))
        sys.stdout.flush()

    def close(self):
        sys.stdout.write("\x1b[?25h" + RESET + "\n")
        sys.stdout.flush()


def bar(value, total, width=20, color=GREEN):
    filled = round(width * value / max(1, total))
    return color + "█" * filled + GREY + "░" * (width - filled) + RESET


FOOTER = "[SPACE] record    [S] skip    [U] undo last take    [Q] quit"


def frame(stream, session, index, state, found, message, big="", sub=""):
    n = len(session)
    status = f"{GREEN}● live {stream.rate} Hz{RESET}" if stream.live else f"{RED}● NO DATA from the ESP32{RESET}"
    rule = GREY + "─" * WIDTH + RESET
    lines = [
        f" {BOLD}{CYAN}WHACK A FLY  swing trainer{RESET}" + " " * 16 + status,
        rule,
    ]
    if index < n:
        item = session[index]
        lines.append(f" Take {BOLD}{index + 1}{RESET} of {n}   {bar(index, n, 30)}")
        lines.append("")
        title = item["label"].upper()
        lines.append(f"   {BOLD}{YELLOW}{title}{RESET}   {item['text']}")
        if item["label"] == "swing":
            s = item["strength"]
            lines.append(f"   Strength  {bar(s, 10, 10, YELLOW)}  {BOLD}{s}{RESET} / 10   {DIM}({STRENGTH_WORDS[s]}){RESET}")
        else:
            lines.append("")
    else:
        lines += ["", f"   {BOLD}{GREEN}All done!{RESET}  {n} prompts finished.", ""]
    lines.append("")
    lines.append(f"   {BOLD}{big}{RESET}")
    lines.append(f"   {sub}")
    lines.append("")
    lines.append(f" {message}")
    lines.append(rule)
    if stream.last:
        r = stream.last
        accel = math.sqrt(r[1] ** 2 + r[2] ** 2 + r[3] ** 2)
        g = stream.gyro_peak()
        lines.append(f" Sensor   gyro {r[4]:+6.2f} {r[5]:+6.2f} {r[6]:+6.2f} rad/s    accel {accel:5.1f} m/s2")
        lines.append(f" Motion   {bar(min(g, 10.0), 10, 30, YELLOW)}  {g:4.1f} rad/s   {DIM}(move the swatter: this bar should jump){RESET}")
    else:
        lines.append(f" Sensor   {RED}no readings yet{RESET}")
        lines.append("")
    lines.append(" Saved so far:  " + "   ".join(f"{k} {v}" for k, v in sorted(found.items())) if found else " Saved so far:  nothing yet")
    lines.append(f" {DIM}{FOOTER}{RESET}")
    return lines


def read_key():
    """One key press, or ''. Windows console only (that is where the recording laptop is)."""
    import msvcrt
    if msvcrt.kbhit():
        ch = msvcrt.getwch()
        if ch in ("\x00", "\xe0"):
            msvcrt.getwch()
            return ""
        return ch.lower()
    return ""


async def run(args):
    stream = Stream()
    runner = None
    usb = None
    if args.wifi:
        try:
            runner = await serve(stream, args.port)
        except OSError as e:
            raise SystemExit(f"cannot listen on port {args.port}: {e}\nIs the backend or another recorder using it?")
    else:
        port = args.usb or find_port()
        if not port:
            raise SystemExit("no ESP32 found on USB: plug it in, or pass --usb COM4 (or use --wifi)")
        print(f"Reading the ESP32 on {port} ...")
        usb = UsbReader(stream, port)
        usb.start()
        for _ in range(60):                  # up to ~6 s for the first samples
            await asyncio.sleep(0.1)
            if stream.live or usb.error:
                break
        if usb.error:
            raise SystemExit(usb.error)
    global FOOTER
    found = counts()
    if args.fill:
        session = build_fill(found, args.target)
        if not session:
            if usb:
                usb.stop = True
            print(f"Nothing to fill: every non-swing class already has {args.target} takes. Raise --target for more.")
            return
        FOOTER = "[SPACE] start / resume    [P] pause    [S] skip    [U] undo last take    [Q] quit"
    else:
        session = build_session(args.rounds)
    screen = Screen()
    index = 0
    last_id = None
    running = False                     # --fill: moving through the prompts by itself
    message = (f"{len(session)} takes, about {len(session) * (args.countdown + args.seconds) / 60:.0f} minutes. "
               "Press SPACE once and it runs by itself." if args.fill else "Hold the swatter ready, then press SPACE.")
    auto = args.auto
    warned_linear = False
    try:
        while True:
            if args.fill and index >= len(session):
                break
            # ---- waiting for the player ----
            key = " " if args.fill and running else ""
            while not key:
                if auto:
                    key = " " if stream.live else ""
                else:
                    key = read_key()
                screen.draw(frame(stream, session, index, "ready", found, message,
                                  "Press SPACE when you are ready." if index < len(session) else "Press Q to finish.",
                                  "" if stream.live else f"{RED}Waiting for the ESP32 ({'wifi port ' + str(args.port) if args.wifi else usb.port})...{RESET}"))
                if usb and usb.error:
                    message = f"{RED}{usb.error}{RESET}"
                elif usb and usb.linear and not warned_linear:
                    warned_linear = True
                    message = (f"{YELLOW}Note: over USB this firmware sends acceleration with gravity removed, "
                               f"so these takes will not match the Wi-Fi stream.{RESET}")
                if key in ("q", " ", "s", "u") or (auto and key):
                    break
                key = ""
                await asyncio.sleep(0.05)
            if key == "q":
                break
            if key == "u":
                if last_id and T.delete(last_id):
                    found = counts()
                    index = max(0, index - 1)
                    message = f"{YELLOW}Deleted the last take. Do this prompt again.{RESET}"
                    last_id = None
                else:
                    message = "Nothing to undo."
                continue
            if index >= len(session):
                if auto:
                    break
                continue
            if key == "s":
                index += 1
                message = "Skipped."
                continue
            if not stream.live:
                message = f"{RED}No data from the ESP32 yet, so I cannot record.{RESET}"
                continue
            # ---- countdown, then record ----
            running = args.fill
            item = session[index]
            pressed = ""
            for n in range(args.countdown, 0, -1):
                screen.draw(frame(stream, session, index, "count", found, message if args.fill else "",
                                  f"{YELLOW}{n}{RESET}", "Get ready..."))
                until = time.monotonic() + (1 if not auto else 0.2)
                while time.monotonic() < until and not pressed:
                    k = read_key() if args.fill and not auto else ""
                    if k in ("q", "p", "s", "u"):
                        pressed = k
                    await asyncio.sleep(0.03)
                if pressed:
                    break
            if pressed == "q":
                break
            if pressed == "p":
                running = False
                message = f"{YELLOW}Paused. Press SPACE to carry on.{RESET}"
                continue
            if pressed == "s":
                index += 1
                message = "Skipped."
                continue
            if pressed == "u":
                if last_id and T.delete(last_id):
                    found = counts()
                    index = max(0, index - 1)
                    message = f"{YELLOW}Deleted the last take. Do that prompt again.{RESET}"
                    last_id = None
                else:
                    message = "Nothing to undo."
                continue
            stream.rows = []
            stream.recording = True
            start = time.monotonic()
            while time.monotonic() - start < args.seconds:
                done = (time.monotonic() - start) / args.seconds
                screen.draw(frame(stream, session, index, "rec", found, "",
                                  f"{GREEN}GO!  {item['label'].upper()} NOW{RESET}", bar(done, 1, 30, RED)))
                await asyncio.sleep(0.05)
            stream.recording = False
            rows, stream.rows = stream.rows, []
            if T.dropout(rows):
                message = (f"{RED}The sensor stopped answering, so this take was NOT saved. Check the wires "
                           f"(SDA, SCL, 3V3, GND), then do it again.{RESET}")
                continue
            if usb:
                rows = T.even_times(rows)      # USB arrives in bursts; the board samples at a steady rate
            warning = check(item["label"], rows)
            rate = len(rows) / args.seconds
            if len(rows) >= 50 and rate < 120:
                warning = f"only {rate:.0f} samples/s, the model needs about 200. This take is not usable: the firmware is too slow over this link."
            if len(rows) < 50:
                message = f"{RED}{warning}{RESET}"
                continue
            if args.fill:
                problem = fill_problem(item["label"], rows)
                if problem:
                    message = f"{RED}Not saved: {problem} Same prompt again.{RESET}"
                    continue
            take = T.make_take(rows, item["label"], item["strength"], args.person, "guided", "live", stream.peer)
            if item["direction"]:
                take["direction"] = item["direction"]
            T.save(take)
            last_id = take["id"]
            found[item["label"]] = found.get(item["label"], 0) + 1      # re-reading every take would stall the loop
            index += 1
            message = f"{GREEN}Saved{RESET} ({len(rows)} samples, {take['sample_rate_hz']} Hz)."
            if warning:
                message += f"  {YELLOW}{warning}{RESET}"
    finally:
        stream.recording = False
        screen.close()
        if usb:
            usb.stop = True
        if runner:
            await runner.cleanup()
    print(f"Saved takes: {sum(found.values())}.  Train with:  .venv\\Scripts\\python -m swatter_ml.train")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--person", default="", help="who is swinging (kept with each take)")
    ap.add_argument("--usb", metavar="COM", help="serial port of the ESP32 (default: find it automatically)")
    ap.add_argument("--wifi", action="store_true", help="receive the ESP32's Wi-Fi stream instead of reading USB")
    ap.add_argument("--port", type=int, default=8767, help="--wifi: port to listen on")
    ap.add_argument("--rounds", type=int, default=1, help="how many passes through the whole prompt list")
    ap.add_argument("--seconds", type=float, default=2.0, help="length of each take")
    ap.add_argument("--countdown", type=int, default=3)
    ap.add_argument("--fill", action="store_true", help="record only the non-swings the data is short of, all in a row")
    ap.add_argument("--target", type=int, default=20, help="--fill: takes each non-swing class should end up with")
    ap.add_argument("--auto", action="store_true", help="testing: press SPACE automatically")
    args = ap.parse_args()
    try:
        asyncio.run(run(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
