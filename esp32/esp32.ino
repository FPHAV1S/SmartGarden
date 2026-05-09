#include <WiFi.h>
#include <HTTPClient.h>
#include <WebServer.h>
#include <Wire.h>
#include <PubSubClient.h>
#include <Adafruit_Sensor.h>
#include <Adafruit_BME280.h>
#include <esp_wifi.h>
#include <esp_system.h>
#include <math.h>

const char* ssid = "GardenLaptop";
const char* password = "GardenBrain123";
const char* espDeviceName = "esp32-zone-1";
const char* wifiHostname = espDeviceName;
const char* firmwareBuild = "live-moisture-readings-2026-05-09";

// ESP connects to the Windows laptop Mobile Hotspot.
// The laptop can still use the phone hotspot as its upstream internet connection.
IPAddress localIp(192, 168, 137, 100);
IPAddress gateway(192, 168, 137, 1);
IPAddress subnet(255, 255, 255, 0);
IPAddress primaryDns(192, 168, 137, 1);
IPAddress secondaryDns(8, 8, 8, 8);

const char* laptopServerIp = "192.168.137.1";
const int apiPort = 5000;
const char* apiPath = "/api/sensor-readings";

// MQTT valve commands published by the ASP.NET app
const char* mqttServer = laptopServerIp;
const int mqttPort = 1883;
const char* valveCommandTopic = "irrigation/zone/+/valve";

const int sensorZoneId = 1;

struct ZoneValveMapping {
  int zoneId;
  int valveNumber;
};

// Add or change rows here when this ESP controls valves for different zones.
// Commands for zones not listed here are ignored by this ESP.
const ZoneValveMapping zoneValveMap[] = {
  { 1, 1 },
  { 2, 2 }
};

const unsigned long sendIntervalMs = 5000;
const unsigned long startupSendIntervalMs = 5000;
const unsigned long reducedLoadAfterBootMs = 300000;
const unsigned long serialHeartbeatIntervalMs = 5000;
const unsigned long disconnectedSensorLogIntervalMs = 5000;
const int debugLogLineCount = 80;
const uint16_t httpConnectTimeoutMs = 2000;
const uint16_t httpRequestTimeoutMs = 4000;
const unsigned long mqttStartupDelayMs = 120000;
const unsigned long mqttReconnectIntervalMs = 30000;
const unsigned long bmeRetryIntervalMs = 30000;
const unsigned long wifiReconnectIntervalMs = 5000;
const unsigned long powerSettleDelayMs = 30000;
const int maxConsecutiveHttpFailuresBeforeWifiReset = 3;
const int wifiConnectAttempts = 40;
const int wifiStatusLogEveryAttempts = 5;
const bool scanWifiBeforeConnect = true;
const int wifiTxPowerQuarterDbm = 78;  // 19.5 dBm; stronger association with Windows Mobile Hotspot.

#define SOIL_PIN 0
#define BME_SDA_PIN 8
#define BME_SCL_PIN 9

#define MOSFET1_PIN 6
#define MOSFET2_PIN 7

// Most XY-MOS modules use HIGH = ON, LOW = OFF
const bool MOSFET_ACTIVE_HIGH = true;

// Raw ADC values for this sensor. Dry reads high, wet reads low.
const int dryValue = 3730;
const int wetValue = 200;
const int soilSampleCount = 25;
const int soilSampleDelayMs = 4;
// Keep this at 0 while calibrating so small changes are visible.
const float dryAirDeadbandPercent = 0.0;
const int soilAdcUnstableRangeLimit = 250;
const int soilShortRawLimit = 50;
// Keep this false while testing by moving the probe between dry/wet conditions.
const bool emergencyHoldLastSoilReading = false;
const float emergencyStartupSoilFallbackPercent = 0.0;
const float suspiciousWetSpikePercent = 25.0;
const float maxAcceptedMoistureJumpPercent = 10.0;
const unsigned long standalonePowerGuardMs = 300000;

WebServer server(80);
Adafruit_BME280 bme;
WiFiClient mqttWifiClient;
PubSubClient mqttClient(mqttWifiClient);

uint8_t targetBssid[6] = { 0, 0, 0, 0, 0, 0 };
int32_t targetChannel = 0;
int32_t targetRssi = -1000;
bool targetNetworkFound = false;

int readingCount = 0;
unsigned long lastSend = 0;
unsigned long lastSerialHeartbeat = 0;
unsigned long lastDisconnectedSensorLog = 0;
unsigned long lastWifiReconnectAttempt = 0;
unsigned long lastMqttReconnectAttempt = 0;
unsigned long lastBmeInitAttempt = 0;

int lastHttpCode = 0;
String lastResponse = "No request sent yet";
String lastMqttCommand = "No MQTT valve command received yet";
String networkStatus = "No network attempts yet";
int consecutiveHttpFailures = 0;
unsigned long lastHttpAttemptAt = 0;
unsigned long lastSuccessfulHttpPostAt = 0;

bool bmeReady = false;
uint8_t bmeAddress = 0;
String bmeStatus = "BME280 not initialized";

float temperature = 24.5;  // fallback until the first BME280 reading succeeds
float humidity = 60.0;     // fallback until the first BME280 reading succeeds
float pressureHpa = 0.0;

int soilRaw = 0;
int soilRawMin = 0;
int soilRawMax = 0;
int soilRawRange = 0;
float soilMoisture = 0.0;
String soilStatus = "No soil reading yet";
String bootResetReason = "unknown";
bool hasAcceptedSoilReading = false;

bool valve1State = false;
bool valve2State = false;
unsigned long valve1AutoOffAt = 0;
unsigned long valve2AutoOffAt = 0;

String debugLogLines[debugLogLineCount];
int debugLogNextIndex = 0;
int debugLogStoredLines = 0;

void handleWifiEvent(WiFiEvent_t event, WiFiEventInfo_t info);
const char* wifiDisconnectReasonText(uint8_t reason);
void configureWifiRadio();
void scanForWifiNetwork();
String bssidText(const uint8_t* bssid);
String targetBssidText();
const char* wifiAuthText(int auth);
String sensorApiUrl();
const char* resetReasonText(esp_reset_reason_t reason);
float fallbackSoilMoisture();
unsigned long currentSendIntervalMs();
bool mqttAllowed();
void recordHttpResult(bool success);
bool waitForWifiJoin(const char* attemptLabel);
void logWifiConnected();
void waitWithSerialProgress(unsigned long durationMs, const char* label);
void printSerialHeartbeat();
void addDebugLog(String message);
String debugLogText();
void handleLogs();

