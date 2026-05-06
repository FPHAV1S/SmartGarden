# SmartGarden Irrigation System (https://github.com/FPHAV1S/SmartGarden)

SmartGarden is a Raspberry Pi based irrigation controller with a Blazor Server
dashboard, PostgreSQL storage, optional MQTT integration, and an ESP32-C3 sketch
that can send sensor-style readings to the web API.

The current repository contains:

- `IrrigationSystem.Web`: ASP.NET Core/Blazor Server web app for dashboard,
  zones, history, settings, login, demo data, auto-watering, and API ingestion.
- `IrrigationSystem.Worker`: optional standalone MQTT-to-PostgreSQL worker.
- `raspberry-pi/setup.sh`: PostgreSQL setup, Mosquitto MQTT setup, and seed
  script.
- `raspberry-pi/irrigation_db.sql`: PostgreSQL schema dump.
- `esp32/esp32.ino`: ESP32-C3 soil sensor, HTTP sender, local status page,
  and MQTT valve-command subscriber.
- helper scripts for collecting ESP32 data and generating BCrypt hashes.

## Current Behavior

- The web app listens on `http://0.0.0.0:5000`.
- Sensor readings are stored in PostgreSQL.
- Readings can arrive by HTTP `POST /api/sensor-readings`.
- Optional AI analysis can review the latest reading plus historical context and
  log a final safe irrigation decision.
- The web app also includes an MQTT background subscriber for
  `garden/+/sensors`.
- Manual and automatic watering publish valve commands to MQTT topic
  `irrigation/zone/{zoneId}/valve`.
- Demo Mode can generate readings for active zones without ESP32 hardware.
- The ESP32 sketch reads the configured soil sensor pin, reads temperature,
  humidity, and pressure from a BME280, and drives two XY-MOS valve outputs.
- The ESP32 subscribes to MQTT valve commands from the web app.

## Hardware Assumptions

- Raspberry Pi 5 or another Linux host for the web app and database.
- ESP32-C3 or compatible ESP32 board for `esp32/esp32.ino`.
- Optional XY-MOS relay/valve hardware connected to the ESP32-C3.
- Optional real sensors. The checked-in ESP32 sketch currently reads soil
  moisture plus BME280 temperature, humidity, and pressure.

## Software Requirements

- .NET 8 SDK or runtime.
- PostgreSQL 17 recommended. The checked-in schema dump was produced by
  PostgreSQL 17.8 and may need editing before loading on older versions.
- Optional: Mosquitto or another MQTT broker on `localhost:1883`.
- Arduino IDE or Arduino CLI with ESP32 board support.

The Arduino sketch uses the built-in ESP32 libraries listed in
`esp32/libraries.txt`, plus `PubSubClient` for MQTT valve commands.

## Repository Layout

```text
SmartGarden/
|-- README.md
|-- esp32/
|   |-- esp32.ino
|   `-- libraries.txt
`-- raspberry-pi/
    |-- setup.sh
    |-- irrigation_db.sql
    |-- collector.py
    |-- hash_passwords.py
    `-- irrigation_project/
        |-- IrrigationSystem.sln
        |-- IrrigationSystem.Tests/
        |-- IrrigationSystem.Web/
        `-- IrrigationSystem.Worker/
```

## Quick Start

From the repository root:

```bash
cd raspberry-pi
chmod +x setup.sh
./setup.sh
```

Then start the web app:

```bash
cd irrigation_project/IrrigationSystem.Web
dotnet restore
dotnet run
```

Open:

```text
http://localhost:5000
```

From another device on the same network, use:

```text
http://<raspberry-pi-ip>:5000
```

Default web login:

```text
Username: postgres
Password: 12344321
```

Default database connection used by the app:

```text
Host: localhost
Port: 5432
Database: irrigation_db
Username: postgres
Password: 1203
```

The web login password and the PostgreSQL role password are different.

## Tests

The repository includes a lightweight test runner that does not require a live
database, MQTT broker, or ESP32. It checks application defaults, API validation,
password hashing, schema/config files, and the ESP32-to-web API contract.

Run it with:

```bash
cd raspberry-pi/irrigation_project/IrrigationSystem.Tests
dotnet run
```

Or from the solution folder:

```bash
cd raspberry-pi/irrigation_project
dotnet run --project IrrigationSystem.Tests
```

