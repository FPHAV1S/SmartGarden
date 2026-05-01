// GardenBrain ESP32-C3
// Soil moisture controls XY-MOS automatically
// If moisture > 30%, MOSFET turns ON
// If moisture <= 30%, MOSFET turns OFF

#define SOIL_PIN 0

#define MOSFET1_PIN 6
#define MOSFET2_PIN 7

// Choose which MOSFET channel to control
#define CONTROLLED_MOSFET_PIN MOSFET1_PIN

// Most XY-MOS modules use HIGH = ON, LOW = OFF
const bool MOSFET_ACTIVE_HIGH = true;

// Your real tested soil sensor calibration
const int dryValue = 3730;
const int wetValue = 200;

// Moisture threshold
const float moistureThreshold = 30.0;

int soilRaw = 0;
float soilMoisture = 0.0;

bool mosfetState = false;

void setup() {
  Serial.begin(115200);
  delay(2000);

  Serial.println();
  Serial.println("GardenBrain ESP32-C3 moisture controlled MOSFET");
  Serial.println("MOSFET ON when moisture is over 30%");
  Serial.println("MOSFET OFF when moisture is 30% or lower");

  analogReadResolution(12);

  pinMode(MOSFET1_PIN, OUTPUT);
  pinMode(MOSFET2_PIN, OUTPUT);

  setMosfet(MOSFET1_PIN, false);
  setMosfet(MOSFET2_PIN, false);
}

void loop() {
  readSoilSensor();

  if (soilMoisture > moistureThreshold) {
    setMosfet(CONTROLLED_MOSFET_PIN, true);
  } else {
    setMosfet(CONTROLLED_MOSFET_PIN, false);
  }

  printStatus();

  delay(1000);
}

void readSoilSensor() {
  soilRaw = analogRead(SOIL_PIN);

  soilMoisture = ((float)(dryValue - soilRaw) / (dryValue - wetValue)) * 100.0;

  if (soilMoisture < 0) soilMoisture = 0;
  if (soilMoisture > 100) soilMoisture = 100;
}

void setMosfet(int pin, bool state) {
  mosfetState = state;

  if (MOSFET_ACTIVE_HIGH) {
    digitalWrite(pin, state ? HIGH : LOW);
  } else {
    digitalWrite(pin, state ? LOW : HIGH);
  }
}

void printStatus() {
  Serial.print("Soil raw: ");
  Serial.print(soilRaw);

  Serial.print(" | Moisture: ");
  Serial.print(soilMoisture);
  Serial.print("%");

  Serial.print(" | Threshold: ");
  Serial.print(moistureThreshold);
  Serial.print("%");

  Serial.print(" | MOSFET: ");
  Serial.println(mosfetState ? "ON" : "OFF");
}