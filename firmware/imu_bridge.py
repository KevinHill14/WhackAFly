"""IMU bridge: reads the XIAO ESP32S3 + LSM6DSOX over USB serial and forwards it to Unity over UDP.

Works with the existing firmware (env imu_test, firmware/src/main.cpp): it sends 'p' to switch on the raw stream,
which prints  "lx:.. ly:.. lz:.. gx:.. gy:.. gz:.."  (linear acceleration in m/s^2 with gravity removed, gyro in
rad/s with the start-up bias removed). The device must stay still for ~2 s after it is plugged in or reset.

Every ~16 ms a UDP JSON packet goes to Unity (default 127.0.0.1:9100):
    {"dg":[x,y,z],  rotation accumulated since the last packet, radians (device axes)
     "g":[x,y,z],   latest gyro, rad/s
     "l":[x,y,z],   latest linear acceleration, m/s^2
     "lp":float,    PEAK |linear acceleration| since the last packet (so no swing is missed)
     "s":[x,y],     thumbstick, -1 to 1 (walk and turn, and the menu cursor)
     "b":0 or 1,    thumbstick pressed at any point since the last packet (the menu's select button)
     "n":int}       samples in this packet

  python firmware/imu_bridge.py                 find the port, run, forward to Unity
  python firmware/imu_bridge.py --port COM4
  python firmware/imu_bridge.py --check         no Unity: print live values and SWING lines, to prove the sensor works
Close any other program using the serial port first (serial monitors, other bridges): only one can have it.
"""
import argparse
import json
import math
import re
import socket
import sys
import time

GYRO_DEADZONE = 0.04   # rad/s
PAIR = re.compile(r"(lx|ly|lz|gx|gy|gz):(-?[0-9.]+)")
STICK = re.compile(r"\bsx:(-?[0-9.]+) sy:(-?[0-9.]+)(?: sb:([01]))?")
stick = [0.0, 0.0]     # latest thumbstick reading
press = [0, 0]         # thumbstick button: now, and pressed since the last packet


def find_port():
    from serial.tools import list_ports
    ports = list(list_ports.comports())
    for p in ports:
        text = f"{p.description} {p.manufacturer} {p.hwid}".lower()
        if "303a" in text or "seeed" in text or "esp32" in text:   # Espressif USB VID
            return p.device
    return ports[0].device if ports else None


def serial_samples(port):
    import serial
    s = serial.Serial(port, 115200, timeout=0.2)
    time.sleep(1.0)
    s.reset_input_buffer()
    s.write(b"p")                      # toggle the raw stream on
    last_p = time.time()
    got = False
    buf = b""
    while True:
        buf += s.read(512)
        while b"\n" in buf:
            line, buf = buf.split(b"\n", 1)
            text = line.decode("ascii", "ignore")
            kv = dict(PAIR.findall(text))
            m = STICK.search(text)
            if m:
                stick[0], stick[1] = float(m.group(1)), float(m.group(2))
                press[0] = int(m.group(3) or 0)
                press[1] |= press[0]
            if len(kv) == 6:
                got = True
                yield [float(kv[k]) for k in ("lx", "ly", "lz", "gx", "gy", "gz")]
            elif text.strip():
                print("device:", text.strip())
        if not got and time.time() - last_p > 3:   # still nothing: 'p' may have toggled it OFF
            s.write(b"p")
            last_p = time.time()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--udp-port", type=int, default=9100)
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--swing-threshold", type=float, default=10.0, help="m/s^2, only used by --check")
    args = ap.parse_args()

    port = args.port or find_port()
    if not port:
        sys.exit("no serial port found: plug the board in, or use --port")
    print("reading", port)
    source = serial_samples(port)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    dest = (args.host, args.udp_port)
    last = time.perf_counter()
    last_send = last
    dg = [0.0, 0.0, 0.0]
    peak = 0.0
    count = 0
    swings = 0
    swing_until = 0.0
    total = 0
    t_print = time.time()
    print("running; Ctrl+C to stop")
    try:
        for lx, ly, lz, gx, gy, gz in source:
            now = time.perf_counter()
            dt = min(now - last, 0.1)
            last = now
            # Tiny rates are sensor noise: counting them makes the aim creep while the board sits still.
            dg[0] += (gx if abs(gx) > GYRO_DEADZONE else 0.0) * dt
            dg[1] += (gy if abs(gy) > GYRO_DEADZONE else 0.0) * dt
            dg[2] += (gz if abs(gz) > GYRO_DEADZONE else 0.0) * dt
            mag = math.sqrt(lx * lx + ly * ly + lz * lz)
            peak = max(peak, mag)
            count += 1
            total += 1
            if args.check:
                if mag > args.swing_threshold and now > swing_until:
                    swing_until = now + 0.4
                    swings += 1
                    print(f"SWING #{swings}  accel {mag:.1f} m/s^2")
                if time.time() - t_print > 0.5:
                    t_print = time.time()
                    print(f"  gyro {gx:+.2f} {gy:+.2f} {gz:+.2f} rad/s   accel {mag:5.1f} m/s^2   samples {total}")
            if now - last_send >= 1 / 60:
                if not args.check:
                    pkt = {"dg": dg, "g": [gx, gy, gz], "l": [lx, ly, lz], "lp": peak, "s": stick, "b": press[1], "n": count}
                    sock.sendto(json.dumps(pkt).encode(), dest)
                    press[1] = press[0]
                last_send = now
                dg = [0.0, 0.0, 0.0]
                peak = 0.0
                count = 0
    except KeyboardInterrupt:
        pass
    except Exception as e:
        sys.exit("serial port problem: %s\nIs another program (serial monitor, another bridge) using it? Only one can." % e)


if __name__ == "__main__":
    main()
