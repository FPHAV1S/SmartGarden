SmartGarden - Irrigation System
=================================

HARDWARE:
- ESP32 DevKit
- BME280 sensor (I2C)
- 3x relay modules
- Raspberry Pi 5

SOFTWARE REQUIREMENTS:
- PostgreSQL 12+
- .NET 6.0+
- Arduino IDE (for ESP32)

========================================
QUICK START (Raspberry Pi)
========================================

1. Setup Database:
   cd raspberry-pi
   chmod +x setup.sh
   ./setup.sh

2. Run Application:
   cd raspberry-pi/irrigation_project/IrrigationSystem.Web
   dotnet run

3. Open Browser:
   http://localhost:5000

Default credentials:
   Username: denis
   Password: 987654456789

========================================
MANUAL SETUP (if script fails)
========================================

1. Install PostgreSQL:
   sudo apt-get install postgresql

2. Create Database:
   sudo -u postgres psql
   CREATE ROLE denis LOGIN PASSWORD '1203';
   CREATE DATABASE irrigation_db;
   ALTER DATABASE irrigation_db OWNER TO denis;
   \q

3. Load Schema:
   sudo -u postgres psql -d irrigation_db -f raspberry-pi/irrigation_db.sql
   sudo -u postgres psql -d irrigation_db -c "INSERT INTO system_settings (auto_watering_enabled, system_mode, default_watering_duration, night_mode_enabled, night_mode_start_hour, night_mode_end_hour, eco_mode_enabled) SELECT true, 'auto', 10, false, 18, 8, false WHERE NOT EXISTS (SELECT 1 FROM system_settings);"
   sudo -u postgres psql -d irrigation_db -c "INSERT INTO users (username, password_hash) VALUES ('denis', '\$2b\$12\$J3Z9neTGYCEkrtqQN3bCpuwQXdCgnUgIJMSYEdAFACfDdFvL2dqC6') ON CONFLICT (username) DO UPDATE SET password_hash = EXCLUDED.password_hash;"

4. Run Application:
   cd raspberry-pi/irrigation_project/IrrigationSystem.Web
   dotnet run

========================================
ESP32 SETUP
========================================

1. Install Libraries in Arduino IDE:
   - Adafruit BME280
   - Adafruit Unified Sensor

2. Upload esp32/garden_brain.ino

3. Connect to "GardenBrainOpen" WiFi

4. ESP32 IP: 192.168.4.100

========================================
CONNECTION INFO
========================================

Database:
  Host: localhost
  Port: 5432
  Database: irrigation_db
  Username: denis
  Password: 1203

Web Application:
  URL: http://localhost:5000
  API: http://192.168.4.100/data

========================================
TROUBLESHOOTING
========================================

Error: "password authentication failed"
  → Run: sudo -u postgres psql -c "ALTER ROLE denis WITH LOGIN PASSWORD '1203';"

Error: "Failed to connect to localhost:5432"
  → Check PostgreSQL: sudo systemctl status postgresql
  → Start: sudo systemctl start postgresql

Error: "Database irrigation_db does not exist"
  → Run: cd raspberry-pi && ./setup.sh