void setup() {
  Serial.begin(115200);
  Serial.setDebugOutput(true);
  delay(2000);

  Serial.println();
  Serial.println("GardenBrain ESP32-C3 booting at 115200 baud.");
  addDebugLog("Booting at 115200 baud");

  pinMode(MOSFET1_PIN, OUTPUT);
  pinMode(MOSFET2_PIN, OUTPUT);
  setValve(1, false);
  setValve(2, false);

  bootResetReason = resetReasonText(esp_reset_reason());

  Serial.print("GardenBrain ESP32-C3 device: ");
  Serial.println(espDeviceName);
  Serial.print("Firmware build: ");
  Serial.println(firmwareBuild);
  addDebugLog(String("Firmware build: ") + firmwareBuild);
  Serial.print("Boot reset reason: ");
  Serial.println(bootResetReason);
  addDebugLog(String("Boot reset reason: ") + bootResetReason);
  Serial.print("Sensor zone: ");
  Serial.println(sensorZoneId);
  Serial.println("Soil Moisture + BME280 + XY-MOS + MQTT valve commands");
  Serial.println("BME280 I2C pins: SDA GPIO8, SCL GPIO9");
  Serial.println("Serial is alive at 115200 baud.");

  Serial.print("Waiting ");
  Serial.print(powerSettleDelayMs / 1000);
  Serial.println(" seconds for PSU voltage to settle before sensor and WiFi startup.");
  waitWithSerialProgress(powerSettleDelayMs, "PSU settle");

  analogReadResolution(12);
  analogSetPinAttenuation(SOIL_PIN, ADC_11db);
  Wire.begin(BME_SDA_PIN, BME_SCL_PIN);
  Wire.setClock(100000);
  initializeBme280();

  WiFi.mode(WIFI_STA);
  configureWifiRadio();
  WiFi.disconnect(true, true);
  delay(500);

  WiFi.persistent(false);
  WiFi.onEvent(handleWifiEvent);

  connectToWifi();

  mqttClient.setServer(mqttServer, mqttPort);
  mqttClient.setCallback(handleMqttMessage);

  server.on("/", handleRoot);
  server.on("/data", handleData);
  server.on("/logs", handleLogs);
  server.on("/send-now", handleSendNow);
  server.on("/valve", handleValve);
  server.onNotFound(handleNotFound);

  server.begin();

  Serial.println("Local web server started.");
  Serial.print("Open ESP32 page: http://");
  Serial.println(WiFi.localIP());
  addDebugLog(String("Local web server started: http://") + WiFi.localIP().toString());

  if (WiFi.status() == WL_CONNECTED) {
    lastMqttCommand = "MQTT delayed during standalone power stabilization";
    updateSensorData();
    sendReading();
    lastSend = millis();
  }
}

void loop() {
  server.handleClient();
  printSerialHeartbeat();

  if (WiFi.status() != WL_CONNECTED) {
    unsigned long now = millis();
    if (now - lastWifiReconnectAttempt >= wifiReconnectIntervalMs) {
      Serial.println("WiFi disconnected. Reconnecting...");
      connectToWifi();
    }

    if (now - lastDisconnectedSensorLog >= disconnectedSensorLogIntervalMs) {
      lastDisconnectedSensorLog = now;
      updateSensorData();
      Serial.println("WiFi is offline, so this reading was logged locally but not posted.");
    }
  }

  if (WiFi.status() == WL_CONNECTED) {
    if (mqttAllowed()) {
      ensureMqttConnected();
      mqttClient.loop();
    }
  }

  handleValveAutoOff();

  if (WiFi.status() == WL_CONNECTED && millis() - lastSend >= currentSendIntervalMs()) {
    lastSend = millis();

    updateSensorData();
    sendReading();
  }

  delay(10);
}

void initializeBme280() {
  lastBmeInitAttempt = millis();

  Serial.print("Initializing BME280 on SDA GPIO");
  Serial.print(BME_SDA_PIN);
  Serial.print(", SCL GPIO");
  Serial.print(BME_SCL_PIN);
  Serial.println("...");

  bmeReady = false;
  bmeAddress = 0;

  if (bme.begin(0x76, &Wire)) {
    bmeAddress = 0x76;
    bmeReady = true;
  } else if (bme.begin(0x77, &Wire)) {
    bmeAddress = 0x77;
    bmeReady = true;
  }

  if (!bmeReady) {
    bmeStatus = "BME280 not found at 0x76 or 0x77";
    Serial.println(bmeStatus);
    Serial.println("Check BME280 VIN/3V3, GND, SDA GPIO8, SCL GPIO9, and sensor address jumper.");
    return;
  }

  bme.setSampling(
    Adafruit_BME280::MODE_NORMAL,
    Adafruit_BME280::SAMPLING_X2,
    Adafruit_BME280::SAMPLING_X16,
    Adafruit_BME280::SAMPLING_X1,
    Adafruit_BME280::FILTER_X16,
    Adafruit_BME280::STANDBY_MS_500
  );

  bmeStatus = "BME280 online at ";
  bmeStatus += bmeAddressText();
  Serial.println(bmeStatus);
}

void ensureBme280Ready() {
  if (bmeReady) {
    return;
  }

  unsigned long now = millis();
  if (now - lastBmeInitAttempt < bmeRetryIntervalMs) {
    return;
  }

  initializeBme280();
}

void readBme280() {
  ensureBme280Ready();

  if (!bmeReady) {
    return;
  }

  float nextTemperature = bme.readTemperature();
  float nextHumidity = bme.readHumidity();
  float nextPressureHpa = bme.readPressure() / 100.0F;

  if (isnan(nextTemperature) || isnan(nextHumidity) || isnan(nextPressureHpa)) {
    bmeReady = false;
    bmeStatus = "BME280 read failed; keeping previous values";
    Serial.println(bmeStatus);
    return;
  }

  temperature = nextTemperature;
  humidity = nextHumidity;
  pressureHpa = nextPressureHpa;

  if (humidity < 0.0) humidity = 0.0;
  if (humidity > 100.0) humidity = 100.0;
  if (pressureHpa < 0.0) pressureHpa = 0.0;

  bmeStatus = "BME280 online at ";
  bmeStatus += bmeAddressText();
}

String bmeAddressText() {
  if (bmeAddress == 0) {
    return "none";
  }

  String address = String(bmeAddress, HEX);
  address.toUpperCase();

  String text = "0x";
  text += address;
  return text;
}

void connectToWifi() {
  lastWifiReconnectAttempt = millis();

  WiFi.setAutoReconnect(false);
  resetWifiStation();

  Serial.print("Connecting to ");
  Serial.print(ssid);
  Serial.println("...");
  if (scanWifiBeforeConnect) {
    scanForWifiNetwork();
    Serial.println("Connecting to the laptop hotspot with static IP after scan diagnostics.");
  } else {
    targetNetworkFound = false;
    targetChannel = 0;
    Serial.println("Skipping startup WiFi scan to reduce cold-boot current draw.");
  }

  if (tryWifiConnection()) {
    return;
  }

  addDebugLog(String("Failed to connect to ") + ssid + " status " + String(WiFi.status()) + " (" + wifiStatusText(WiFi.status()) + ")");

  Serial.print("Failed to connect to ");
  Serial.println(ssid);
  Serial.print("WiFi status code: ");
  Serial.print(WiFi.status());
  Serial.print(" (");
  Serial.print(wifiStatusText(WiFi.status()));
  Serial.println(")");
  Serial.println("Make sure Windows Mobile Hotspot is enabled with SSID/password matching this sketch.");
  Serial.println("The laptop can be connected to the phone hotspot as its upstream internet source.");
  Serial.println("Reason auth expired/auth fail means the ESP is failing before IP setup; re-check hotspot password/security.");
  Serial.println("Laptop-hotspot mode uses ESP static IP 192.168.137.100 and laptop target 192.168.137.1.");
}

void resetWifiStation() {
  WiFi.setAutoReconnect(false);
  WiFi.disconnect(false);

  unsigned long startedAt = millis();
  while (WiFi.status() != WL_DISCONNECTED && millis() - startedAt < 2000) {
    delay(100);
  }

  WiFi.mode(WIFI_OFF);
  delay(300);
  WiFi.mode(WIFI_STA);
  delay(300);

  configureWifiRadio();
  WiFi.setHostname(wifiHostname);
  WiFi.setSleep(false);
}

