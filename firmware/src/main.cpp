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
#include <WebSocketsClient.h>
#include <Wire.h>
#include <Adafruit_LSM6DSOX.h>

// ---- WiFi: the name and password are in the git-ignored wifi_secrets.h --------
// Copy firmware/src/wifi_secrets.example.h to wifi_secrets.h and fill it in.
#include "wifi_secrets.h"
// -----------------------------------------------------------------------------

// ---- WebSocket (Raspberry Pi training-data server) --------------------------
// The Pi's address on the hotspot, and the game backend's swatter stream port (see backend/README.md).
constexpr char PI_HOST[] = "172.20.10.10";
constexpr uint16_t PI_PORT = 8767;
constexpr char PI_WS_PATH[] = "/";
// -----------------------------------------------------------------------------

// ---- Thumbstick (walk and turn) ----------------------------------------------
// VCC -> 3V3, GND -> GND, X -> D0, Y -> D1. Leave it untouched while the board starts.
constexpr int PIN_STICK_X = D0;
constexpr int PIN_STICK_Y = D1;
constexpr int PIN_STICK_PRESS = D2;   // Shorts to GND when pressed.
constexpr int STICK_ADC_MAX = 4095;
constexpr float STICK_DEADZONE = 0.15f;
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
WebSocketsClient webSocket;

float gravity[3] = {0, 0, 0};   // slowly-tracked gravity vector (device frame)
float gyroBias[3] = {0, 0, 0};  // rad/s, measured while still
bool streamRaw = false;
uint32_t lastMoveMs = 0;
uint32_t lastSwingMs = 0;

static const char AXIS_NAME[3] = {'X', 'Y', 'Z'};

int stickCenterX = 2048, stickCenterY = 2048;
bool stickFound = false;      // false if the pins do not rest mid-range (no stick wired): it then reads as centred
float stickX = 0, stickY = 0;
bool stickPressed = false;

static int readStickPin(int pin) {
  long sum = 0;
  for (int i = 0; i < 4; i++) sum += analogRead(pin);
  return sum / 4;
}

static void calibrateStick() {
  long sx = 0, sy = 0;
  for (int i = 0; i < 50; i++) {
    sx += readStickPin(PIN_STICK_X);
    sy += readStickPin(PIN_STICK_Y);
    delay(4);
  }
  stickCenterX = sx / 50;
  stickCenterY = sy / 50;
  stickFound = stickCenterX > 1200 && stickCenterX < 2900 && stickCenterY > 1200 && stickCenterY < 2900;
  Serial.printf("Thumbstick %s (centre %d, %d)\n", stickFound ? "found" : "NOT found", stickCenterX, stickCenterY);
}

// -1..1 around the centre, ramping up from 0 at the edge of the deadzone.
static float stickValue(int raw, int center) {
  float v = raw >= center ? float(raw - center) / float(STICK_ADC_MAX - center) : float(raw - center) / float(center);
  v = constrain(v, -1.0f, 1.0f);
  if (fabsf(v) < STICK_DEADZONE) return 0.0f;
  return (v < 0 ? -1.0f : 1.0f) * (fabsf(v) - STICK_DEADZONE) / (1.0f - STICK_DEADZONE);
}

// Ranges and rates, set at start-up and again if the sensor has to be restarted.
static void configureImu() {
  imu.setAccelRange(LSM6DS_ACCEL_RANGE_8_G);
  imu.setGyroRange(LSM6DS_GYRO_RANGE_1000_DPS);
  imu.setAccelDataRate(LSM6DS_RATE_208_HZ);
  imu.setGyroDataRate(LSM6DS_RATE_208_HZ);
}

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

static void webSocketEvent(WStype_t type, uint8_t *payload, size_t length) {
  switch (type) {
    case WStype_CONNECTED:
      Serial.println("WebSocket connected to Pi server.");
      break;
    case WStype_DISCONNECTED:
      Serial.println("WebSocket disconnected from Pi server.");
      break;
    case WStype_ERROR:
      Serial.println("WebSocket error.");
      break;
    default:
      break;
  }
}

