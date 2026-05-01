#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DB_NAME="irrigation_db"
DB_USER="denis"
DB_PASSWORD="1203"
SQL_FILE="$SCRIPT_DIR/irrigation_db.sql"
MQTT_CONF_DIR="/etc/mosquitto/conf.d"
MQTT_MAIN_CONF="/etc/mosquitto/mosquitto.conf"
MQTT_CONF_FILE="/etc/mosquitto/conf.d/gardenbrain.conf"
MQTT_BACKUP_DIR="/etc/mosquitto/gardenbrain-backups"

echo "==================================="
echo "Garden Brain - Database Setup"
echo "==================================="

# Check if PostgreSQL is installed
if ! command -v psql &> /dev/null; then
    echo "Installing PostgreSQL..."
    sudo apt-get update
    sudo apt-get install -y postgresql postgresql-contrib
    sudo systemctl start postgresql
    sudo systemctl enable postgresql
fi

# Check if Mosquitto is installed
if ! command -v mosquitto &> /dev/null || ! command -v mosquitto_pub &> /dev/null; then
    echo "Installing Mosquitto MQTT broker..."
    sudo apt-get update
    sudo apt-get install -y mosquitto mosquitto-clients
fi

# Allow ESP32 clients on the Raspberry Pi access point to connect to MQTT.
echo "Configuring Mosquitto MQTT broker..."
sudo install -d -m 755 "$MQTT_CONF_DIR"
sudo install -d -m 755 "$MQTT_BACKUP_DIR"

STALE_MQTT_BACKUPS="$(
    sudo find "$MQTT_CONF_DIR" -maxdepth 1 -type f \( -name '*.bak' -o -name '*.bak.*' -o -name '*~' \) -print 2>/dev/null || true
)"

if [ -n "$STALE_MQTT_BACKUPS" ]; then
    echo "Moving Mosquitto backup files out of $MQTT_CONF_DIR:"
    echo "$STALE_MQTT_BACKUPS"
    while IFS= read -r backup_file; do
        [ -n "$backup_file" ] || continue
        sudo mv "$backup_file" "$MQTT_BACKUP_DIR/"
    done <<EOF_BACKUPS
$STALE_MQTT_BACKUPS
EOF_BACKUPS
fi

OTHER_MQTT_LISTENER_FILES="$(
    sudo grep -RIlE '^[[:space:]]*(listener|port)[[:space:]]+1883([[:space:]]|$)' "$MQTT_CONF_DIR" 2>/dev/null \
        | grep -v -F "$MQTT_CONF_FILE" || true
)"

MQTT_ACTIVE_CONF_FILE="$MQTT_CONF_FILE"
if [ -n "$OTHER_MQTT_LISTENER_FILES" ]; then
    echo "Found existing Mosquitto config already using TCP port 1883:"
    echo "$OTHER_MQTT_LISTENER_FILES"
    sudo rm -f "$MQTT_CONF_FILE"

    MQTT_LISTENER_FILE_COUNT="$(printf '%s\n' "$OTHER_MQTT_LISTENER_FILES" | sed '/^[[:space:]]*$/d' | wc -l)"
    if [ "$MQTT_LISTENER_FILE_COUNT" -ne 1 ]; then
        echo "Multiple existing Mosquitto files declare TCP port 1883."
        echo "Please keep exactly one listener before rerunning setup."
        sudo grep -RInE '^[[:space:]]*(listener|port)[[:space:]]+1883([[:space:]]|$)' "$MQTT_CONF_DIR" || true
        exit 1
    fi

    MQTT_ACTIVE_CONF_FILE="$OTHER_MQTT_LISTENER_FILES"
    echo "Updating $MQTT_ACTIVE_CONF_FILE to expose MQTT on the Raspberry Pi access point."
fi

if [ -f "$MQTT_ACTIVE_CONF_FILE" ]; then
    sudo cp "$MQTT_ACTIVE_CONF_FILE" "$MQTT_BACKUP_DIR/$(basename "$MQTT_ACTIVE_CONF_FILE").bak.$(date +%Y%m%d%H%M%S)"
fi

sudo tee "$MQTT_ACTIVE_CONF_FILE" > /dev/null <<'EOF'
listener 1883 0.0.0.0
allow_anonymous true
EOF

echo "Validating Mosquitto MQTT broker startup..."
sudo systemctl stop mosquitto 2>/dev/null || true
set +e
sudo timeout 2s mosquitto -c "$MQTT_MAIN_CONF" -v >/tmp/gardenbrain-mosquitto-check.log 2>&1
MQTT_CHECK_STATUS=$?
set -e

if [ "$MQTT_CHECK_STATUS" -ne 0 ] && [ "$MQTT_CHECK_STATUS" -ne 124 ]; then
    echo "Mosquitto failed to start with $MQTT_MAIN_CONF."
    echo "Validation output:"
    cat /tmp/gardenbrain-mosquitto-check.log
    echo ""
    echo "Mosquitto config files declaring TCP port 1883:"
    sudo grep -RInE '^[[:space:]]*(listener|port)[[:space:]]+1883([[:space:]]|$)' "$MQTT_CONF_DIR" || true
    echo ""
    echo "Processes currently using TCP port 1883:"
    sudo ss -ltnp | grep ':1883' || true
    exit 1
fi

