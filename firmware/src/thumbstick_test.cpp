// Adafruit Mini Analog Thumbstick test for Seeed XIAO ESP32S3
//
// Wiring (the thumbstick breakout has 5 pins):
//   Thumbstick VCC -> 3V3        (NOT 5V: ESP32 ADC pins max out at 3.3V)
//   Thumbstick GND -> GND
//   Thumbstick X   -> D0 (GPIO1)  ADC1
//   Thumbstick Y   -> D1 (GPIO2)  ADC1
//   Thumbstick SEL -> D2 (GPIO3)  button, pulls to GND when pressed
//
// D4/D5 (GPIO5/6) are left free for the I2C IMU. Only ADC1 pins are used, so
// this keeps working if Wi-Fi is added later (ADC2 doesn't work with Wi-Fi).
//
// Open the serial monitor at 115200 baud. Leave the stick untouched for the
// ~1 s calibration at boot, then move it around:
//   - prints direction changes: UP / DOWN / LEFT / RIGHT (and diagonals) / CENTER
//   - prints "BUTTON pressed/released" for the SEL click
// Send 'p' over serial to toggle raw streaming (works with the serial plotter).
// Send 'c' to recalibrate the center position.

#include <Arduino.h>

// ---- Pins -------------------------------------------------------------------
constexpr int PIN_X = D0;
constexpr int PIN_Y = D1;
constexpr int PIN_SEL = D2;
// ---- Tunables ---------------------------------------------------------------
constexpr float DEADZONE = 0.15f;        // |value| below this counts as centered
constexpr float DIR_THRESHOLD = 0.5f;    // |value| above this counts as a direction
constexpr bool INVERT_X = false;         // flip if "RIGHT" prints as "LEFT"
constexpr bool INVERT_Y = false;         // flip if "UP" prints as "DOWN"
constexpr uint32_t LOOP_PERIOD_MS = 10;  // 100 Hz
constexpr uint32_t DEBOUNCE_MS = 25;
// -----------------------------------------------------------------------------

constexpr int ADC_MAX = 4095;  // 12-bit

int centerX = ADC_MAX / 2;
int centerY = ADC_MAX / 2;
bool streamRaw = false;

// Average several reads to smooth out ADC noise.
static int readAvg(int pin, int n = 8) {
  long sum = 0;
  for (int i = 0; i < n; i++) sum += analogRead(pin);
  return sum / n;
}

static void calibrate() {
  Serial.println("Calibrating... leave the stick centered.");
  long sx = 0, sy = 0;
  const int samples = 100;
  for (int i = 0; i < samples; i++) {
    sx += readAvg(PIN_X);
    sy += readAvg(PIN_Y);
    delay(10);
  }
  centerX = sx / samples;
  centerY = sy / samples;
  Serial.printf("Center: x=%d y=%d\n", centerX, centerY);
}

// Map a raw reading to -1..1 around the calibrated center, with deadzone.
static float normalize(int raw, int center) {
  float v = raw >= center ? float(raw - center) / float(ADC_MAX - center)
                          : float(raw - center) / float(center);
  v = constrain(v, -1.0f, 1.0f);
  if (fabsf(v) < DEADZONE) return 0.0f;
  // Rescale so output ramps from 0 at the deadzone edge to 1 at full travel.
  float sign = v < 0 ? -1.0f : 1.0f;
  return sign * (fabsf(v) - DEADZONE) / (1.0f - DEADZONE);
}

static const char* directionName(float x, float y) {
  bool r = x > DIR_THRESHOLD, l = x < -DIR_THRESHOLD;
  bool u = y > DIR_THRESHOLD, d = y < -DIR_THRESHOLD;
  if (u && r) return "UP-RIGHT";
  if (u && l) return "UP-LEFT";
  if (d && r) return "DOWN-RIGHT";
  if (d && l) return "DOWN-LEFT";
  if (u) return "UP";
  if (d) return "DOWN";
  if (r) return "RIGHT";
  if (l) return "LEFT";
  return "CENTER";
}

void setup() {
  Serial.begin(115200);
  uint32_t start = millis();
  while (!Serial && millis() - start < 3000) delay(10);  // wait for USB serial
  Serial.println("\nThumbstick test (XIAO ESP32S3)");

  analogReadResolution(12);
  pinMode(PIN_SEL, INPUT_PULLUP);  // breakout has no pull-up; button shorts to GND

  calibrate();
  Serial.println("Move the stick or press it. 'p' = raw stream, 'c' = recalibrate.");
}

void loop() {
  static uint32_t lastLoop = 0;
  static const char* lastDir = "CENTER";
  static bool lastBtn = false;      // debounced state (true = pressed)
  static bool rawBtnPrev = false;
  static uint32_t btnChangeMs = 0;

  uint32_t now = millis();
  if (now - lastLoop < LOOP_PERIOD_MS) return;
  lastLoop = now;

  while (Serial.available()) {
    char c = Serial.read();
    if (c == 'p') {
      streamRaw = !streamRaw;
      Serial.println(streamRaw ? "Raw streaming ON" : "Raw streaming OFF");
    } else if (c == 'c') {
      calibrate();
    }
  }

  int rawX = readAvg(PIN_X);
  int rawY = readAvg(PIN_Y);
  float x = normalize(rawX, centerX);
  float y = normalize(rawY, centerY);
  if (INVERT_X) x = -x;
  if (INVERT_Y) y = -y;

  if (streamRaw) {
    // Format works with the Arduino/PlatformIO serial plotter.
    Serial.printf("rawX:%d rawY:%d x:%.2f y:%.2f btn:%d\n", rawX, rawY, x, y, lastBtn);
  }

  // Report only when the direction changes.
  const char* dir = directionName(x, y);
  if (dir != lastDir && strcmp(dir, lastDir) != 0) {
    lastDir = dir;
    Serial.printf("%-10s (x=%+.2f, y=%+.2f)\n", dir, x, y);
  }

  // Debounced button (active low).
  bool btn = digitalRead(PIN_SEL) == LOW;
  if (btn != rawBtnPrev) {
    rawBtnPrev = btn;
    btnChangeMs = now;
  }
  if (btn != lastBtn && now - btnChangeMs > DEBOUNCE_MS) {
    lastBtn = btn;
    Serial.println(btn ? "BUTTON pressed" : "BUTTON released");
  }
}
