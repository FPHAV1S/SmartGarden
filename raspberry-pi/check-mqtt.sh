#!/bin/bash
set -u

MQTT_PORT=1883
AP_IP=192.168.4.1

section() {
    echo ""
    echo "== $1 =="
}

check_tcp() {
    local host="$1"

    if timeout 3 bash -c "</dev/tcp/$host/$MQTT_PORT" 2>/dev/null; then
        echo "OK: TCP connect to $host:$MQTT_PORT"
    else
        echo "FAIL: TCP connect to $host:$MQTT_PORT"
    fi
}

section "Mosquitto Service"
systemctl is-active mosquitto || true
systemctl status mosquitto --no-pager -l || true

section "Mosquitto Configs For Port 1883"
sudo grep -RInE '^[[:space:]]*(listener|port)[[:space:]]+1883([[:space:]]|$)|^[[:space:]]*allow_anonymous' /etc/mosquitto || true

section "TCP Listeners"
sudo ss -ltnp | grep ":$MQTT_PORT" || echo "No TCP listener on port $MQTT_PORT"

section "Pi Addresses"
ip -4 addr show || true

section "Local TCP Checks"
check_tcp 127.0.0.1
check_tcp "$AP_IP"

if command -v mosquitto_pub >/dev/null 2>&1; then
    section "Mosquitto Publish Checks"
    if timeout 5 mosquitto_pub -h 127.0.0.1 -p "$MQTT_PORT" -t gardenbrain/diagnostic -m local-test; then
        echo "OK: mosquitto_pub to 127.0.0.1"
    else
        echo "FAIL: mosquitto_pub to 127.0.0.1"
    fi

    if timeout 5 mosquitto_pub -h "$AP_IP" -p "$MQTT_PORT" -t gardenbrain/diagnostic -m ap-test; then
        echo "OK: mosquitto_pub to $AP_IP"
    else
        echo "FAIL: mosquitto_pub to $AP_IP"
    fi
fi

section "Firewall"
if command -v ufw >/dev/null 2>&1; then
    sudo ufw status verbose || true
else
    echo "ufw not installed"
fi

if command -v iptables >/dev/null 2>&1; then
    echo ""
    echo "iptables INPUT policy and MQTT/API rules:"
    sudo iptables -S INPUT | grep -E '^-P INPUT|--dport (1883|5000)' || true
else
    echo "iptables not installed"
fi

if command -v firewall-cmd >/dev/null 2>&1; then
    sudo firewall-cmd --state 2>/dev/null || true
    sudo firewall-cmd --list-ports 2>/dev/null || true
else
    echo "firewalld not installed"
fi

section "Recent Mosquitto Logs"
sudo journalctl -u mosquitto -n 80 --no-pager || true