sudo systemctl enable mosquitto
if ! sudo systemctl restart mosquitto; then
    echo "Mosquitto failed to restart."
    echo "Recent Mosquitto service logs:"
    sudo journalctl -u mosquitto -n 80 --no-pager || true
    echo ""
    echo "Processes currently using TCP port 1883:"
    sudo ss -ltnp | grep ':1883' || true
    exit 1
fi

MQTT_LISTENERS="$(sudo ss -ltnp | grep ':1883' || true)"
if [ -z "$MQTT_LISTENERS" ]; then
    echo "Mosquitto restarted, but nothing is listening on TCP port 1883."
    sudo journalctl -u mosquitto -n 80 --no-pager || true
    exit 1
fi

echo "Mosquitto TCP listeners:"
echo "$MQTT_LISTENERS"

if ! echo "$MQTT_LISTENERS" | grep -Eq '[[:space:]](0\.0\.0\.0|192\.168\.4\.1|\*):1883[[:space:]]'; then
    echo "Mosquitto is listening on TCP port 1883, but not on the Raspberry Pi access point."
    echo "The ESP32 needs 0.0.0.0:1883 or 192.168.4.1:1883, not only 127.0.0.1:1883."
    sudo grep -RInE '^[[:space:]]*(listener|port)[[:space:]]+1883([[:space:]]|$)' "$MQTT_CONF_DIR" || true
    exit 1
fi

if command -v ufw >/dev/null 2>&1 && sudo ufw status | grep -q "Status: active"; then
    echo "Opening MQTT port 1883 in ufw..."
    sudo ufw allow 1883/tcp
fi

if command -v iptables >/dev/null 2>&1; then
    if ! sudo iptables -C INPUT -p tcp --dport 1883 -j ACCEPT 2>/dev/null; then
        echo "Opening MQTT port 1883 in iptables..."
        sudo iptables -I INPUT -p tcp --dport 1883 -j ACCEPT
    fi

    if command -v netfilter-persistent >/dev/null 2>&1; then
        echo "Saving iptables MQTT rule with netfilter-persistent..."
        sudo netfilter-persistent save
    elif [ -d /etc/iptables ] && command -v iptables-save >/dev/null 2>&1; then
        echo "Saving iptables MQTT rule to /etc/iptables/rules.v4..."
        sudo sh -c 'iptables-save > /etc/iptables/rules.v4'
    fi
fi

if command -v firewall-cmd >/dev/null 2>&1 && sudo firewall-cmd --state >/dev/null 2>&1; then
    echo "Opening MQTT port 1883 in firewalld..."
    sudo firewall-cmd --permanent --add-port=1883/tcp
    sudo firewall-cmd --reload
fi

# Create application role
echo "Creating/updating application database user..."
sudo -u postgres psql -v ON_ERROR_STOP=1 -c "DO \$\$ BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = '$DB_USER') THEN
        CREATE ROLE $DB_USER LOGIN PASSWORD '$DB_PASSWORD';
    ELSE
        ALTER ROLE $DB_USER WITH LOGIN PASSWORD '$DB_PASSWORD';
    END IF;
END \$\$;"

# Create database
echo "Creating database..."
if sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname = '$DB_NAME'" | grep -q 1; then
    echo "Database already exists"
else
    sudo -u postgres createdb -O "$DB_USER" "$DB_NAME"
fi

# Load schema
echo "Loading schema..."
if sudo -u postgres psql -d "$DB_NAME" -tAc "SELECT to_regclass('public.users') IS NOT NULL" | grep -q t; then
    echo "Schema already exists"
else
    sudo -u postgres psql -v ON_ERROR_STOP=1 -d "$DB_NAME" < "$SQL_FILE"
fi

# Seed required defaults
echo "Seeding default settings and login..."
sudo -u postgres psql -v ON_ERROR_STOP=1 -d "$DB_NAME" <<'SQL'
INSERT INTO system_settings (
    auto_watering_enabled,
    system_mode,
    default_watering_duration,
    night_mode_enabled,
    night_mode_start_hour,
    night_mode_end_hour,
    eco_mode_enabled
)
SELECT true, 'auto', 10, false, 18, 8, false
WHERE NOT EXISTS (SELECT 1 FROM system_settings);

INSERT INTO users (username, password_hash)
VALUES ('denis', '$2b$12$J3Z9neTGYCEkrtqQN3bCpuwQXdCgnUgIJMSYEdAFACfDdFvL2dqC6')
ON CONFLICT (username) DO UPDATE
SET password_hash = EXCLUDED.password_hash;
SQL

# Set postgres password (for easy testing)
echo "Setting postgres password..."
sudo -u postgres psql -c "ALTER USER postgres PASSWORD 'postgres';"

# Allow local connections
echo "Configuring PostgreSQL access..."
sudo sed -i 's/peer/trust/g' /etc/postgresql/*/main/pg_hba.conf
sudo sed -i 's/ident/md5/g' /etc/postgresql/*/main/pg_hba.conf
sudo systemctl restart postgresql

echo ""
echo "✅ Database setup complete!"
echo ""
echo "Connection details:"
echo "  Host: localhost"
echo "  Port: 5432"
echo "  Database: $DB_NAME"
echo "  Username: $DB_USER"
echo "  Password: $DB_PASSWORD"
echo "  MQTT broker: 0.0.0.0:1883"
echo ""