bool tryWifiConnection() {
  Serial.println("Using static IP 192.168.137.100.");
  if (!WiFi.config(localIp, gateway, subnet, primaryDns, secondaryDns)) {
    Serial.println("Static IP configuration failed.");
  }

  int beginStatus;

  if (targetNetworkFound && targetChannel > 0) {
    Serial.print("Connecting to scanned AP on channel ");
    Serial.print(targetChannel);
    Serial.print(" BSSID ");
    Serial.println(targetBssidText());
    beginStatus = WiFi.begin(ssid, password, targetChannel, targetBssid);
  } else {
    Serial.println("Connecting with normal SSID/password join.");
    beginStatus = WiFi.begin(ssid, password);
  }

  Serial.print("WiFi.begin returned ");
  Serial.print(beginStatus);
  Serial.print(" (");
  Serial.print(wifiStatusText(beginStatus));
  Serial.println("). This is the immediate state; the connection attempt continues below.");

  if (waitForWifiJoin(targetNetworkFound ? "scanned AP" : "normal SSID")) {
    return true;
  }

  if (targetNetworkFound) {
    Serial.println("Scanned AP join failed. Retrying once with normal SSID/password join.");
    addDebugLog("Scanned AP join failed; retrying without channel/BSSID lock");
    WiFi.disconnect(false);
    delay(500);
    targetNetworkFound = false;

    beginStatus = WiFi.begin(ssid, password);

    Serial.print("Fallback WiFi.begin returned ");
    Serial.print(beginStatus);
    Serial.print(" (");
    Serial.print(wifiStatusText(beginStatus));
    Serial.println(").");

    if (waitForWifiJoin("fallback normal SSID")) {
      return true;
    }
  }

  Serial.print("Attempt ended with status ");
  Serial.print(WiFi.status());
  Serial.print(" (");
  Serial.print(wifiStatusText(WiFi.status()));
  Serial.println(")");
  addDebugLog(String("WiFi attempt ended with status ") + String(WiFi.status()) + " (" + wifiStatusText(WiFi.status()) + ")");
  return false;
}

bool waitForWifiJoin(const char* attemptLabel) {
  int attempts = 0;
  int lastStatus = -1;

  while (WiFi.status() != WL_CONNECTED && attempts < wifiConnectAttempts) {
    int status = WiFi.status();
    if (status != lastStatus || attempts % wifiStatusLogEveryAttempts == 0) {
      Serial.println();
      Serial.print("WiFi status: ");
      Serial.print(status);
      Serial.print(" (");
      Serial.print(wifiStatusText(status));
      Serial.println(")");
      lastStatus = status;
    }

    delay(500);
    attempts++;
  }

  Serial.println();

  if (WiFi.status() == WL_CONNECTED) {
    addDebugLog(String("WiFi join succeeded using ") + attemptLabel);
    logWifiConnected();
    return true;
  }

  addDebugLog(String("WiFi join failed using ") + attemptLabel + ": " + wifiStatusText(WiFi.status()));
  return false;
}

void logWifiConnected() {
  WiFi.setAutoReconnect(true);
  addDebugLog(String("WiFi connected. IP ") + WiFi.localIP().toString() + ", RSSI " + String(WiFi.RSSI()) + " dBm");
  Serial.println("Connected successfully.");
  Serial.print("ESP32-C3 IP address: ");
  Serial.println(WiFi.localIP());
  Serial.print("Gateway: ");
  Serial.println(WiFi.gatewayIP());
  Serial.print("Laptop API target: ");
  Serial.println(sensorApiUrl());
  Serial.print("Signal strength: ");
  Serial.print(WiFi.RSSI());
  Serial.println(" dBm");
}

void handleWifiEvent(WiFiEvent_t event, WiFiEventInfo_t info) {
  if (event != ARDUINO_EVENT_WIFI_STA_DISCONNECTED) {
    return;
  }

  Serial.print("WiFi disconnected reason: ");
  Serial.print(info.wifi_sta_disconnected.reason);
  Serial.print(" (");
  Serial.print(wifiDisconnectReasonText(info.wifi_sta_disconnected.reason));
  Serial.println(")");
  addDebugLog(String("WiFi disconnected: ") + String(info.wifi_sta_disconnected.reason) + " (" + wifiDisconnectReasonText(info.wifi_sta_disconnected.reason) + ")");
}

const char* wifiDisconnectReasonText(uint8_t reason) {
  switch (reason) {
    case 2:
      return "auth expired";
    case 3:
      return "auth leave";
    case 4:
      return "assoc expired";
    case 5:
      return "assoc too many";
    case 7:
      return "not authenticated";
    case 8:
      return "not associated";
    case 15:
      return "4-way handshake timeout";
    case 201:
      return "no AP found";
    case 202:
      return "auth fail";
    case 203:
      return "assoc fail";
    case 204:
      return "handshake timeout";
    default:
      return "unknown";
  }
}

void configureWifiRadio() {
  WiFi.setSleep(false);
  WiFi.setMinSecurity(WIFI_AUTH_WPA2_PSK);
  esp_wifi_set_ps(WIFI_PS_NONE);
  esp_wifi_set_protocol(WIFI_IF_STA, WIFI_PROTOCOL_11B | WIFI_PROTOCOL_11G | WIFI_PROTOCOL_11N);
  esp_wifi_set_max_tx_power(wifiTxPowerQuarterDbm);
}

void scanForWifiNetwork() {
  Serial.println("Scanning for WiFi networks...");

  targetNetworkFound = false;
  targetChannel = 0;
  targetRssi = -1000;

  int networkCount = WiFi.scanNetworks(false, true);

  if (networkCount < 0) {
    Serial.print("WiFi scan failed with code ");
    Serial.println(networkCount);
    return;
  }

  int matches = 0;

  for (int i = 0; i < networkCount; i++) {
    if (WiFi.SSID(i) != ssid) {
      continue;
    }

    matches++;

    if (!targetNetworkFound || WiFi.RSSI(i) > targetRssi) {
      targetNetworkFound = true;
      targetChannel = WiFi.channel(i);
      targetRssi = WiFi.RSSI(i);

      uint8_t* bssid = WiFi.BSSID(i);
      if (bssid != nullptr) {
        for (int j = 0; j < 6; j++) {
          targetBssid[j] = bssid[j];
        }
      }
    }

    Serial.print("Found ");
    Serial.print(ssid);
    Serial.print(" | channel ");
    Serial.print(WiFi.channel(i));
    Serial.print(" | BSSID ");
    Serial.print(bssidText(WiFi.BSSID(i)));
    Serial.print(" | RSSI ");
    Serial.print(WiFi.RSSI(i));
    Serial.print(" dBm | auth ");
    Serial.print(WiFi.encryptionType(i));
    Serial.print(" (");
    Serial.print(wifiAuthText(WiFi.encryptionType(i)));
    Serial.println(")");
  }

  if (matches == 0) {
    Serial.print(ssid);
    Serial.println(" was not found by the ESP32-C3 scan.");
    Serial.println("Check that Windows Mobile Hotspot is on and set to 2.4 GHz if that option exists.");
    addDebugLog(String(ssid) + " not found in WiFi scan. Check hotspot is on, 2.4 GHz, and SSID spelling.");
  } else if (matches > 1) {
    Serial.print("Warning: found ");
    Serial.print(matches);
    Serial.print(" networks named ");
    Serial.print(ssid);
    Serial.println(". Rename the laptop hotspot if the ESP may be choosing the wrong one.");
    addDebugLog(String("Found ") + String(matches) + " networks named " + ssid + "; using strongest RSSI " + String(targetRssi) + " dBm");
  } else {
    addDebugLog(String("Found ") + ssid + " on channel " + String(targetChannel) + " BSSID " + targetBssidText() + " RSSI " + String(targetRssi) + " dBm");
  }

  WiFi.scanDelete();
}

