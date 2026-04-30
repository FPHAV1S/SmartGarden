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
   cd database
   chmod +x setup.sh
   ./setup.sh

2. Run Application:
   cd raspberry-pi/IrrigationSystem.Web
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
   CREATE DATABASE irrigation_db;
   ALTER USER postgres PASSWORD 'postgres';
   \q

3. Load Schema:
   sudo -u postgres psql -d irrigation_db -f database/schema.sql

4. Run Application:
   cd raspberry-pi/IrrigationSystem.Web
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
  Username: postgres
  Password: postgres

Web Application:
  URL: http://localhost:5000
  API: http://192.168.4.100/data

========================================
TROUBLESHOOTING
========================================

Error: "password authentication failed"
  → Run: sudo -u postgres psql -c "ALTER USER postgres PASSWORD 'postgres';"

Error: "Failed to connect to localhost:5432"
  → Check PostgreSQL: sudo systemctl status postgresql
  → Start: sudo systemctl start postgresql

Error: "Database irrigation_db does not exist"
  → Run setup.sh again