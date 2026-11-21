//ESP32 joins WhackAFly-AP, connects to the Pi, sends "hi from ESP32", prints the reply.
#include "pi_esp.h"
#include <WiFi.h>

static const char* AP_SSID = "WhackAFly-AP";
static const char* AP_PASSWORD = "whackafly123";
static const char* PI_SERVER_IP = "192.168.4.1";
static const uint16_t PI_SERVER_PORT = 5000;

void piEspSetup() {
  Serial.begin(115200);
  while (!Serial) {
    delay(10);
  }

  WiFi.mode(WIFI_STA);
  WiFi.begin(AP_SSID, AP_PASSWORD);
  Serial.print("Connecting to ");
  Serial.println(AP_SSID);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println();
  Serial.print("Connected, IP: ");
  Serial.println(WiFi.localIP());

  WiFiClient client;
  Serial.println("Connecting to Pi server...");
  if (!client.connect(PI_SERVER_IP, PI_SERVER_PORT)) {
    Serial.println("Connection to Pi server failed");
    return;
  }
  Serial.println("Connected to Pi server");

  client.print("hi from ESP32");

  unsigned long start = millis();
  while (client.available() == 0 && millis() - start < 5000) {
    delay(10);
  }

  if (client.available()) {
    String reply = client.readString();
    Serial.print("Reply from Pi: ");
    Serial.println(reply);
  } else {
    Serial.println("No reply received (timeout)");
  }

  client.stop();
}

void piEspLoop() {
  // One-shot test; nothing to do here.
}
