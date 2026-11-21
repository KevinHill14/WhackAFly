# combined_test

Tests the LSM6DSOX gyro/accelerometer and the analog thumbstick **at the same time** on one
XIAO ESP32S3. It is its own PlatformIO project, so it does not change the main firmware.

```sh
cd firmware/combined_test
pio run -t upload
pio device monitor        # 115200 baud; press the reset button after it connects
```

Wiring (both parts share 3V3 and GND):

| Part | Pin | XIAO pin |
|---|---|---|
| LSM6DSOX | VIN, GND | 3V3, GND |
| | SDA, SCL | D4 (GPIO5), D5 (GPIO6) |
| Thumbstick | VCC, GND | 3V3, GND |
| | X, Y | D0 (GPIO1), D1 (GPIO2) |
| | SEL | D2 (GPIO3) |

Keep the swatter still and the stick untouched for about 4 s after boot. It then prints a
self-test (PASS or FAIL lines), then a live line every 100 ms and events:
`SWING`, `MOVE`, `STICK <direction>`, `BUTTON pressed/released`.

The self-test checks: the gyro answers on I2C; the button reads released; the stick centre is
mid-range; the accelerometer reads about 1 g; the gyro keeps at least 150 Hz while the stick is
read too; and the stick stays steady (under 200 ADC counts peak to peak) while the gyro uses
I2C. The limits are rules of thumb. Serial commands: `p` live line on/off, `c` recalibrate the
stick, `t` rerun the self-test.

If the stick direction is backwards, or the gyro is not found, see the comments at the top of
`src/main.cpp`.