## AI Irrigation Decisions

The web backend can optionally call a local Ollama model or the OpenAI API when
sensor readings arrive or when the auto-watering service checks a zone. The AI
recommendation is never used directly as a valve command. It is passed through
the existing rule-based fallback and safety checks first.

The AI reads:

- latest soil moisture, temperature, humidity, pressure, zone, and timestamp;
- recent sensor history for the zone;
- recent watering events;
- current zone threshold and system settings;
- saved `pattern_memory` summaries from earlier AI decisions.

The final decision is based on:

```text
sensor reading + old rule logic + AI recommendation + safety checks + history logs
```

### Enable Local Ollama AI

On Windows, install Ollama and pull the small local model:

```powershell
ollama pull qwen3:1.7b
ollama run qwen3:1.7b
```

The web app calls Ollama at `http://localhost:11434/api/chat`. Configure
`raspberry-pi/irrigation_project/IrrigationSystem.Web/appsettings.json`:

```json
"AiIrrigation": {
  "EnableAiDecisionMaking": true,
  "Provider": "Ollama",
  "Model": "qwen3:1.7b",
  "OllamaBaseUrl": "http://localhost:11434",
  "MinimumMinutesBetweenAiCalls": 15,
  "EnableWateringCooldown": true,
  "MinimumMinutesBetweenWaterings": 120
}
```

If the settings row already exists in PostgreSQL, open the dashboard Settings
page, turn on `Enable AI Decision Making`, choose `Ollama`, set the model to
`qwen3:1.7b`, and click `Test AI Provider`.

### Enable OpenAI AI

When using `Provider: "OpenAI"`, the API key must come from the environment
variable `OPENAI_API_KEY`. Do not put the key in the ESP32 sketch, frontend
files, GitHub, `appsettings.json`, or any public file.

Linux/Raspberry Pi:

```bash
export OPENAI_API_KEY="<your-openai-api-key>"
```

Windows PowerShell:

```powershell
$env:OPENAI_API_KEY = "<your-openai-api-key>"
```

Then enable AI in
`raspberry-pi/irrigation_project/IrrigationSystem.Web/appsettings.json`:

```json
"AiIrrigation": {
  "EnableAiDecisionMaking": true,
  "Provider": "OpenAI",
  "Model": "gpt-4.1-nano",
  "ApiKeyEnvironmentVariable": "OPENAI_API_KEY",
  "MinimumMinutesBetweenAiCalls": 15,
  "EnableWateringCooldown": true,
  "MinimumMinutesBetweenWaterings": 120
}
```

`qwen3:1.7b` is configured as the local default. `gpt-4.1-nano` remains a
cheap/fast hosted option when the provider is OpenAI. You can change the
provider and model name in config or on the Settings page without changing code.

If AI is disabled, Ollama/OpenAI is unavailable, the API call fails, or the AI
returns invalid JSON, the system keeps working with the old rule-based logic.

### Open-Meteo Weather Integration

The dashboard can now fetch hourly rain forecasts from Open-Meteo without an
API key. The weather integration is enabled by default for Burgas, Bulgaria
(latitude `42.5048`, longitude `27.4626`).

When rain is likely in the configured lookahead window or expected rainfall
exceeds the configured threshold, automatic watering is blocked by safety
logic. The dashboard shows a weather card with rain probability, expected rain,
lookahead hours, advice text, and a temporary weather pause button.
AI irrigation prompts also receive the weather context so recommendations can
mention rain when it affects the decision.

A confirmed pause stores a watering block in PostgreSQL and prevents the auto-
watering service from opening valves until the block expires.

If the weather API is unavailable, the app continues using old rule-based logic
instead of failing.

### Safety Rules

AI decisions are accepted only when they parse as strict JSON with:

```json
{
  "shouldWater": true,
  "recommendedValveState": "ON",
  "recommendedDurationSeconds": 10,
  "confidence": 0.82,
  "reason": "Soil moisture is falling and has stayed below the learned safe range.",
  "learnedObservation": "This soil usually dries quickly after several low readings.",
  "suggestedMoistureThreshold": 30,
  "riskLevel": "LOW"
}
```

After parsing, safety still blocks or changes the decision when needed:

