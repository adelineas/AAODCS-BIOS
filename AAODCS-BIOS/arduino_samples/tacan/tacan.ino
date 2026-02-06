/*
 TACAN with 14 seg Display and I2C HT16K33
 This sketch sets the Nano as a Slave to the RS-485 Bus for the right console.
 */
// 14 seg connect to A04(SDA) & A05(SCL), 5v & Gnd 
//#define DCSBIOS_DEFAULT_SERIAL
//#define DCSBIOS_IRQ_SERIAL
#define DCSBIOS_RS485_SLAVE 2
#define TXENABLE_PIN 2  //The Arduino pin that is connected to the RE and DE pins on the RS-485 transceiver.
#include "DcsBios.h"

#include <Wire.h>
#include <Adafruit_GFX.h>
#include "Adafruit_LEDBackpack.h"
Adafruit_AlphaNum4 alpha4 = Adafruit_AlphaNum4();

DcsBios::ActionButton tacanXyToggle("TACAN_XY", "TOGGLE", 12);

const byte tacanModePins[5] = {11, 3, 4, 5, 6};
DcsBios::SwitchMultiPos tacanMode("TACAN_MODE", tacanModePins, 5);

DcsBios::Switch2Pos tacanTestBtn("TACAN_TEST_BTN", 10);
DcsBios::LED tacanTest(0x10da, 0x0400, 9);

DcsBios::RotaryEncoder tacan1("TACAN_1", "DEC", "INC", A2, A3);
DcsBios::RotaryEncoder tacan10("TACAN_10", "DEC", "INC", A0, A1);

DcsBios::PotentiometerEWMA<5, 128, 5> tacanVol("TACAN_VOL", A6);

void setup() {
  DcsBios::setup(); //nothing for us to do here
  
  alpha4.begin(0x70);  // pass in the address
  alpha4.setBrightness(7);  //Brightness 0-15
  alpha4.clear();
  alpha4.writeDisplay();
 }
 
void onTacanChannelChange(char* newValue) {
    alpha4.writeDigitAscii(0, newValue[0]);
    alpha4.writeDigitAscii(1, newValue[1]);
    alpha4.writeDigitAscii(2, newValue[2]); 
    alpha4.writeDigitAscii(3, newValue[3]);
    alpha4.writeDisplay();   
    delay(10);   
}  

DcsBios::StringBuffer<4> tacanChannelBuffer(0x1162, onTacanChannelChange);

void loop() {
  DcsBios::loop();
}


