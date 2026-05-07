#!/usr/bin/env python3
import os
import requests
import time
import json
from datetime import datetime

ESP32_URL = os.getenv("ESP32_URL")

def collect_data():
    try:
        response = requests.get(ESP32_URL, timeout=5)
        data = response.json()
        data['timestamp'] = datetime.now().isoformat()
        
        print(f"[{datetime.now().strftime('%H:%M:%S')}] "
              f"Temp: {data['temperature']:.1f}C, "
              f"Humidity: {data['humidity']:.1f}%, "
              f"Pressure: {data['pressure']:.1f} hPa")
        
        with open('sensor_data.json', 'a') as f:
            f.write(json.dumps(data) + '\n')
        
        return data
    except Exception as e:
        print(f"Error: {e}")
        return None

if __name__ == "__main__":
    print("Garden Brain Data Collector")
    if not ESP32_URL:
        print("Set ESP32_URL to the IP printed in the ESP32 Serial Monitor, for example:")
        print("ESP32_URL=http://10.163.174.123/data python collector.py")
        raise SystemExit(1)

    print("Collecting from:", ESP32_URL)
    
    while True:
        collect_data()
        time.sleep(5)