- moisture at or above 60% forces `OFF`;
- duration is clamped to 120 seconds;
- repeated watering is blocked by the cooldown when `EnableWateringCooldown` is on;
- low AI confidence uses the old rule-based decision;
- `riskLevel: "HIGH"` blocks automatic watering;
- missing/invalid AI output uses the old rule-based decision.

### Learning Behavior

The project does not train or fine-tune a model. Its practical learning comes
from stored context:

- `ai_irrigation_decision_logs` stores AI recommendations, final safe decisions,
  fallback use, confidence, reasons, and errors;
- `pattern_memory` stores short observations from confident AI decisions, such
  as drying patterns or suggested thresholds.

Future AI calls receive recent readings, watering events, and pattern memory as
context, so the recommendations can adapt to the garden's history without
claiming true model training.

## Getting Data Into the Dashboard

### Option 1: Demo Mode

Use this when you want to test the dashboard without hardware.

1. Start the web app.
2. Log in.
3. Go to `Settings`.
4. Enable `Demo Mode`.
5. Return to the dashboard. New readings are generated about every 5 seconds.

### Option 2: HTTP API

You can post a reading directly:

```bash
curl -X POST http://localhost:5000/api/sensor-readings \
  -H "Content-Type: application/json" \
  -d '{"device":"esp32-zone-1","zoneId":1,"temperature":24.5,"humidity":60,"pressure":1012.8,"soilMoisture":45}'
```

The API accepts either `soilMoisture` or `moisture`. `pressure` is optional
for older clients, but the ESP32 sketch now sends it from the BME280. If a
zone has Sensor ESP Name filled in, the `device` value must match that name.

### Option 3: ESP32-C3 Sketch

Upload `esp32/esp32.ino` to the ESP32.

The checked-in sketch expects:

```text
Wi-Fi SSID: GardenBrain
Wi-Fi password: GardenBrain123
ESP32 static IP: 192.168.137.100
Windows laptop/API IP: 192.168.137.1
API URL: http://192.168.137.1:5000/api/sensor-readings
```

Configure the Windows Mobile Hotspot separately so that it provides the
`GardenBrain` network on 2.4 GHz. Windows Internet Connection Sharing normally
uses `192.168.137.1` for the laptop side of the hotspot. If the web app or MQTT
broker runs on a different host, update `apiUrl` and `mqttServer` in the sketch.

After upload, the ESP32:

- posts a JSON reading every 5 seconds;
- receives a quick API acknowledgement after the reading is stored while
  AI/watering decisions continue in the web app background queue;
- exposes a status page at `http://192.168.137.100/`;
- exposes latest JSON data at `http://192.168.137.100/data`;
- can send a reading immediately from `http://192.168.137.100/send-now`;
- can open or close valves locally from
  `http://192.168.137.100/valve?valve=1&state=open&duration=10`;
- subscribes to MQTT topic `irrigation/zone/+/valve` so dashboard and
  auto-watering commands can drive the valve pins.

Each ESP32 should have a unique `espDeviceName` in `esp32/esp32.ino`, for
example `esp32-zone-1` or `esp32-greenhouse`. The Zones page has a matching
Sensor ESP Name field. When that field is filled in, `/api/sensor-readings`
only accepts readings for that zone from the matching ESP name.

When adding another ESP32, change these sketch values before flashing it:

- `espDeviceName`, which must match the zone's Sensor ESP Name;
- `localIp`, so two boards do not use the same static IP;
- `sensorZoneId`, which is the zone that board's soil sensor reports for;
- `zoneValveMap`, which maps dashboard zone IDs to this board's local valve
  numbers. MQTT valve commands for zones not listed in `zoneValveMap` are
  ignored by that ESP.

## API Endpoints

Main endpoints exposed by `IrrigationSystem.Web`:

```text
GET  /api/zones
GET  /api/latest
POST /api/sensor-readings
GET  /api/zone/{zoneId}/history?hours=24
POST /api/adaptive/run
GET  /api/ai/latest-decision
GET  /api/ai/history?count=20
```

Example sensor payload:

```json
{
  "device": "esp32-zone-1",
  "zoneId": 1,
  "readingCount": 42,
  "temperature": 24.5,
  "humidity": 60.0,
  "pressure": 1012.8,
  "soilMoisture": 45.0,
  "rssi": -55,
  "ip": "192.168.137.100"
}
```

## MQTT

