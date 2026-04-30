#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DB_NAME="irrigation_db"
DB_USER="denis"
DB_PASSWORD="1203"
SQL_FILE="$SCRIPT_DIR/irrigation_db.sql"

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
echo ""
