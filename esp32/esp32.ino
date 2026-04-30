#include <WiFi.h>
#include <WebServer.h>
#include <Wire.h>
#include <Adafruit_BME280.h>

#define SDA_PIN 21
#define SCL_PIN 22
#define VALVE1_PIN 25
#define VALVE2_PIN 26
#define VALVE3_PIN 27

Adafruit_BME280 bme;

const char* ssid = "GardenBrainOpen";
const char* password = "";

IPAddress local_IP(192, 168, 4, 100);
IPAddress gateway(192, 168, 4, 1);
IPAddress subnet(255, 255, 255, 0);
IPAddress dns(8, 8, 8, 8);

WebServer server(80);

float temperature = 0;
float humidity = 0;
float pressure = 0;
int readingCount = 0;
unsigned long lastUpdate = 0;

bool valve1State = false;
bool valve2State = false;
bool valve3State = false;

void setup() {
  Serial.begin(115200);
  delay(2000);
  
  pinMode(VALVE1_PIN, OUTPUT);
  pinMode(VALVE2_PIN, OUTPUT);
  pinMode(VALVE3_PIN, OUTPUT);
  
  digitalWrite(VALVE1_PIN, LOW);
  digitalWrite(VALVE2_PIN, LOW);
  digitalWrite(VALVE3_PIN, LOW);
  
  Wire.begin(SDA_PIN, SCL_PIN);
  
  if (!bme.begin(0x76)) {
    Serial.println("BME280 sensor not found!");
    while (1);
  }
  
  Serial.println("Garden Brain starting...");
  
  WiFi.disconnect(true);
  WiFi.mode(WIFI_STA);
  WiFi.config(local_IP, gateway, subnet, dns);
  WiFi.begin(ssid, password);
  
  Serial.print("Connecting");
  int attempts = 0;
  while (WiFi.status() != WL_CONNECTED && attempts < 60) {
    delay(500);
    Serial.print(".");
    attempts++;
  }
  
  if (WiFi.status() == WL_CONNECTED) {
    Serial.println("\nConnected!");
    Serial.print("IP Address: ");
    Serial.println(WiFi.localIP());
    
    server.on("/", handleRoot);
    server.on("/data", handleData);
    server.on("/status", handleStatus);
    server.on("/valve", handleValve);
    
    server.begin();
    Serial.println("HTTP server started");
  } else {
    Serial.println("\nConnection failed!");
  }
}

void loop() {
  if (WiFi.status() == WL_CONNECTED) {
    server.handleClient();
    
    if (millis() - lastUpdate > 5000) {
      lastUpdate = millis();
      updateSensorData();
      readingCount++;
      
      Serial.print("Reading #");
      Serial.print(readingCount);
      Serial.print(" - Temp: ");
      Serial.print(temperature);
      Serial.print("C, Humidity: ");
      Serial.print(humidity);
      Serial.print("%, Pressure: ");
      Serial.print(pressure);
      Serial.print(" hPa | Valves: ");
      Serial.print(valve1State ? "1:ON " : "1:OFF ");
      Serial.print(valve2State ? "2:ON " : "2:OFF ");
      Serial.println(valve3State ? "3:ON" : "3:OFF");
    }
  }
  delay(10);
}

void updateSensorData() {
  temperature = bme.readTemperature();
  humidity = bme.readHumidity();
  pressure = bme.readPressure() / 100.0;
}

void setValve(int valve, bool state) {
  int pin = 0;
  if (valve == 1) {
    pin = VALVE1_PIN;
    valve1State = state;
  } else if (valve == 2) {
    pin = VALVE2_PIN;
    valve2State = state;
  } else if (valve == 3) {
    pin = VALVE3_PIN;
    valve3State = state;
  }
  
  digitalWrite(pin, state ? HIGH : LOW);
  Serial.print("Valve ");
  Serial.print(valve);
  Serial.println(state ? " opened" : " closed");
}

