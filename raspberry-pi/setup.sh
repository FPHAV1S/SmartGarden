#!/bin/bash

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

# Create database
echo "Creating database..."
sudo -u postgres psql -c "CREATE DATABASE irrigation_db;" 2>/dev/null || echo "Database already exists"

# Load schema
echo "Loading schema..."
sudo -u postgres psql -d irrigation_db -f schema.sql

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
echo "  Database: irrigation_db"
echo "  Username: postgres"
echo "  Password: postgres"
echo ""