String targetBssidText() {
  return bssidText(targetBssid);
}

String bssidText(const uint8_t* bssid) {
  if (bssid == nullptr) {
    return "unknown";
  }

  char text[18];
  snprintf(
    text,
    sizeof(text),
    "%02X:%02X:%02X:%02X:%02X:%02X",
    bssid[0],
    bssid[1],
    bssid[2],
    bssid[3],
    bssid[4],
    bssid[5]
  );

  return String(text);
}

const char* wifiAuthText(int auth) {
  switch (auth) {
    case 0:
      return "open";
    case 1:
      return "WEP";
    case 2:
      return "WPA PSK";
    case 3:
      return "WPA2 PSK";
    case 4:
      return "WPA/WPA2 PSK";
    case 5:
      return "WPA2 Enterprise";
    case 6:
      return "WPA3 PSK";
    case 7:
      return "WPA2/WPA3 PSK";
    default:
      return "unknown";
  }
}

String sensorApiUrl() {
  return String("http://") + laptopServerIp + ":" + String(apiPort) + apiPath;
}

const char* resetReasonText(esp_reset_reason_t reason) {
  switch (reason) {
    case ESP_RST_POWERON:
      return "power on";
    case ESP_RST_EXT:
      return "external reset";
    case ESP_RST_SW:
      return "software reset";
    case ESP_RST_PANIC:
      return "panic";
    case ESP_RST_INT_WDT:
      return "interrupt watchdog";
    case ESP_RST_TASK_WDT:
      return "task watchdog";
    case ESP_RST_WDT:
      return "watchdog";
    case ESP_RST_DEEPSLEEP:
      return "deep sleep wake";
    case ESP_RST_BROWNOUT:
      return "brownout";
    case ESP_RST_SDIO:
      return "SDIO";
    default:
      return "unknown";
  }
}

const char* wifiStatusText(int status) {
  switch (status) {
    case WL_IDLE_STATUS:
      return "idle";
    case WL_NO_SSID_AVAIL:
      return "SSID not available";
    case WL_SCAN_COMPLETED:
      return "scan completed";
    case WL_CONNECTED:
      return "connected";
    case WL_CONNECT_FAILED:
      return "connect failed";
    case WL_CONNECTION_LOST:
      return "connection lost";
    case WL_DISCONNECTED:
      return "disconnected";
    default:
      return "unknown";
  }
}

void ensureMqttConnected() {
  if (!mqttAllowed()) {
    return;
  }

  if (mqttClient.connected()) {
    return;
  }

  unsigned long now = millis();
  if (now - lastMqttReconnectAttempt < mqttReconnectIntervalMs) {
    return;
  }

  lastMqttReconnectAttempt = now;
  connectToMqtt();
}

void connectToMqtt() {
  if (!mqttAllowed()) {
    lastMqttCommand = "MQTT delayed during standalone power stabilization";
    return;
  }

  if (WiFi.status() != WL_CONNECTED || mqttClient.connected()) {
    return;
  }

  String clientId = String("GardenBrain-") + espDeviceName + "-" + String((uint32_t)ESP.getEfuseMac(), HEX);

  Serial.print("Connecting to MQTT broker ");
  Serial.print(mqttServer);
  Serial.print(":");
  Serial.print(mqttPort);
  Serial.print(" as ");
  Serial.println(clientId);

  if (!mqttBrokerPortOpen()) {
    lastMqttCommand = "MQTT broker TCP port unreachable at " + String(mqttServer) + ":" + String(mqttPort);
    Serial.println(lastMqttCommand);
    Serial.println("Check Raspberry Pi: sudo systemctl status mosquitto");
    Serial.println("Check listener: sudo ss -ltnp | grep 1883");
    return;
  }

  if (mqttClient.connect(clientId.c_str())) {
    Serial.println("MQTT connected.");
    if (mqttClient.subscribe(valveCommandTopic)) {
      Serial.print("Subscribed to valve commands: ");
      Serial.println(valveCommandTopic);
    } else {
      Serial.println("Failed to subscribe to valve command topic.");
    }
  } else {
    int state = mqttClient.state();
    Serial.print("MQTT connection failed. State: ");
    Serial.print(state);
    Serial.print(" (");
    Serial.print(mqttStateText(state));
    Serial.println(")");
  }
}

bool mqttBrokerPortOpen() {
  WiFiClient probeClient;
  bool connected = probeClient.connect(mqttServer, mqttPort);
  probeClient.stop();
  return connected;
}

const char* mqttStateText(int state) {
  switch (state) {
    case -4:
      return "connection timeout";
    case -3:
      return "connection lost";
    case -2:
      return "TCP connect failed";
    case -1:
      return "disconnected";
    case 0:
      return "connected";
    case 1:
      return "bad protocol";
    case 2:
      return "client ID rejected";
    case 3:
      return "broker unavailable";
    case 4:
      return "bad credentials";
    case 5:
      return "not authorized";
    default:
      return "unknown";
  }
}

void handleMqttMessage(char* topic, byte* payload, unsigned int length) {
  String body;
  body.reserve(length + 1);

  for (unsigned int i = 0; i < length; i++) {
    body += (char)payload[i];
  }

  int valveNumber = valveNumberFromTopic(topic);

  Serial.print("MQTT message on ");
  Serial.print(topic);
  Serial.print(": ");
  Serial.println(body);

  if (valveNumber != 1 && valveNumber != 2) {
    lastMqttCommand = "Ignored command for unsupported topic: " + String(topic);
    Serial.println(lastMqttCommand);
    return;
  }

  String command = body;
  command.toLowerCase();

  if (isOpenCommand(command)) {
    int durationSeconds = extractDurationSeconds(command);
    openValveForDuration(valveNumber, durationSeconds);
    lastMqttCommand = "Opened valve " + String(valveNumber);
    if (durationSeconds > 0) {
      lastMqttCommand += " for " + String(durationSeconds) + " seconds";
    }
    return;
  }

  if (isCloseCommand(command)) {
    setValve(valveNumber, false);
    lastMqttCommand = "Closed valve " + String(valveNumber);
    return;
  }

  lastMqttCommand = "Unknown MQTT valve command: " + body;
  Serial.println(lastMqttCommand);
}

int valveNumberFromTopic(const char* topic) {
  String topicText = String(topic);
  const String prefix = "irrigation/zone/";
  const String suffix = "/valve";

  if (!topicText.startsWith(prefix) || !topicText.endsWith(suffix)) {
    return -1;
  }

  int start = prefix.length();
  int end = topicText.length() - suffix.length();
  int commandZoneId = topicText.substring(start, end).toInt();

  return localValveForZone(commandZoneId);
}

int localValveForZone(int commandZoneId) {
  for (size_t i = 0; i < sizeof(zoneValveMap) / sizeof(zoneValveMap[0]); i++) {
    if (zoneValveMap[i].zoneId == commandZoneId) {
      return zoneValveMap[i].valveNumber;
    }
  }

  return -1;
}