void setup() {
  Serial.begin(115200);
  uint32_t start = millis();
  while (!Serial && millis() - start < 3000) delay(10);  // wait for USB serial
  Serial.setTxTimeoutMs(0);  // With no USB reader attached, printing must never stall the sensor stream.
  Serial.println("\nLSM6DSOX motion test (XIAO ESP32S3)");

  // Say why Wi-Fi fails: it is the first thing to check when the swatter does not reach the Pi.
  WiFi.onEvent([](WiFiEvent_t event, WiFiEventInfo_t info) {
    if (event == ARDUINO_EVENT_WIFI_STA_GOT_IP) Serial.printf("WiFi got address %s\n", WiFi.localIP().toString().c_str());
    else if (event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED) Serial.printf("WiFi not connected, reason %d\n", info.wifi_sta_disconnected.reason);
  });
  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);      // Power saving would delay the 200 Hz stream.
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  Serial.printf("Connecting to WiFi \"%s\"", WIFI_SSID);
  // Wait up to 8 s, then carry on without Wi-Fi: the USB serial stream (send 'p') must work even when the
  // Pi's hotspot is off. The Wi-Fi stack keeps trying in the background.
  uint32_t wifiStart = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - wifiStart < 8000) {
    delay(500);
    Serial.print(".");
  }
  if (WiFi.status() == WL_CONNECTED) Serial.printf("\nWiFi connected, IP address: %s\n", WiFi.localIP().toString().c_str());
  else {
    Serial.println("\nNo WiFi yet (continuing; still retrying in the background).");
    WiFi.disconnect();                    // A scan fails while a connection attempt is running.
    delay(200);
    int found = WiFi.scanNetworks();
    for (int i = 0; i < found && i < 6; i++) Serial.printf("  sees \"%s\" %d dBm ch %d\n", WiFi.SSID(i).c_str(), WiFi.RSSI(i), WiFi.channel(i));
    bool seen = false;
    for (int i = 0; i < found; i++) {
      if (WiFi.SSID(i) == WIFI_SSID) {
        seen = true;
        Serial.printf("Hotspot visible: signal %d dBm, channel %d\n", WiFi.RSSI(i), WiFi.channel(i));
      }
    }
    if (!seen) Serial.printf("Hotspot NOT visible (%d other networks seen). Check the antenna.\n", found);
    WiFi.scanDelete();
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  }

  webSocket.begin(PI_HOST, PI_PORT, PI_WS_PATH);
  webSocket.onEvent(webSocketEvent);
  webSocket.setReconnectInterval(2000);

  Wire.begin();  // XIAO ESP32S3 default: SDA=GPIO5 (D4), SCL=GPIO6 (D5)
  Wire.setClock(400000);

  // Breakouts use 0x6A or 0x6B depending on the SDO/SA0 pin.
  if (!imu.begin_I2C(0x6A) && !imu.begin_I2C(0x6B)) {
    Serial.println("ERROR: LSM6DSOX not found. Check wiring (SDA=D4, SCL=D5, 3V3, GND).");
    while (true) delay(1000);
  }
  Serial.println("LSM6DSOX found.");

  configureImu();

  analogReadResolution(12);
  pinMode(PIN_STICK_PRESS, INPUT_PULLUP);
  calibrateStick();
  calibrate();
}

void loop() {
  static uint32_t lastLoop = 0;
  uint32_t now = millis();
  webSocket.loop();

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

  // A hard swing can drop the I2C link, and the sensor then reads exactly zero until it is restarted.
  // Those readings are never sent, and the link is restarted (the calibration is kept).
  static uint8_t zeroReads = 0;
  if (accel[0] == 0 && accel[1] == 0 && accel[2] == 0) {
    if (++zeroReads >= 10) {
      zeroReads = 0;
      Serial.println("Sensor stopped answering, restarting I2C.");
      Wire.end();
      Wire.begin();
      Wire.setClock(400000);
      if (imu.begin_I2C(0x6A) || imu.begin_I2C(0x6B)) configureImu();
    }
    return;
  }
  zeroReads = 0;

  // Remove gyro bias, and strip gravity so only motion remains.
  float linear[3];
  for (int k = 0; k < 3; k++) {
    gyro[k] -= gyroBias[k];
    gravity[k] += GRAVITY_ALPHA * (accel[k] - gravity[k]);
    linear[k] = accel[k] - gravity[k];
  }

  static uint8_t stickTick = 0;
  if (stickFound && ++stickTick >= 4) {     // 50 Hz is plenty, and keeps the sensor loop at 200 Hz
    stickTick = 0;
    stickX = stickValue(readStickPin(PIN_STICK_X), stickCenterX);
    stickY = stickValue(readStickPin(PIN_STICK_Y), stickCenterY);
    static bool lastPress = false;        // Two matching reads 20 ms apart, so contact bounce is ignored.
    bool press = digitalRead(PIN_STICK_PRESS) == LOW;
    if (press == lastPress) stickPressed = press;
    lastPress = press;
  }

  if (streamRaw) {
    // Format works with the Arduino/PlatformIO serial plotter.
    // ax/ay/az (raw, gravity included) match what the Wi-Fi stream sends, so USB recordings can train the same model.
    Serial.printf("lx:%.2f ly:%.2f lz:%.2f gx:%.2f gy:%.2f gz:%.2f ax:%.3f ay:%.3f az:%.3f sx:%.2f sy:%.2f sb:%d\n",
                  linear[0], linear[1], linear[2], gyro[0], gyro[1], gyro[2], accel[0], accel[1], accel[2], stickX, stickY, stickPressed ? 1 : 0);
  }

  // Stream every sample to the Pi for training-data collection.
  char json[220];
  snprintf(json, sizeof(json),
           "{\"t\":%lu,\"ax\":%.4f,\"ay\":%.4f,\"az\":%.4f,\"gx\":%.4f,\"gy\":%.4f,\"gz\":%.4f,\"sx\":%.2f,\"sy\":%.2f,\"sb\":%d}",
           (unsigned long)now, accel[0], accel[1], accel[2], gyro[0], gyro[1], gyro[2], stickX, stickY, stickPressed ? 1 : 0);
  webSocket.sendTXT(json);

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