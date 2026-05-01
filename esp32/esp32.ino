#include <WiFi.h>
#include <HTTPClient.h>
#include <WebServer.h>
#include <PubSubClient.h>

const char* ssid = "GardenBrain";
const char* password = "";

// ESP32-C3 static IP on Raspberry Pi AP
IPAddress local_IP(192, 168, 4, 100);
IPAddress gateway(192, 168, 4, 1);
IPAddress subnet(255, 255, 255, 0);
IPAddress dns(8, 8, 8, 8);

// ASP.NET API endpoint
const char* apiUrl = "http://192.168.4.1:5000/api/sensor-readings";

// MQTT valve commands published by the ASP.NET app
const char* mqttServer = "192.168.4.1";
const int mqttPort = 1883;
const char* valveCommandTopic = "irrigation/zone/+/valve";

const int zoneId = 1;
const unsigned long sendIntervalMs = 5000;
const unsigned long mqttReconnectIntervalMs = 5000;

#define SOIL_PIN 0

#define MOSFET1_PIN 6
#define MOSFET2_PIN 7

// Most XY-MOS modules use HIGH = ON, LOW = OFF
const bool MOSFET_ACTIVE_HIGH = true;

// Your real soil sensor calibration values
const int dryValue = 3730;
const int wetValue = 200;

WebServer server(80);
WiFiClient mqttWifiClient;
PubSubClient mqttClient(mqttWifiClient);

int readingCount = 0;
unsigned long lastSend = 0;
unsigned long lastMqttReconnectAttempt = 0;

int lastHttpCode = 0;
String lastResponse = "No request sent yet";
String lastMqttCommand = "No MQTT valve command received yet";

float temperature = 24.5;  // fake for now
float humidity = 60.0;    // fake for now

int soilRaw = 0;
float soilMoisture = 0.0;

bool valve1State = false;
bool valve2State = false;
unsigned long valve1AutoOffAt = 0;
unsigned long valve2AutoOffAt = 0;

void setup() {
  Serial.begin(115200);
  delay(2000);

  Serial.println();
  Serial.println("GardenBrain ESP32-C3");
  Serial.println("Soil Moisture + XY-MOS + MQTT valve commands");
  Serial.println("No BME280, no buck converter");

  analogReadResolution(12);

  pinMode(MOSFET1_PIN, OUTPUT);
  pinMode(MOSFET2_PIN, OUTPUT);

  setValve(1, false);
  setValve(2, false);

  WiFi.disconnect(true);
  delay(500);

  WiFi.mode(WIFI_STA);

  if (!WiFi.config(local_IP, gateway, subnet, dns)) {
    Serial.println("Static IP configuration failed.");
  }

  WiFi.begin(ssid, password);
  connectToWifi();

  mqttClient.setServer(mqttServer, mqttPort);
  mqttClient.setCallback(handleMqttMessage);

  server.on("/", handleRoot);
  server.on("/data", handleData);
  server.on("/send-now", handleSendNow);
  server.on("/valve", handleValve);
  server.onNotFound(handleNotFound);

  server.begin();

  Serial.println("Local web server started.");
  Serial.print("Open ESP32 page: http://");
  Serial.println(WiFi.localIP());

  if (WiFi.status() == WL_CONNECTED) {
    connectToMqtt();
    updateSensorData();
    sendReading();
    lastSend = millis();
  }
}

void loop() {
  server.handleClient();

  if (WiFi.status() != WL_CONNECTED) {
    Serial.println("WiFi disconnected. Reconnecting...");
    WiFi.disconnect();
    WiFi.begin(ssid, password);
    connectToWifi();
  }

  if (WiFi.status() == WL_CONNECTED) {
    ensureMqttConnected();
    mqttClient.loop();
  }

  handleValveAutoOff();

  if (WiFi.status() == WL_CONNECTED && millis() - lastSend >= sendIntervalMs) {
    lastSend = millis();

    updateSensorData();
    sendReading();
  }

  delay(10);
}

void connectToWifi() {
  Serial.print("Connecting to ");
  Serial.print(ssid);

  int attempts = 0;

  while (WiFi.status() != WL_CONNECTED && attempts < 60) {
    delay(500);
    Serial.print(".");
    attempts++;
  }

  Serial.println();

  if (WiFi.status() == WL_CONNECTED) {
    Serial.println("Connected successfully.");
    Serial.print("ESP32-C3 IP address: ");
    Serial.println(WiFi.localIP());
    Serial.print("Gateway: ");
    Serial.println(WiFi.gatewayIP());
    Serial.print("Signal strength: ");
    Serial.print(WiFi.RSSI());
    Serial.println(" dBm");
  } else {
    Serial.println("Failed to connect to Raspberry Pi AP.");
    Serial.println("Check that the AP name is exactly GardenBrain.");
  }
}

void ensureMqttConnected() {
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
  if (WiFi.status() != WL_CONNECTED || mqttClient.connected()) {
    return;
  }

  String clientId = "GardenBrain-esp32-c3-" + String((uint32_t)ESP.getEfuseMac(), HEX);

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

  // Zone 1 controls MOSFET 1 and Zone 2 controls MOSFET 2 on this ESP32.
  return commandZoneId;
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

  // Fake values until BME280 is added later
  temperature += 0.1;
  humidity += 0.2;

  if (temperature > 30.0) temperature = 24.5;
  if (humidity > 80.0) humidity = 60.0;

  Serial.print("Reading #");
  Serial.print(readingCount);

  Serial.print(" | Soil raw: ");
  Serial.print(soilRaw);

  Serial.print(" | Moisture: ");
  Serial.print(soilMoisture);
  Serial.print("%");

  Serial.print(" | Valve 1: ");
  Serial.print(valve1State ? "ON" : "OFF");

  Serial.print(" | Valve 2: ");
  Serial.println(valve2State ? "ON" : "OFF");
}