MQTT is optional for viewing HTTP sensor data, but it is required for the
current manual and automatic valve command path.

Install Mosquitto on the Raspberry Pi if you want MQTT commands:

```bash
sudo apt-get update
sudo apt-get install -y mosquitto mosquitto-clients
sudo grep -RIn 'listener 1883\|port 1883' /etc/mosquitto
printf 'listener 1883 0.0.0.0\nallow_anonymous true\n' | sudo tee /etc/mosquitto/conf.d/gardenbrain.conf
sudo systemctl enable --now mosquitto
sudo systemctl restart mosquitto
```

If the `grep` command already shows another config file such as
`/etc/mosquitto/conf.d/irrigation.conf`, edit that file instead of adding
`gardenbrain.conf`. Mosquitto should only have one listener for TCP port
`1883`.

The web app publishes valve commands to:

```text
irrigation/zone/{zoneId}/valve
```

Payload examples:

```json
{"action":"open","duration":10}
{"action":"close"}
```

The checked-in ESP32 `zoneValveMap` maps `irrigation/zone/1/valve` to MOSFET 1
and `irrigation/zone/2/valve` to MOSFET 2. Open commands with a positive
`duration` auto-close the valve on the ESP32 even if the web app disconnects.
For additional ESP32 boards, change `zoneValveMap` so each board only reacts to
the zones wired to its local valves.

The web app hosted MQTT sensor subscriber listens to:

```text
garden/zone1/sensors
garden/zone2/sensors
garden/zone3/sensors
```

Expected payload:

```json
{"moisture":45,"temperature":24.5,"humidity":60,"pressure":1012.8}
```

The optional standalone worker project uses a different sensor topic pattern:

```text
irrigation/zone/{zoneId}/sensors
```

Run it only if you specifically want that separate MQTT ingestion process:

```bash
cd raspberry-pi/irrigation_project/IrrigationSystem.Worker
dotnet run
```

## Database

`raspberry-pi/setup.sh` does the following:

- installs PostgreSQL if `psql` is missing;
- installs Mosquitto and `mosquitto-clients` if they are missing;
- configures Mosquitto to listen on `0.0.0.0:1883` for ESP32 clients on the
  GardenBrain network;
- creates or updates the `postgres` PostgreSQL role with password `1203`;
- creates `irrigation_db` if needed;
- loads `raspberry-pi/irrigation_db.sql`;
- seeds default system settings;
- seeds the default web login for user `postgres`;
- confirms the PostgreSQL `postgres` password is `1203`;
- changes local PostgreSQL auth entries from `peer` to `trust`.

The SQL file was dumped from PostgreSQL 17.8. If your distribution installs an
older PostgreSQL release, either use PostgreSQL 17 or regenerate/trim the dump
for that server version before running the setup script.

The schema includes:

- `zones`
- `sensor_readings`
- `irrigation_events`
- `ai_irrigation_decision_logs`
- `pattern_memory`
- `system_settings`
- `system_logs`
- `users`
- `login_attempts`

One default zone is created by the application when Demo Mode starts or when
the first valid sensor reading is posted. Additional zones can be added from
the web app's Zones page.

For a manual schema load:

```bash
cd raspberry-pi
sudo -u postgres psql -d irrigation_db -f irrigation_db.sql
```

To reset the application login to the default password:

```bash
sudo -u postgres psql -d irrigation_db <<'SQL'
INSERT INTO users (username, password_hash)
VALUES ('postgres', '$2b$12$J3Z9neTGYCEkrtqQN3bCpuwQXdCgnUgIJMSYEdAFACfDdFvL2dqC6')
ON CONFLICT (username) DO UPDATE
SET password_hash = EXCLUDED.password_hash;
SQL
```

## Troubleshooting

### The app opens on port 5000, not 5093

`Program.cs` calls `UseUrls("http://0.0.0.0:5000")`, which overrides the
development profile URL in `launchSettings.json`.

### Dashboard says no zones or no data

Enable Demo Mode or post a sensor reading to `/api/sensor-readings`. The SQL
schema does not seed zones by itself.

### PostgreSQL password authentication failed

Run the setup script again:

```bash
cd raspberry-pi
./setup.sh
```

Or reset the role password manually:

```bash
sudo -u postgres psql -c "ALTER ROLE postgres WITH LOGIN PASSWORD '1203';"
```

