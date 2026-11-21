// LSM6DSOX motion test for Seeed XIAO ESP32S3
//
// Wiring (I2C):
//   LSM6DSOX VIN -> 3V3     GND -> GND
//   LSM6DSOX SDA -> D4 (GPIO5)
//   LSM6DSOX SCL -> D5 (GPIO6)
//
// Open the serial monitor at 115200 baud. Hold the device still for ~2 s
// while it calibrates, then:
//   - push it in a direction  -> prints "MOVE  +X / -X / +Y / -Y / +Z / -Z"
//   - swing it like a bat/hammer -> prints "SWING <axis> <direction>"
// Send 'p' over serial to toggle raw data streaming (handy for plotting).
//
// Library: adafruit/Adafruit LSM6DS (PlatformIO) -- also pulls in Adafruit BusIO
// and Adafruit Unified Sensor.

#include <Arduino.h>
#include <WiFi.h>
#include <Wire.h>
#include <Adafruit_LSM6DSOX.h>

// ---- WiFi (phone hotspot) ---------------------------------------------------
constexpr char WIFI_SSID[] = "SM-G781W0986";
constexpr char WIFI_PASSWORD[] = "blwt3316";
// -----------------------------------------------------------------------------

// ---- Tunables ---------------------------------------------------------------
constexpr float MOVE_THRESHOLD = 3.0f;    // m/s^2 of linear accel to count as a push
constexpr float SWING_THRESHOLD = 4.0f;   // rad/s of rotation to count as a swing
constexpr float GRAVITY_ALPHA = 0.02f;    // low-pass for gravity estimate (smaller = slower)
constexpr uint32_t MOVE_COOLDOWN_MS = 300;
constexpr uint32_t SWING_COOLDOWN_MS = 400;
constexpr uint32_t LOOP_PERIOD_MS = 5;    // ~200 Hz
// -----------------------------------------------------------------------------

Adafruit_LSM6DSOX imu;

float gravity[3] = {0, 0, 0};   // slowly-tracked gravity vector (device frame)
float gyroBias[3] = {0, 0, 0};  // rad/s, measured while still
bool streamRaw = false;
uint32_t lastMoveMs = 0;
uint32_t lastSwingMs = 0;

static const char AXIS_NAME[3] = {'X', 'Y', 'Z'};

static void readSensor(float accel[3], float gyro[3]) {
  sensors_event_t a, g, t;
  imu.getEvent(&a, &g, &t);
  accel[0] = a.acceleration.x;
  accel[1] = a.acceleration.y;
  accel[2] = a.acceleration.z;
  gyro[0] = g.gyro.x;
  gyro[1] = g.gyro.y;
  gyro[2] = g.gyro.z;
}

// Average ~2 s of samples while the device is held still.
static void calibrate() {
  Serial.println("Calibrating... keep the device still.");
  const int samples = 300;
  float accSum[3] = {0, 0, 0};
  float gyroSum[3] = {0, 0, 0};
  for (int i = 0; i < samples; i++) {
    float a[3], g[3];
    readSensor(a, g);
    for (int k = 0; k < 3; k++) {
      accSum[k] += a[k];
      gyroSum[k] += g[k];
    }
    delay(7);
  }
  for (int k = 0; k < 3; k++) {
    gravity[k] = accSum[k] / samples;
    gyroBias[k] = gyroSum[k] / samples;
  }
  Serial.println("Calibration done. Try pushing or swinging the device.");
}

// Returns index of the axis with the largest absolute value.
static int dominantAxis(const float v[3]) {
  int best = 0;
  for (int k = 1; k < 3; k++) {
    if (fabsf(v[k]) > fabsf(v[best])) best = k;
  }
  return best;
}

void setup() {
  Serial.begin(115200);
  uint32_t start = millis();
  while (!Serial && millis() - start < 3000) delay(10);  // wait for USB serial
  Serial.println("\nLSM6DSOX motion test (XIAO ESP32S3)");

  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  Serial.printf("Connecting to WiFi \"%s\"", WIFI_SSID);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.printf("\nWiFi connected, IP address: %s\n", WiFi.localIP().toString().c_str());

  Wire.begin();  // XIAO ESP32S3 default: SDA=GPIO5 (D4), SCL=GPIO6 (D5)
  Wire.setClock(400000);

  // Breakouts use 0x6A or 0x6B depending on the SDO/SA0 pin.
  if (!imu.begin_I2C(0x6A) && !imu.begin_I2C(0x6B)) {
    Serial.println("ERROR: LSM6DSOX not found. Check wiring (SDA=D4, SCL=D5, 3V3, GND).");
    while (true) delay(1000);
  }
  Serial.println("LSM6DSOX found.");

  imu.setAccelRange(LSM6DS_ACCEL_RANGE_8_G);
  imu.setGyroRange(LSM6DS_GYRO_RANGE_1000_DPS);
  imu.setAccelDataRate(LSM6DS_RATE_208_HZ);
  imu.setGyroDataRate(LSM6DS_RATE_208_HZ);

  calibrate();
}

void loop() {
  static uint32_t lastLoop = 0;
  uint32_t now = millis();
  if (now - lastLoop < LOOP_PERIOD_MS) return;
  lastLoop = now;

  // Toggle raw streaming with 'p'
  while (Serial.available()) {
    if (Serial.read() == 'p') {
      streamRaw = !streamRaw;
      Serial.println(streamRaw ? "Raw streaming ON" : "Raw streaming OFF");
    }
  }

  float accel[3], gyro[3];
  readSensor(accel, gyro);

  // Remove gyro bias, and strip gravity so only motion remains.
  float linear[3];
  for (int k = 0; k < 3; k++) {
    gyro[k] -= gyroBias[k];
    gravity[k] += GRAVITY_ALPHA * (accel[k] - gravity[k]);
    linear[k] = accel[k] - gravity[k];
  }

  if (streamRaw) {
    // Format works with the Arduino/PlatformIO serial plotter.
    Serial.printf("lx:%.2f ly:%.2f lz:%.2f gx:%.2f gy:%.2f gz:%.2f\n",
                  linear[0], linear[1], linear[2], gyro[0], gyro[1], gyro[2]);
  }

  // Swing: strong rotation about one axis. Checked first, because swinging
  // also produces linear acceleration and we don't want a swing to read as a push.
  int gAxis = dominantAxis(gyro);
  if (fabsf(gyro[gAxis]) > SWING_THRESHOLD && now - lastSwingMs > SWING_COOLDOWN_MS) {
    lastSwingMs = now;
    lastMoveMs = now;  // suppress a push report for the same gesture
    Serial.printf("SWING  about %c axis, %s  (%.1f rad/s)\n", AXIS_NAME[gAxis],
                  gyro[gAxis] > 0 ? "positive" : "negative", fabsf(gyro[gAxis]));
    return;
  }

  // Directional push: strong linear acceleration along one axis.
  int lAxis = dominantAxis(linear);
  if (fabsf(linear[lAxis]) > MOVE_THRESHOLD && now - lastMoveMs > MOVE_COOLDOWN_MS) {
    lastMoveMs = now;
    Serial.printf("MOVE   %c%c  (%.1f m/s^2)\n", linear[lAxis] > 0 ? '+' : '-',
                  AXIS_NAME[lAxis], fabsf(linear[lAxis]));
  }
}