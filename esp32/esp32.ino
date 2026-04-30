#include <WiFi.h>
#include <HTTPClient.h>
#include <WebServer.h>

const char* ssid = "GardenBrain";
const char* password = "";

IPAddress local_IP(192, 168, 4, 100);
IPAddress gateway(192, 168, 4, 1);
IPAddress subnet(255, 255, 255, 0);
IPAddress dns(8, 8, 8, 8);

const char* apiUrl = "http://192.168.4.1:5000/api/sensor-readings";
const int zoneId = 1;
const unsigned long sendIntervalMs = 5000;

WebServer server(80);

int readingCount = 0;
unsigned long lastSend = 0;
int lastHttpCode = 0;
String lastResponse = "No request sent yet";

float testTemperature = 24.5;
float testHumidity = 60.0;
float testSoilMoisture = 45.0;

void setup() {
  Serial.begin(115200);
  delay(2000);

  Serial.println();
  Serial.println("SmartGarden ESP32-C3 sender");
  Serial.println("Connecting to Raspberry Pi AP...");

  WiFi.disconnect(true);
  delay(500);
  WiFi.mode(WIFI_STA);

  if (!WiFi.config(local_IP, gateway, subnet, dns)) {
    Serial.println("Static IP configuration failed.");
  }

  WiFi.begin(ssid, password);
  connectToWifi();

  server.on("/", handleRoot);
  server.on("/data", handleData);
  server.on("/send-now", handleSendNow);
  server.onNotFound(handleNotFound);
  server.begin();

  Serial.println("Local status server started.");
  Serial.print("ESP32 status page: http://");
  Serial.println(WiFi.localIP());
  Serial.print("ASP.NET API target: ");
  Serial.println(apiUrl);

  if (WiFi.status() == WL_CONNECTED) {
    updateTestData();
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

  if (WiFi.status() == WL_CONNECTED && millis() - lastSend >= sendIntervalMs) {
    lastSend = millis();
    updateTestData();
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

void updateTestData() {
  readingCount++;

  testTemperature += 0.1;
  testHumidity += 0.2;
  testSoilMoisture += 0.3;

  if (testTemperature > 30.0) testTemperature = 24.5;
  if (testHumidity > 80.0) testHumidity = 60.0;
  if (testSoilMoisture > 100.0) testSoilMoisture = 45.0;
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
  json += "\"temperature\":" + String(testTemperature, 2) + ",";
  json += "\"humidity\":" + String(testHumidity, 2) + ",";
  json += "\"soilMoisture\":" + String(testSoilMoisture, 2) + ",";
  json += "\"rssi\":" + String(WiFi.RSSI()) + ",";
  json += "\"ip\":\"" + WiFi.localIP().toString() + "\"";
  json += "}";

  return json;
}

void handleRoot() {
  String html = "<!DOCTYPE html><html><head>";
  html += "<meta charset='utf-8'>";
  html += "<meta name='viewport' content='width=device-width, initial-scale=1'>";
  html += "<title>SmartGarden ESP32-C3</title>";
  html += "<style>";
  html += "body{font-family:Arial;background:#1a1a1a;color:white;max-width:800px;margin:40px auto;padding:20px;}";
  html += ".card{background:#2d2d2d;padding:20px;margin:15px 0;border-radius:8px;border-left:5px solid #4CAF50;}";
  html += "h1{color:#4CAF50;text-align:center;}";
  html += ".value{font-size:30px;color:#4CAF50;font-weight:bold;}";
  html += "a{color:#4CAF50;}";
  html += "</style>";
  html += "<script>";
  html += "function updateData(){fetch('/data').then(r=>r.json()).then(d=>{";
  html += "document.getElementById('count').innerText=d.readingCount;";
  html += "document.getElementById('temp').innerText=d.temperature.toFixed(1);";
  html += "document.getElementById('hum').innerText=d.humidity.toFixed(1);";
  html += "document.getElementById('soil').innerText=d.soilMoisture.toFixed(1);";
  html += "document.getElementById('rssi').innerText=d.rssi;";
  html += "document.getElementById('http').innerText=d.lastHttpCode;";
  html += "document.getElementById('response').innerText=d.lastResponse;";
  html += "});}";
  html += "setInterval(updateData,2000);window.onload=updateData;";
  html += "</script>";
  html += "</head><body>";
  html += "<h1>SmartGarden ESP32-C3 Sender</h1>";
  html += "<div class='card'><p>Status: <span class='value'>";
  html += (WiFi.status() == WL_CONNECTED ? "Connected" : "Disconnected");
  html += "</span></p><p>WiFi AP: ";
  html += ssid;
  html += "</p><p>ESP32 IP: ";
  html += WiFi.localIP().toString();
  html += "</p><p>API target: ";
  html += apiUrl;
  html += "</p></div>";
  html += "<div class='card'><p>Reading Count: <span id='count' class='value'>0</span></p>";
  html += "<p>Temperature: <span id='temp' class='value'>0</span> C</p>";
  html += "<p>Humidity: <span id='hum' class='value'>0</span> %</p>";
  html += "<p>Soil Moisture: <span id='soil' class='value'>0</span> %</p></div>";
  html += "<div class='card'><p>Signal Strength: <span id='rssi'>0</span> dBm</p>";
  html += "<p>Last HTTP Status: <span id='http'>0</span></p>";
  html += "<p>Last Response: <span id='response'>No request sent yet</span></p>";
  html += "<p><a href='/send-now'>Send now</a> | <a href='/data'>JSON data</a></p></div>";
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
  json += "\"readingCount\":" + String(readingCount) + ",";
  json += "\"temperature\":" + String(testTemperature, 2) + ",";
  json += "\"humidity\":" + String(testHumidity, 2) + ",";
  json += "\"soilMoisture\":" + String(testSoilMoisture, 2) + ",";
  json += "\"lastHttpCode\":" + String(lastHttpCode) + ",";
  json += "\"lastResponse\":\"" + escapeJson(lastResponse) + "\",";
  json += "\"uptime\":" + String(millis() / 1000);
  json += "}";

  server.send(200, "application/json", json);
}

void handleSendNow() {
  updateTestData();
  sendReading();
  server.send(200, "text/plain", "Sent reading. HTTP status: " + String(lastHttpCode) + "\n" + lastResponse);
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