bool isOpenCommand(String command) {
  return command == "open" ||
         command == "on" ||
         command.indexOf("\"action\":\"open\"") >= 0 ||
         command.indexOf("\"action\": \"open\"") >= 0 ||
         command.indexOf("\"state\":\"open\"") >= 0 ||
         command.indexOf("\"state\":\"on\"") >= 0;
}

bool isCloseCommand(String command) {
  return command == "close" ||
         command == "closed" ||
         command == "off" ||
         command.indexOf("\"action\":\"close\"") >= 0 ||
         command.indexOf("\"action\": \"close\"") >= 0 ||
         command.indexOf("\"state\":\"close\"") >= 0 ||
         command.indexOf("\"state\":\"closed\"") >= 0 ||
         command.indexOf("\"state\":\"off\"") >= 0;
}

int extractDurationSeconds(String command) {
  int durationIndex = command.indexOf("\"duration\"");
  if (durationIndex < 0) {
    return 0;
  }

  int colonIndex = command.indexOf(':', durationIndex);
  if (colonIndex < 0) {
    return 0;
  }

  int start = colonIndex + 1;
  while (start < command.length() && (command[start] == ' ' || command[start] == '"')) {
    start++;
  }

  int end = start;
  while (end < command.length() && command[end] >= '0' && command[end] <= '9') {
    end++;
  }

  if (end == start) {
    return 0;
  }

  int duration = command.substring(start, end).toInt();
  if (duration < 0) {
    return 0;
  }
  if (duration > 3600) {
    return 3600;
  }

  return duration;
}

void updateSensorData() {
  readingCount++;

  soilMoisture = readSoilMoisturePercent();
  readBme280();

  Serial.print("Reading #");
  Serial.print(readingCount);

  Serial.print(" | Temp: ");
  Serial.print(temperature);
  Serial.print(" C");

  Serial.print(" | Humidity: ");
  Serial.print(humidity);
  Serial.print("%");

  Serial.print(" | Pressure: ");
  Serial.print(pressureHpa);
  Serial.print(" hPa");

  Serial.print(" | Soil raw: ");
  Serial.print(soilRaw);
  Serial.print(" (");
  Serial.print(soilRawMin);
  Serial.print("-");
  Serial.print(soilRawMax);
  Serial.print(", range ");
  Serial.print(soilRawRange);
  Serial.print(")");

  Serial.print(" | Moisture: ");
  Serial.print(soilMoisture);
  Serial.print("%");

  Serial.print(" | Soil status: ");
  Serial.print(soilStatus);

  Serial.print(" | BME280: ");
  Serial.print(bmeReady ? "OK" : "offline");

  Serial.print(" | Valve 1: ");
  Serial.print(valve1State ? "ON" : "OFF");

  Serial.print(" | Valve 2: ");
  Serial.println(valve2State ? "ON" : "OFF");

  addDebugLog(
    String("Reading #") + String(readingCount) +
    ": moisture " + String(soilMoisture, 1) +
    "%, raw " + String(soilRaw) +
    ", temp " + String(temperature, 1) +
    " C, humidity " + String(humidity, 1) +
    "%, soil " + soilStatus
  );
}

float readSoilMoisturePercent() {
  soilRaw = readSoilRawAverage();

  if (soilRawRange > soilAdcUnstableRangeLimit) {
    soilStatus = "ADC unstable/noisy; check PSU, ground, and sensor wiring";
    return fallbackSoilMoisture();
  }

  if (soilRaw <= soilShortRawLimit) {
    soilStatus = "ADC near 0; sensor output may be shorted, floating, or unpowered";
    return fallbackSoilMoisture();
  }

  float percent = ((float)(dryValue - soilRaw) / (dryValue - wetValue)) * 100.0;

  if (percent < 0) percent = 0;
  if (percent > 100) percent = 100;
  if (percent <= dryAirDeadbandPercent) percent = 0;

  if (emergencyHoldLastSoilReading) {
    if (!hasAcceptedSoilReading && millis() < standalonePowerGuardMs && percent >= suspiciousWetSpikePercent) {
      soilStatus = "Startup/standalone wet spike ignored; using emergency fallback";
      return emergencyStartupSoilFallbackPercent;
    }

    if (hasAcceptedSoilReading && percent - soilMoisture > maxAcceptedMoistureJumpPercent) {
      soilStatus = "Sudden wet spike ignored; holding previous moisture";
      return soilMoisture;
    }
  }

  hasAcceptedSoilReading = true;
  soilStatus = "OK";
  return percent;
}

float fallbackSoilMoisture() {
  if (hasAcceptedSoilReading) {
    return soilMoisture;
  }

  return emergencyStartupSoilFallbackPercent;
}

int readSoilRawAverage() {
  long total = 0;
  int lowest = 4095;
  int highest = 0;

  analogRead(SOIL_PIN);
  delay(soilSampleDelayMs);

  for (int i = 0; i < soilSampleCount; i++) {
    int sample = analogRead(SOIL_PIN);
    total += sample;

    if (sample < lowest) lowest = sample;
    if (sample > highest) highest = sample;

    delay(soilSampleDelayMs);
  }

  soilRawMin = lowest;
  soilRawMax = highest;
  soilRawRange = highest - lowest;

  if (soilSampleCount > 2) {
    total -= lowest;
    total -= highest;
    return total / (soilSampleCount - 2);
  }

  return total / soilSampleCount;
}

void openValveForDuration(int valveNumber, int durationSeconds) {
  setValve(valveNumber, true);

  if (durationSeconds <= 0) {
    return;
  }

  unsigned long autoOffAt = millis() + ((unsigned long)durationSeconds * 1000UL);
  setValveAutoOffAt(valveNumber, autoOffAt);

  Serial.print("Valve ");
  Serial.print(valveNumber);
  Serial.print(" scheduled to close in ");
  Serial.print(durationSeconds);
  Serial.println(" seconds");
}

void setValve(int valveNumber, bool state) {
  int pin = -1;

  if (valveNumber == 1) {
    pin = MOSFET1_PIN;
    valve1State = state;
  } else if (valveNumber == 2) {
    pin = MOSFET2_PIN;
    valve2State = state;
  } else {
    Serial.println("Invalid valve number.");
    return;
  }

  if (MOSFET_ACTIVE_HIGH) {
    digitalWrite(pin, state ? HIGH : LOW);
  } else {
    digitalWrite(pin, state ? LOW : HIGH);
  }

  setValveAutoOffAt(valveNumber, 0);

  Serial.print("Valve ");
  Serial.print(valveNumber);
  Serial.println(state ? " ON" : " OFF");
  addDebugLog(String("Valve ") + String(valveNumber) + (state ? " ON" : " OFF"));
}

void setValveAutoOffAt(int valveNumber, unsigned long autoOffAt) {
  if (valveNumber == 1) {
    valve1AutoOffAt = autoOffAt;
  } else if (valveNumber == 2) {
    valve2AutoOffAt = autoOffAt;
  }
}

void handleValveAutoOff() {
  unsigned long now = millis();

  if (valve1State && valve1AutoOffAt > 0 && (long)(now - valve1AutoOffAt) >= 0) {
    Serial.println("Valve 1 auto-close time reached.");
    setValve(1, false);
  }

  if (valve2State && valve2AutoOffAt > 0 && (long)(now - valve2AutoOffAt) >= 0) {
    Serial.println("Valve 2 auto-close time reached.");
    setValve(2, false);
  }
}