### Cannot connect to PostgreSQL on localhost:5432

```bash
sudo systemctl status postgresql
sudo systemctl start postgresql
```

### MQTT connection errors appear in logs

Start Mosquitto if you need valve commands or MQTT sensor ingestion:

```bash
sudo systemctl start mosquitto
```

If you only use Demo Mode or HTTP sensor posts, MQTT errors do not prevent the
dashboard from storing and displaying readings.

If Mosquitto fails to start, check the real startup error:

```bash
cd raspberry-pi
./check-mqtt.sh
sudo systemctl status mosquitto
sudo journalctl -u mosquitto -n 80 --no-pager
sudo ss -ltnp | grep ':1883'
sudo grep -RIn 'listener 1883\|port 1883' /etc/mosquitto
```

A common cause is more than one Mosquitto config file trying to listen on
`1883`. Keep one listener for GardenBrain, for example:

```text
listener 1883 0.0.0.0
allow_anonymous true
```

If both `gardenbrain.conf` and `irrigation.conf` contain `listener 1883`,
remove the duplicate generated file and make the remaining listener external:

```bash
sudo rm /etc/mosquitto/conf.d/gardenbrain.conf
printf 'listener 1883 0.0.0.0\nallow_anonymous true\n' | sudo tee /etc/mosquitto/conf.d/irrigation.conf
sudo systemctl restart mosquitto
```

If Mosquitto is listening but the ESP32 still reports the MQTT port as
unreachable while HTTP `5000` works, check the firewall:

```bash
sudo iptables -S INPUT | grep -E '^-P INPUT|--dport (1883|5000)'
sudo iptables -C INPUT -p tcp --dport 1883 -j ACCEPT || sudo iptables -I INPUT -p tcp --dport 1883 -j ACCEPT
sudo netfilter-persistent save
```

### ESP32 cannot connect

Check that:

- the Windows hotspot SSID is exactly `GardenBrain`;
- the hotspot password is exactly `GardenBrain123`;
- the laptop hotspot IP is `192.168.137.1`;
- the web app is running on port `5000`;
- the ESP32 static IP `192.168.137.100` is not already in use.

If the serial monitor prints `HTTP status: -1`, the ESP32 HTTP client did not
receive a normal HTTP response. In the checked-in sketch, the `Response:` line
prints the underlying client error, such as `connection refused` or `read
Timeout`. Confirm that `http://192.168.137.1:5000/api/zones` opens from the
laptop and that Windows Firewall allows inbound TCP `5000` on the hotspot
network.

### ESP32-C3 serial monitor is blank

The sketch prints `GardenBrain ESP32-C3 device: <espDeviceName>` and
`Serial is alive at 115200 baud` about two seconds after reset. If the Arduino
Serial Monitor stays completely blank:

- set the monitor baud rate to `115200`;
- in Arduino IDE, select an ESP32-C3 board and set `USB CDC On Boot` to
  `Enabled`;
- upload again, then press the board `EN`/`RST` button with the Serial Monitor
  already open;
- select the ESP32-C3 USB/JTAG serial port, not an old Bluetooth or stale COM
  port;
- if `USB CDC On Boot` is disabled, `Serial` may go to UART0 pins instead of
  the USB cable, so the USB Serial Monitor can be blank even though the sketch
  is running.

### Request body timed out due to data arriving too slowly

This can happen when an ESP32 or another small client posts JSON slowly over an
unstable Wi-Fi link. The web app disables Kestrel's
`MinRequestBodyDataRate` limit so slow sensor posts are accepted instead of
failing before the API action runs.

## Security Notes

This project currently contains hard-coded development credentials and an open
ESP32 Wi-Fi configuration. Before using it on a real network, change the web
login, database password, PostgreSQL access rules, and Wi-Fi settings.

`setup.sh` also changes local PostgreSQL authentication to simplify testing on a
Raspberry Pi. Review `/etc/postgresql/*/main/pg_hba.conf` before treating the
device as production-ready.

## Helper Scripts

- `raspberry-pi/hash_passwords.py` prints BCrypt hashes for sample passwords.
  It does not update the database automatically.
- `raspberry-pi/collector.py` polls `http://192.168.137.100/data` and appends JSON
  lines to `sensor_data.json`. It may need small field-name updates if you use
  it with the current ESP32 sketch output.
