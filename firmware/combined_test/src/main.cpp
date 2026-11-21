// Combined test: LSM6DSOX gyro/accelerometer (I2C) and analog thumbstick, used at the same time.
//
// Wiring (both devices share 3V3 and GND):
//   LSM6DSOX   VIN -> 3V3   GND -> GND   SDA -> D4 (GPIO5)   SCL -> D5 (GPIO6)
//   Thumbstick VCC -> 3V3   GND -> GND   X   -> D0 (GPIO1)   Y   -> D1 (GPIO2)   SEL -> D2 (GPIO3)
//
// On boot it checks each part, then runs both together for 2 s and prints PASS / FAIL lines.
// After that it prints one live status line every 100 ms plus events as they happen:
//   SWING / MOVE      from the gyro and accelerometer (same rules as the main firmware)
//   STICK <direction> from the thumbstick;  BUTTON pressed / released for the SEL click
// Keep the swatter still and the stick untouched during the first ~4 s (calibration and self-test).
//
// Serial commands (115200 baud):  p = live line on/off   c = recalibrate stick   t = rerun self-test

#include <Arduino.h>
#include <Wire.h>
#include <Adafruit_LSM6DSOX.h>

// ---- Pins -------------------------------------------------------------------
constexpr int PIN_X = D0;
constexpr int PIN_Y = D1;
constexpr int PIN_SEL = D2;
// ---- Tunables ---------------------------------------------------------------
constexpr uint32_t LOOP_PERIOD_MS = 5;       // 200 Hz, the rate the project streams the IMU at
constexpr uint32_t PRINT_PERIOD_MS = 100;
constexpr float MOVE_THRESHOLD = 3.0f;       // m/s^2 of linear acceleration to count as a push
constexpr float SWING_THRESHOLD = 4.0f;      // rad/s of rotation to count as a swing
constexpr float GRAVITY_ALPHA = 0.02f;
constexpr uint32_t MOVE_COOLDOWN_MS = 300;
constexpr uint32_t SWING_COOLDOWN_MS = 400;
constexpr float DEADZONE = 0.15f;            // stick |value| below this counts as centred
constexpr float DIR_THRESHOLD = 0.5f;        // stick |value| above this counts as a direction
constexpr uint32_t DEBOUNCE_MS = 25;
// Pass limits for the self-test (rules of thumb, not guarantees):
constexpr float GRAVITY_MIN = 8.5f, GRAVITY_MAX = 11.0f;   // |accel| at rest, m/s^2
constexpr float MIN_IMU_HZ = 150.0f;                       // IMU rate while the stick is being read too
constexpr int MAX_STICK_NOISE = 200;                       // peak-to-peak ADC counts at rest (12-bit)
// -----------------------------------------------------------------------------

constexpr int ADC_MAX = 4095;
Adafruit_LSM6DSOX imu;
uint8_t imuAddr = 0;

float gravity[3] = {0, 0, 0};
float gyroBias[3] = {0, 0, 0};
int centerX = ADC_MAX / 2, centerY = ADC_MAX / 2;
bool showLive = true;
uint32_t lastMoveMs = 0, lastSwingMs = 0;
const char AXIS[3] = {'X', 'Y', 'Z'};

static void readImu(float a[3], float g[3]) {
  sensors_event_t ea, eg, et;
  imu.getEvent(&ea, &eg, &et);
  a[0] = ea.acceleration.x; a[1] = ea.acceleration.y; a[2] = ea.acceleration.z;
  g[0] = eg.gyro.x; g[1] = eg.gyro.y; g[2] = eg.gyro.z;
}

static int readAdc(int pin) {            // average a few reads to smooth ADC noise
  long sum = 0;
  for (int i = 0; i < 8; i++) sum += analogRead(pin);
  return sum / 8;
}

static float stickValue(int raw, int center) {
  float v = raw >= center ? float(raw - center) / float(ADC_MAX - center)
                          : float(raw - center) / float(center);
  if (fabsf(v) < DEADZONE) return 0.0f;
  return v > 1.0f ? 1.0f : (v < -1.0f ? -1.0f : v);
}