unsigned long secondsUntilAutoOff(int valveNumber) {
  unsigned long autoOffAt = valveNumber == 1 ? valve1AutoOffAt : valve2AutoOffAt;
  if (autoOffAt == 0) {
    return 0;
  }

  unsigned long now = millis();
  if ((long)(now - autoOffAt) >= 0) {
    return 0;
  }

  return (autoOffAt - now + 999UL) / 1000UL;
}

void sendReading() {
  HTTPClient http;
  String apiUrl = sensorApiUrl();
  lastHttpAttemptAt = millis();
  String payload = buildPayload();

  Serial.print("POST ");
  Serial.println(apiUrl);
  Serial.println(payload);

  if (!http.begin(apiUrl)) {
    lastHttpCode = -7;
    lastResponse = "HTTP begin failed; check laptopServerIp/apiPort";
    addDebugLog(String("HTTP begin failed for ") + apiUrl);

    Serial.print("HTTP status: ");
    Serial.println(lastHttpCode);
    Serial.print("Response: ");
    Serial.println(lastResponse);
    recordHttpResult(false);
    return;
  }

  http.setConnectTimeout(httpConnectTimeoutMs);
  http.setTimeout(httpRequestTimeoutMs);
  http.setReuse(false);
  http.addHeader("Content-Type", "application/json");
  http.addHeader("Connection", "close");

  lastHttpCode = http.POST(payload);
  if (lastHttpCode < 0) {
    lastResponse = http.errorToString(lastHttpCode);
  } else {
    lastResponse = http.getString();
  }

  Serial.print("HTTP status: ");
  Serial.println(lastHttpCode);

  Serial.print("Response: ");
  Serial.println(lastResponse);
  addDebugLog(String("HTTP POST result: ") + String(lastHttpCode) + " - " + lastResponse.substring(0, 120));

  http.end();
  recordHttpResult(lastHttpCode >= 200 && lastHttpCode < 300);
}

void recordHttpResult(bool success) {
  if (success) {
    consecutiveHttpFailures = 0;
    lastSuccessfulHttpPostAt = millis();
    networkStatus = "HTTP OK";
    addDebugLog("HTTP OK");
    return;
  }

  consecutiveHttpFailures++;
  networkStatus = "HTTP failure " + String(consecutiveHttpFailures) + "/" + String(maxConsecutiveHttpFailuresBeforeWifiReset);
  addDebugLog(networkStatus);

  if (consecutiveHttpFailures >= maxConsecutiveHttpFailuresBeforeWifiReset) {
    Serial.println("Repeated HTTP failures; resetting WiFi before the next send.");
    networkStatus = "Resetting WiFi after repeated HTTP failures";
    addDebugLog(networkStatus);
    consecutiveHttpFailures = 0;
    mqttClient.disconnect();
    WiFi.disconnect(false);
    lastWifiReconnectAttempt = 0;
  }
}

unsigned long currentSendIntervalMs() {
  return millis() < reducedLoadAfterBootMs
      ? startupSendIntervalMs
      : sendIntervalMs;
}

bool mqttAllowed() {
  return millis() >= mqttStartupDelayMs;
}

void waitWithSerialProgress(unsigned long durationMs, const char* label) {
  unsigned long startedAt = millis();
  unsigned long nextLogAt = 0;

  while (millis() - startedAt < durationMs) {
    unsigned long elapsedMs = millis() - startedAt;

    if (elapsedMs >= nextLogAt) {
      Serial.print(label);
      Serial.print(": ");
      Serial.print(elapsedMs / 1000);
      Serial.print("/");
      Serial.print(durationMs / 1000);
      Serial.println("s");
      Serial.flush();
      nextLogAt += 1000;
    }

    delay(100);
  }

  Serial.print(label);
  Serial.println(": complete");
}

void printSerialHeartbeat() {
  unsigned long now = millis();
  if (now - lastSerialHeartbeat < serialHeartbeatIntervalMs) {
    return;
  }

  lastSerialHeartbeat = now;

  Serial.print("Heartbeat | uptime: ");
  Serial.print(now / 1000);
  Serial.print("s | WiFi: ");
  Serial.print(wifiStatusText(WiFi.status()));

  if (WiFi.status() == WL_CONNECTED) {
    long nextSendInSeconds = 0;
    unsigned long elapsedSinceSend = now - lastSend;
    unsigned long interval = currentSendIntervalMs();
    if (elapsedSinceSend < interval) {
      nextSendInSeconds = (long)((interval - elapsedSinceSend + 999UL) / 1000UL);
    }

    Serial.print(" | IP: ");
    Serial.print(WiFi.localIP());
    Serial.print(" | RSSI: ");
    Serial.print(WiFi.RSSI());
    Serial.print(" dBm | next HTTP POST in: ");
    Serial.print(nextSendInSeconds);
    Serial.print("s | last HTTP: ");
    Serial.print(lastHttpCode);
  } else {
    Serial.print(" | retry target SSID: ");
    Serial.print(ssid);
  }

  Serial.print(" | network: ");
  Serial.println(networkStatus);
}

void addDebugLog(String message) {
  String line = String(millis() / 1000) + "s " + message;

  if (line.length() > 220) {
    line = line.substring(0, 220);
  }

  debugLogLines[debugLogNextIndex] = line;
  debugLogNextIndex = (debugLogNextIndex + 1) % debugLogLineCount;
  if (debugLogStoredLines < debugLogLineCount) {
    debugLogStoredLines++;
  }

  Serial.println(line);
}

String debugLogText() {
  String text;
  text.reserve(debugLogStoredLines * 80);

  for (int i = 0; i < debugLogStoredLines; i++) {
    int index = (debugLogNextIndex - debugLogStoredLines + i + debugLogLineCount) % debugLogLineCount;
    text += debugLogLines[index];
    text += "\n";
  }

  return text;
}

String buildPayload() {
  String json = "{";
  json += "\"device\":\"" + String(espDeviceName) + "\",";
  json += "\"firmwareBuild\":\"" + String(firmwareBuild) + "\",";
  json += "\"resetReason\":\"" + escapeJson(bootResetReason) + "\",";
  json += "\"zoneId\":" + String(sensorZoneId) + ",";
  json += "\"readingCount\":" + String(readingCount) + ",";
  json += "\"temperature\":" + String(temperature, 2) + ",";
  json += "\"humidity\":" + String(humidity, 2) + ",";
  json += "\"pressure\":" + String(pressureHpa, 2) + ",";
  json += "\"bme280Ready\":" + String(bmeReady ? "true" : "false") + ",";
  json += "\"bme280Status\":\"" + escapeJson(bmeStatus) + "\",";
  json += "\"soilMoisture\":" + String(soilMoisture, 2) + ",";
  json += "\"soilRaw\":" + String(soilRaw) + ",";
  json += "\"soilRawMin\":" + String(soilRawMin) + ",";
  json += "\"soilRawMax\":" + String(soilRawMax) + ",";
  json += "\"soilRawRange\":" + String(soilRawRange) + ",";
  json += "\"soilStatus\":\"" + escapeJson(soilStatus) + "\",";
  json += "\"networkStatus\":\"" + escapeJson(networkStatus) + "\",";
  json += "\"consecutiveHttpFailures\":" + String(consecutiveHttpFailures) + ",";
  json += "\"sendIntervalMs\":" + String(currentSendIntervalMs()) + ",";
  json += "\"mqttAllowed\":" + String(mqttAllowed() ? "true" : "false") + ",";
  json += "\"lastHttpAttemptAgo\":" + String(lastHttpAttemptAt == 0 ? -1 : (long)((millis() - lastHttpAttemptAt) / 1000)) + ",";
  json += "\"lastSuccessfulHttpPostAgo\":" + String(lastSuccessfulHttpPostAt == 0 ? -1 : (long)((millis() - lastSuccessfulHttpPostAt) / 1000)) + ",";
  json += "\"valve1\":" + String(valve1State ? "true" : "false") + ",";
  json += "\"valve2\":" + String(valve2State ? "true" : "false") + ",";
  json += "\"rssi\":" + String(WiFi.RSSI()) + ",";
  json += "\"ip\":\"" + WiFi.localIP().toString() + "\"";
  json += "}";

  return json;
}