void handleRoot() {
  String html = "<!DOCTYPE html><html><head>";
  html += "<meta charset='utf-8'>";
  html += "<meta name='viewport' content='width=device-width,initial-scale=1'>";
  html += "<title>Garden Brain</title>";
  html += "<style>";
  html += "body{font-family:Arial;max-width:800px;margin:50px auto;padding:20px;background:#1a1a1a;color:#fff;}";
  html += ".card{background:#2d2d2d;padding:25px;margin:15px 0;border-radius:10px;border-left:4px solid #4CAF50;}";
  html += "h1{color:#4CAF50;text-align:center;}";
  html += ".value{font-size:42px;font-weight:bold;color:#4CAF50;margin:10px 0;}";
  html += ".label{font-size:16px;color:#aaa;}";
  html += ".footer{text-align:center;color:#666;margin-top:30px;font-size:14px;}";
  html += ".valve-control{display:flex;justify-content:space-between;align-items:center;margin:10px 0;}";
  html += ".btn{padding:10px 20px;border:none;border-radius:5px;cursor:pointer;font-size:16px;font-weight:bold;}";
  html += ".btn-on{background:#4CAF50;color:#fff;}";
  html += ".btn-off{background:#f44336;color:#fff;}";
  html += ".valve-status{font-size:18px;margin-right:10px;}";
  html += "</style>";
  html += "<script>";
  html += "function updateData(){";
  html += "fetch('/data').then(r=>r.json()).then(d=>{";
  html += "document.getElementById('temp').innerText=d.temperature.toFixed(1);";
  html += "document.getElementById('hum').innerText=d.humidity.toFixed(1);";
  html += "document.getElementById('press').innerText=d.pressure.toFixed(1);";
  html += "document.getElementById('count').innerText=d.count;";
  html += "document.getElementById('v1').innerText=d.valve1?'ON':'OFF';";
  html += "document.getElementById('v2').innerText=d.valve2?'ON':'OFF';";
  html += "document.getElementById('v3').innerText=d.valve3?'ON':'OFF';";
  html += "});}";
  html += "function toggleValve(num){";
  html += "fetch('/valve?valve='+num+'&toggle=1').then(()=>updateData());}";
  html += "setInterval(updateData,3000);";
  html += "</script>";
  html += "</head><body>";
  html += "<h1>🌱 Garden Brain Monitor</h1>";
  html += "<div class='card'><div class='label'>Temperature</div><div class='value'><span id='temp'>";
  html += String(temperature, 1);
  html += "</span> °C</div></div>";
  html += "<div class='card'><div class='label'>Humidity</div><div class='value'><span id='hum'>";
  html += String(humidity, 1);
  html += "</span> %</div></div>";
  html += "<div class='card'><div class='label'>Pressure</div><div class='value'><span id='press'>";
  html += String(pressure, 1);
  html += "</span> hPa</div></div>";
  html += "<div class='card'><div class='label'>Irrigation Control</div>";
  html += "<div class='valve-control'><span class='valve-status'>Valve 1: <b id='v1'>";
  html += valve1State ? "ON" : "OFF";
  html += "</b></span><button class='btn ";
  html += valve1State ? "btn-off" : "btn-on";
  html += "' onclick='toggleValve(1)'>";
  html += valve1State ? "Turn OFF" : "Turn ON";
  html += "</button></div>";
  html += "<div class='valve-control'><span class='valve-status'>Valve 2: <b id='v2'>";
  html += valve2State ? "ON" : "OFF";
  html += "</b></span><button class='btn ";
  html += valve2State ? "btn-off" : "btn-on";
  html += "' onclick='toggleValve(2)'>";
  html += valve2State ? "Turn OFF" : "Turn ON";
  html += "</button></div>";
  html += "<div class='valve-control'><span class='valve-status'>Valve 3: <b id='v3'>";
  html += valve3State ? "ON" : "OFF";
  html += "</b></span><button class='btn ";
  html += valve3State ? "btn-off" : "btn-on";
  html += "' onclick='toggleValve(3)'>";
  html += valve3State ? "Turn OFF" : "Turn ON";
  html += "</button></div></div>";
  html += "<div class='footer'>Reading #<span id='count'>";
  html += String(readingCount);
  html += "</span> | ESP32 IP: ";
  html += WiFi.localIP().toString();
  html += "</div></body></html>";
  
  server.send(200, "text/html", html);
}

void handleData() {
  String json = "{";
  json += "\"temperature\":" + String(temperature, 2) + ",";
  json += "\"humidity\":" + String(humidity, 2) + ",";
  json += "\"pressure\":" + String(pressure, 2) + ",";
  json += "\"count\":" + String(readingCount) + ",";
  json += "\"valve1\":" + String(valve1State ? "true" : "false") + ",";
  json += "\"valve2\":" + String(valve2State ? "true" : "false") + ",";
  json += "\"valve3\":" + String(valve3State ? "true" : "false");
  json += "}";
  
  server.send(200, "application/json", json);
}

void handleValve() {
  if (server.hasArg("valve") && server.hasArg("toggle")) {
    int valveNum = server.arg("valve").toInt();
    
    if (valveNum >= 1 && valveNum <= 3) {
      bool currentState = false;
      if (valveNum == 1) currentState = valve1State;
      else if (valveNum == 2) currentState = valve2State;
      else if (valveNum == 3) currentState = valve3State;
      
      setValve(valveNum, !currentState);
      server.send(200, "text/plain", "OK");
    } else {
      server.send(400, "text/plain", "Invalid valve number");
    }
  } else if (server.hasArg("valve") && server.hasArg("state")) {
    int valveNum = server.arg("valve").toInt();
    bool state = server.arg("state") == "1" || server.arg("state") == "on";
    
    if (valveNum >= 1 && valveNum <= 3) {
      setValve(valveNum, state);
      server.send(200, "text/plain", "OK");
    } else {
      server.send(400, "text/plain", "Invalid valve number");
    }
  } else {
    server.send(400, "text/plain", "Missing parameters");
  }
}

void handleStatus() {
  String status = "Garden Brain System\n";
  status += "Status: Online\n";
  status += "WiFi SSID: " + String(WiFi.SSID()) + "\n";
  status += "Signal Strength: " + String(WiFi.RSSI()) + " dBm\n";
  status += "IP Address: " + WiFi.localIP().toString() + "\n";
  status += "Total Readings: " + String(readingCount) + "\n";
  status += "Uptime: " + String(millis() / 1000) + " seconds\n";
  status += "Valve 1: " + String(valve1State ? "OPEN" : "CLOSED") + "\n";
  status += "Valve 2: " + String(valve2State ? "OPEN" : "CLOSED") + "\n";
  status += "Valve 3: " + String(valve3State ? "OPEN" : "CLOSED") + "\n";
  
  server.send(200, "text/plain", status);
}