static const char* stickDirection(float x, float y) {
  bool r = x > DIR_THRESHOLD, l = x < -DIR_THRESHOLD, u = y > DIR_THRESHOLD, d = y < -DIR_THRESHOLD;
  if (u && r) return "UP-RIGHT";  if (u && l) return "UP-LEFT";
  if (d && r) return "DOWN-RIGHT"; if (d && l) return "DOWN-LEFT";
  if (u) return "UP"; if (d) return "DOWN"; if (r) return "RIGHT"; if (l) return "LEFT";
  return "CENTER";
}

static int dominant(const float v[3]) {
  int best = 0;
  for (int k = 1; k < 3; k++) if (fabsf(v[k]) > fabsf(v[best])) best = k;
  return best;
}

static bool i2cPing(uint8_t addr) {
  Wire.beginTransmission(addr);
  return Wire.endTransmission() == 0;
}

static void scanI2c() {
  Serial.print("I2C devices found:");
  int n = 0;
  for (uint8_t addr = 0x08; addr < 0x78; addr++) {
    if (i2cPing(addr)) { Serial.printf(" 0x%02X", addr); n++; }
  }
  Serial.println(n ? "" : " none");
}

static void calibrateStick() {
  long sx = 0, sy = 0;
  const int n = 100;
  for (int i = 0; i < n; i++) { sx += readAdc(PIN_X); sy += readAdc(PIN_Y); delay(5); }
  centerX = sx / n; centerY = sy / n;
  Serial.printf("Stick centre: x=%d y=%d (expect roughly 1500 to 2600)\n", centerX, centerY);
}

static int passed = 0, total = 0;
static void check(bool ok, const char* what, const char* detail = "") {
  total++; if (ok) passed++;
  Serial.printf("  [%s] %s %s\n", ok ? "PASS" : "FAIL", what, detail);
}

static void selfTest() {
  passed = total = 0;
  Serial.println("\n== Self-test: keep the swatter still and the stick untouched ==");
  scanI2c();
  check(imuAddr != 0, "gyro answers on I2C", imuAddr ? "" : "(check SDA=D4, SCL=D5, 3V3, GND)");
  check(digitalRead(PIN_SEL) == HIGH, "stick button reads released at rest", "(LOW means SEL is shorted to GND or held)");
  check(centerX > 1200 && centerX < 2900 && centerY > 1200 && centerY < 2900, "stick centre is mid-range",
        "(near 0 or 4095 means X/Y wiring or power is off)");

  // Both devices at the same time for 2 s.
  int minX = 4095, maxX = 0, minY = 4095, maxY = 0, imuCount = 0;
  double accSum = 0; double gSum[3] = {0, 0, 0};
  uint32_t t0 = millis(), next = t0;
  while (millis() - t0 < 2000) {
    if (millis() < next) continue;
    next += LOOP_PERIOD_MS;
    int x = readAdc(PIN_X), y = readAdc(PIN_Y);
    minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y);
    if (imuAddr) {
      float a[3], g[3]; readImu(a, g);
      accSum += sqrtf(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
      for (int k = 0; k < 3; k++) { gSum[k] += g[k]; gravity[k] = a[k]; }
      imuCount++;
    }
  }
  float hz = imuCount / ((millis() - t0) / 1000.0f);
  float mag = imuCount ? accSum / imuCount : 0;
  char buf[80];
  snprintf(buf, sizeof(buf), "(%.2f m/s^2)", mag);
  check(imuCount > 0 && mag > GRAVITY_MIN && mag < GRAVITY_MAX, "accelerometer reads about 1 g at rest", buf);
  snprintf(buf, sizeof(buf), "(%.0f Hz, need %.0f)", hz, MIN_IMU_HZ);
  check(hz >= MIN_IMU_HZ, "gyro keeps its rate while the stick is read too", buf);
  int noise = max(maxX - minX, maxY - minY);
  snprintf(buf, sizeof(buf), "(%d counts, limit %d)", noise, MAX_STICK_NOISE);
  check(noise <= MAX_STICK_NOISE, "stick is steady while the gyro talks on I2C", buf);
  if (imuCount) for (int k = 0; k < 3; k++) gyroBias[k] = gSum[k] / imuCount;
  Serial.printf("== Self-test: %d of %d passed ==\n", passed, total);
  Serial.println(passed == total ? "Both devices work together. Now move them: events print below.\n"
                                 : "Fix the FAIL lines above, then press 't' to rerun.\n");
}