void handleRoot() {
  String html = "<!DOCTYPE html><html><head>";
  html += "<meta charset='utf-8'>";
  html += "<meta name='viewport' content='width=device-width, initial-scale=1'>";
  html += "<title>";
  html += espDeviceName;
  html += "</title>";

  html += "<style>";
  html += "body{font-family:Arial;background:#1a1a1a;color:white;max-width:850px;margin:30px auto;padding:20px;}";
  html += ".card{background:#2d2d2d;padding:20px;margin:15px 0;border-radius:8px;border-left:5px solid #4CAF50;}";
  html += "h1{color:#4CAF50;text-align:center;}";
  html += ".value{font-size:30px;color:#4CAF50;font-weight:bold;}";
  html += ".btn{display:inline-block;padding:12px 20px;margin:5px;border-radius:6px;text-decoration:none;color:white;font-weight:bold;}";
  html += ".on{background:#4CAF50;}";
  html += ".off{background:#f44336;}";
  html += "a{color:#4CAF50;}";
  html += "</style>";

  html += "<script>";
  html += "function updateData(){fetch('/data').then(r=>r.json()).then(d=>{";
  html += "document.getElementById('count').innerText=d.readingCount;";
  html += "document.getElementById('temp').innerText=d.temperature.toFixed(1);";
  html += "document.getElementById('humidity').innerText=d.humidity.toFixed(1);";
  html += "document.getElementById('pressure').innerText=d.pressure.toFixed(1);";
  html += "document.getElementById('bmeStatus').innerText=d.bme280Status;";
  html += "document.getElementById('raw').innerText=d.soilRaw;";
  html += "document.getElementById('rawRange').innerText=d.soilRawMin+'-'+d.soilRawMax+' (range '+d.soilRawRange+')';";
  html += "document.getElementById('soil').innerText=d.soilMoisture.toFixed(1);";
  html += "document.getElementById('soilStatus').innerText=d.soilStatus;";
  html += "document.getElementById('resetReason').innerText=d.resetReason;";
  html += "document.getElementById('v1').innerText=d.valve1?'ON':'OFF';";
  html += "document.getElementById('v2').innerText=d.valve2?'ON':'OFF';";
  html += "document.getElementById('v1auto').innerText=d.valve1AutoOffIn;";
  html += "document.getElementById('v2auto').innerText=d.valve2AutoOffIn;";
  html += "document.getElementById('rssi').innerText=d.rssi;";
  html += "document.getElementById('mqtt').innerText=d.mqttAllowed?(d.mqttConnected?'Connected':'Disconnected'):'Delayed';";
  html += "document.getElementById('mqttCommand').innerText=d.lastMqttCommand;";
  html += "document.getElementById('networkStatus').innerText=d.networkStatus;";
  html += "document.getElementById('sendInterval').innerText=(d.sendIntervalMs/1000).toFixed(0);";
  html += "document.getElementById('httpFailures').innerText=d.consecutiveHttpFailures;";
  html += "document.getElementById('lastHttpAttempt').innerText=d.lastHttpAttemptAgo<0?'never':d.lastHttpAttemptAgo+'s ago';";
  html += "document.getElementById('lastHttpSuccess').innerText=d.lastSuccessfulHttpPostAgo<0?'never':d.lastSuccessfulHttpPostAgo+'s ago';";
  html += "document.getElementById('http').innerText=d.lastHttpCode;";
  html += "document.getElementById('response').innerText=d.lastResponse;";
  html += "});}";
  html += "setInterval(updateData,2000);window.onload=updateData;";
  html += "</script>";

  html += "</head><body>";
  html += "<h1>";
  html += espDeviceName;
  html += "</h1>";

  html += "<div class='card'>";
  html += "<p>Status: <span class='value'>";
  html += WiFi.status() == WL_CONNECTED ? "Connected" : "Disconnected";
  html += "</span></p>";
  html += "<p>WiFi AP: ";
  html += ssid;
  html += "</p>";
  html += "<p>ESP32 IP: ";
  html += WiFi.localIP().toString();
  html += "</p>";
  html += "<p>Sensor Zone: ";
  html += String(sensorZoneId);
  html += "</p>";
  html += "<p>API target: ";
  html += sensorApiUrl();
  html += "</p>";
  html += "<p>Reset reason: <span id='resetReason'>";
  html += escapeHtml(bootResetReason);
  html += "</span></p>";
  html += "<p>MQTT broker: ";
  html += mqttServer;
  html += ":";
  html += String(mqttPort);
  html += " (<span id='mqtt'>";
  html += mqttClient.connected() ? "Connected" : "Disconnected";
  html += "</span>)</p>";
  html += "<p>MQTT topic: ";
  html += valveCommandTopic;
  html += "</p>";
  html += "</div>";

  html += "<div class='card'>";
  html += "<p>Reading Count: <span id='count' class='value'>0</span></p>";
  html += "<p>Temperature: <span id='temp' class='value'>";
  html += String(temperature, 1);
  html += "</span> C</p>";
  html += "<p>Humidity: <span id='humidity' class='value'>";
  html += String(humidity, 1);
  html += "</span> %</p>";
  html += "<p>Pressure: <span id='pressure' class='value'>";
  html += String(pressureHpa, 1);
  html += "</span> hPa</p>";
  html += "<p>Soil Raw: <span id='raw' class='value'>0</span></p>";
  html += "<p>Soil Raw Min/Max: <span id='rawRange'>0-0 (range 0)</span></p>";
  html += "<p>Soil Moisture: <span id='soil' class='value'>0</span> %</p>";
  html += "<p>Soil Status: <span id='soilStatus'>";
  html += escapeHtml(soilStatus);
  html += "</span></p>";
  html += "<p>BME280: <span id='bmeStatus'>";
  html += escapeHtml(bmeStatus);
  html += "</span></p>";
  html += "</div>";

  html += "<div class='card'>";
  html += "<h2>XY-MOS Valve Control</h2>";

  html += "<p>Valve 1: <span id='v1' class='value'>OFF</span> <small>auto-off in <span id='v1auto'>0</span>s</small></p>";
  html += "<a class='btn on' href='/valve?valve=1&state=on'>Valve 1 ON</a>";
  html += "<a class='btn on' href='/valve?valve=1&state=open&duration=10'>Valve 1 10s</a>";
  html += "<a class='btn off' href='/valve?valve=1&state=off'>Valve 1 OFF</a>";

  html += "<p>Valve 2: <span id='v2' class='value'>OFF</span> <small>auto-off in <span id='v2auto'>0</span>s</small></p>";
  html += "<a class='btn on' href='/valve?valve=2&state=on'>Valve 2 ON</a>";
  html += "<a class='btn on' href='/valve?valve=2&state=open&duration=10'>Valve 2 10s</a>";
  html += "<a class='btn off' href='/valve?valve=2&state=off'>Valve 2 OFF</a>";

  html += "</div>";

  html += "<div class='card'>";
  html += "<p>Signal Strength: <span id='rssi'>0</span> dBm</p>";
  html += "<p>Network Status: <span id='networkStatus'>";
  html += escapeHtml(networkStatus);
  html += "</span></p>";
  html += "<p>Current Send Interval: <span id='sendInterval'>";
  html += String(currentSendIntervalMs() / 1000);
  html += "</span>s</p>";
  html += "<p>HTTP Failures: <span id='httpFailures'>";
  html += String(consecutiveHttpFailures);
  html += "</span></p>";
  html += "<p>Last HTTP Attempt: <span id='lastHttpAttempt'>never</span></p>";
  html += "<p>Last HTTP Success: <span id='lastHttpSuccess'>never</span></p>";
  html += "<p>Last MQTT Command: <span id='mqttCommand'>";
  html += escapeHtml(lastMqttCommand);
  html += "</span></p>";
  html += "<p>Last HTTP Status: <span id='http'>0</span></p>";
  html += "<p>Last Response: <span id='response'>No request sent yet</span></p>";
  html += "<p><a href='/send-now'>Send now</a> | <a href='/data'>JSON data</a> | <a href='/logs'>Debug logs</a></p>";
  html += "</div>";

  html += "</body></html>";

  server.send(200, "text/html", html);
}