float readSoilMoisturePercent() {
  soilRaw = analogRead(SOIL_PIN);

  float percent = ((float)(dryValue - soilRaw) / (dryValue - wetValue)) * 100.0;

  if (percent < 0) percent = 0;
  if (percent > 100) percent = 100;

  return percent;
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
  String payload = buildPayload();

  Serial.print("POST ");
  Serial.println(apiUrl);
  Serial.println(payload);

  http.begin(apiUrl);
  http.addHeader("Content-Type", "application/json");

  lastHttpCode = http.POST(payload);
  lastResponse = http.getString();

  Serial.print("HTTP status: ");
  Serial.println(lastHttpCode);

  Serial.print("Response: ");
  Serial.println(lastResponse);

  http.end();
}

String buildPayload() {
  String json = "{";
  json += "\"device\":\"esp32-c3\",";
  json += "\"zoneId\":" + String(zoneId) + ",";
  json += "\"readingCount\":" + String(readingCount) + ",";
  json += "\"temperature\":" + String(temperature, 2) + ",";
  json += "\"humidity\":" + String(humidity, 2) + ",";
  json += "\"soilMoisture\":" + String(soilMoisture, 2) + ",";
  json += "\"soilRaw\":" + String(soilRaw) + ",";
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
  html += "<title>GardenBrain ESP32-C3</title>";

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
  html += "document.getElementById('raw').innerText=d.soilRaw;";
  html += "document.getElementById('soil').innerText=d.soilMoisture.toFixed(1);";
  html += "document.getElementById('v1').innerText=d.valve1?'ON':'OFF';";
  html += "document.getElementById('v2').innerText=d.valve2?'ON':'OFF';";
  html += "document.getElementById('v1auto').innerText=d.valve1AutoOffIn;";
  html += "document.getElementById('v2auto').innerText=d.valve2AutoOffIn;";
  html += "document.getElementById('rssi').innerText=d.rssi;";
  html += "document.getElementById('mqtt').innerText=d.mqttConnected?'Connected':'Disconnected';";
  html += "document.getElementById('mqttCommand').innerText=d.lastMqttCommand;";
  html += "document.getElementById('http').innerText=d.lastHttpCode;";
  html += "document.getElementById('response').innerText=d.lastResponse;";
  html += "});}";
  html += "setInterval(updateData,2000);window.onload=updateData;";
  html += "</script>";

  html += "</head><body>";
  html += "<h1>GardenBrain ESP32-C3</h1>";

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
  html += "<p>API target: ";
  html += apiUrl;
  html += "</p>";
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
  html += "<p>Soil Raw: <span id='raw' class='value'>0</span></p>";
  html += "<p>Soil Moisture: <span id='soil' class='value'>0</span> %</p>";
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
  html += "<p>Last MQTT Command: <span id='mqttCommand'>";
  html += escapeJson(lastMqttCommand);
  html += "</span></p>";
  html += "<p>Last HTTP Status: <span id='http'>0</span></p>";
  html += "<p>Last Response: <span id='response'>No request sent yet</span></p>";
  html += "<p><a href='/send-now'>Send now</a> | <a href='/data'>JSON data</a></p>";
  html += "</div>";

  html += "</body></html>";

  server.send(200, "text/html", html);
}

void handleData() {
  String json = "{";
  json += "\"device\":\"esp32-c3\",";
  json += "\"status\":\"" + String(WiFi.status() == WL_CONNECTED ? "online" : "offline") + "\",";
  json += "\"ip\":\"" + WiFi.localIP().toString() + "\",";
  json += "\"ssid\":\"" + String(WiFi.SSID()) + "\",";
  json += "\"rssi\":" + String(WiFi.RSSI()) + ",";
  json += "\"mqttConnected\":" + String(mqttClient.connected() ? "true" : "false") + ",";
  json += "\"mqttTopic\":\"" + String(valveCommandTopic) + "\",";
  json += "\"lastMqttCommand\":\"" + escapeJson(lastMqttCommand) + "\",";
  json += "\"readingCount\":" + String(readingCount) + ",";
  json += "\"temperature\":" + String(temperature, 2) + ",";
  json += "\"humidity\":" + String(humidity, 2) + ",";
  json += "\"soilRaw\":" + String(soilRaw) + ",";
  json += "\"soilMoisture\":" + String(soilMoisture, 2) + ",";
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

void handleValve() {
  if ((!server.hasArg("valve") && !server.hasArg("zone")) || !server.hasArg("state")) {
    server.send(400, "text/plain", "Missing valve/zone or state parameter");
    return;
  }

  int valveNumber = server.hasArg("valve")
    ? server.arg("valve").toInt()
    : server.arg("zone").toInt();

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

String escapeJson(String value) {
  value.replace("\\", "\\\\");
  value.replace("\"", "\\\"");
  value.replace("\n", "\\n");
  value.replace("\r", "");
  return value;
}