void setup() {
  Serial.begin(115200);
  uint32_t start = millis();
  while (!Serial && millis() - start < 3000) delay(10);
  Serial.println("\nCombined test: LSM6DSOX + thumbstick (XIAO ESP32S3)");

  analogReadResolution(12);
  pinMode(PIN_SEL, INPUT_PULLUP);        // the breakout has no pull-up; the button shorts to GND
  Wire.begin();                          // SDA=GPIO5 (D4), SCL=GPIO6 (D5)
  Wire.setClock(400000);

  if (imu.begin_I2C(0x6A)) imuAddr = 0x6A; else if (imu.begin_I2C(0x6B)) imuAddr = 0x6B;
  if (imuAddr) {
    Serial.printf("LSM6DSOX found at 0x%02X\n", imuAddr);
    imu.setAccelRange(LSM6DS_ACCEL_RANGE_8_G);
    imu.setGyroRange(LSM6DS_GYRO_RANGE_1000_DPS);
    imu.setAccelDataRate(LSM6DS_RATE_208_HZ);
    imu.setGyroDataRate(LSM6DS_RATE_208_HZ);
  } else {
    Serial.println("LSM6DSOX NOT found (continuing so the stick can still be tested).");
  }
  calibrateStick();
  selfTest();
}

void loop() {
  static uint32_t lastLoop = 0, lastPrint = 0, lastHealth = 0, btnChangedMs = 0;
  static bool btnState = false;
  static const char* lastDir = "CENTER";
  uint32_t now = millis();
  if (now - lastLoop < LOOP_PERIOD_MS) return;
  lastLoop = now;

  while (Serial.available()) {
    char c = Serial.read();
    if (c == 'p') { showLive = !showLive; Serial.println(showLive ? "Live line ON" : "Live line OFF"); }
    else if (c == 'c') calibrateStick();
    else if (c == 't') selfTest();
  }

  // Stick
  int rx = readAdc(PIN_X), ry = readAdc(PIN_Y);
  float sx = stickValue(rx, centerX), sy = stickValue(ry, centerY);
  const char* dir = stickDirection(sx, sy);
  if (strcmp(dir, lastDir) != 0) { Serial.printf("STICK  %s\n", dir); lastDir = dir; }
  bool btn = digitalRead(PIN_SEL) == LOW;
  if (btn != btnState && now - btnChangedMs > DEBOUNCE_MS) {
    btnState = btn; btnChangedMs = now;
    Serial.println(btn ? "BUTTON pressed" : "BUTTON released");
  }

  // Gyro and accelerometer
  float a[3] = {0, 0, 0}, g[3] = {0, 0, 0}, lin[3] = {0, 0, 0};
  if (imuAddr) {
    readImu(a, g);
    for (int k = 0; k < 3; k++) {
      g[k] -= gyroBias[k];
      gravity[k] += GRAVITY_ALPHA * (a[k] - gravity[k]);
      lin[k] = a[k] - gravity[k];
    }
    int ga = dominant(g);
    if (fabsf(g[ga]) > SWING_THRESHOLD && now - lastSwingMs > SWING_COOLDOWN_MS) {
      lastSwingMs = lastMoveMs = now;
      Serial.printf("SWING  about %c axis, %s  (%.1f rad/s)\n", AXIS[ga], g[ga] > 0 ? "positive" : "negative", fabsf(g[ga]));
    } else {
      int la = dominant(lin);
      if (fabsf(lin[la]) > MOVE_THRESHOLD && now - lastMoveMs > MOVE_COOLDOWN_MS) {
        lastMoveMs = now;
        Serial.printf("MOVE   %c%c  (%.1f m/s^2)\n", lin[la] > 0 ? '+' : '-', AXIS[la], fabsf(lin[la]));
      }
    }
    if (now - lastHealth > 1000) {       // the gyro must stay on the bus while the ADC runs
      lastHealth = now;
      if (!i2cPing(imuAddr)) Serial.println("WARNING: gyro stopped answering on I2C (loose SDA/SCL/power?)");
    }
  }

  if (showLive && now - lastPrint >= PRINT_PERIOD_MS) {
    lastPrint = now;
    Serial.printf("gyro %6.2f %6.2f %6.2f rad/s | lin %6.2f %6.2f %6.2f m/s2 | stick x %5.2f y %5.2f raw %4d %4d | btn %d\n",
                  g[0], g[1], g[2], lin[0], lin[1], lin[2], sx, sy, rx, ry, btn ? 1 : 0);
  }
}