void handleData() {
  String json = "{";
  json += "\"device\":\"" + String(espDeviceName) + "\",";
  json += "\"firmwareBuild\":\"" + String(firmwareBuild) + "\",";
  json += "\"resetReason\":\"" + escapeJson(bootResetReason) + "\",";
  json += "\"sensorZoneId\":" + String(sensorZoneId) + ",";
  json += "\"status\":\"" + String(WiFi.status() == WL_CONNECTED ? "online" : "offline") + "\",";
  json += "\"ip\":\"" + WiFi.localIP().toString() + "\",";
  json += "\"ssid\":\"" + String(WiFi.SSID()) + "\",";
  json += "\"rssi\":" + String(WiFi.RSSI()) + ",";
  json += "\"mqttConnected\":" + String(mqttClient.connected() ? "true" : "false") + ",";
  json += "\"mqttTopic\":\"" + String(valveCommandTopic) + "\",";
  json += "\"lastMqttCommand\":\"" + escapeJson(lastMqttCommand) + "\",";
  json += "\"mqttAllowed\":" + String(mqttAllowed() ? "true" : "false") + ",";
  json += "\"networkStatus\":\"" + escapeJson(networkStatus) + "\",";
  json += "\"consecutiveHttpFailures\":" + String(consecutiveHttpFailures) + ",";
  json += "\"sendIntervalMs\":" + String(currentSendIntervalMs()) + ",";
  json += "\"lastHttpAttemptAgo\":" + String(lastHttpAttemptAt == 0 ? -1 : (long)((millis() - lastHttpAttemptAt) / 1000)) + ",";
  json += "\"lastSuccessfulHttpPostAgo\":" + String(lastSuccessfulHttpPostAt == 0 ? -1 : (long)((millis() - lastSuccessfulHttpPostAt) / 1000)) + ",";
  json += "\"readingCount\":" + String(readingCount) + ",";
  json += "\"temperature\":" + String(temperature, 2) + ",";
  json += "\"humidity\":" + String(humidity, 2) + ",";
  json += "\"pressure\":" + String(pressureHpa, 2) + ",";
  json += "\"bme280Ready\":" + String(bmeReady ? "true" : "false") + ",";
  json += "\"bme280Status\":\"" + escapeJson(bmeStatus) + "\",";
  json += "\"soilRaw\":" + String(soilRaw) + ",";
  json += "\"soilRawMin\":" + String(soilRawMin) + ",";
  json += "\"soilRawMax\":" + String(soilRawMax) + ",";
  json += "\"soilRawRange\":" + String(soilRawRange) + ",";
  json += "\"soilMoisture\":" + String(soilMoisture, 2) + ",";
  json += "\"soilStatus\":\"" + escapeJson(soilStatus) + "\",";
  json += "\"valve1\":" + String(valve1State ? "true" : "false") + ",";
  json += "\"valve2\":" + String(valve2State ? "true" : "false") + ",";
  json += "\"valve1AutoOffIn\":" + String(secondsUntilAutoOff(1)) + ",";
  json += "\"valve2AutoOffIn\":" + String(secondsUntilAutoOff(2)) + ",";
  json += "\"lastHttpCode\":" + String(lastHttpCode) + ",";
  json += "\"lastResponse\":\"" + escapeJson(lastResponse) + "\",";
  json += "\"uptime\":" + String(millis() / 1000);
  json += "}";

  server.send(200, "application/json", json);
}

void handleLogs() {
  server.send(200, "text/plain", debugLogText());
}

void handleValve() {
  if ((!server.hasArg("valve") && !server.hasArg("zone")) || !server.hasArg("state")) {
    server.send(400, "text/plain", "Missing valve/zone or state parameter");
    return;
  }

  int valveNumber = server.hasArg("valve")
    ? server.arg("valve").toInt()
    : localValveForZone(server.arg("zone").toInt());

  String stateArg = server.arg("state");
  stateArg.toLowerCase();

  bool state = stateArg == "on" || stateArg == "open" || stateArg == "1" || stateArg == "true";
  bool validState = state ||
                    stateArg == "off" ||
                    stateArg == "close" ||
                    stateArg == "closed" ||
                    stateArg == "0" ||
                    stateArg == "false";

  if (valveNumber != 1 && valveNumber != 2) {
    server.send(400, "text/plain", "Invalid valve number");
    return;
  }

  if (!validState) {
    server.send(400, "text/plain", "Invalid valve state");
    return;
  }

  int durationSeconds = 0;
  if (server.hasArg("duration")) {
    durationSeconds = server.arg("duration").toInt();
    if (durationSeconds < 0) {
      durationSeconds = 0;
    }
    if (durationSeconds > 3600) {
      durationSeconds = 3600;
    }
  }

  if (state && durationSeconds > 0) {
    openValveForDuration(valveNumber, durationSeconds);
  } else {
    setValve(valveNumber, state);
  }

  server.sendHeader("Location", "/");
  server.send(303);
}

void handleSendNow() {
  updateSensorData();
  sendReading();

  server.send(
    200,
    "text/plain",
    "Sent reading. HTTP status: " + String(lastHttpCode) + "\n" + lastResponse
  );
}

void handleNotFound() {
  server.send(404, "text/plain", "Not found");
}

String escapeHtml(String value) {
  value.replace("&", "&amp;");
  value.replace("<", "&lt;");
  value.replace(">", "&gt;");
  value.replace("\"", "&quot;");
  value.replace("'", "&#39;");
  return value;
}

String escapeJson(String value) {
  value.replace("\\", "\\\\");
  value.replace("\"", "\\\"");
  value.replace("\n", "\\n");
  value.replace("\r", "");
  return value;
}
