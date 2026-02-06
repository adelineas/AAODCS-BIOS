// UFC with 2 x CD74HC4067 & local (35 Pin's) - USB Joystick library.
// NOTE: This sketch file is for use with Arduino Leonardo and Arduino Micro only.
//
//--------------------------------------------------------------------
#define DCSBIOS_DEFAULT_SERIAL
#include <light_CD74HC4067.h>
#include <Joystick.h>

// s0 s1 s2 s3: select pins
CD74HC4067 mux(4, 5, 6, 7);  // create a new CD74HC4067 object

const int signal_pin = 2; // Pin Connected to Sig pin of CD74HC4067
const int signal2_pin = 3; // Pin Connected to Sig pin of CD74HC4067

Joystick_ Joystick = {Joystick_(0x03, JOYSTICK_TYPE_JOYSTICK,  35, 0,  false, false, false, false, false, false, false, false, false, false, false)};

#include "DcsBios.h"

DcsBios::Switch2Pos ufcMasterCaution("UFC_MASTER_CAUTION", 35); 
DcsBios::LED masterCaution(0x1012, 0x0800, 16); // LED am ProMicro

void setup() {
  pinMode(signal_pin, INPUT);  // Set as input for reading through signal pin for CD74HC4067 (1)
  pinMode(signal2_pin, INPUT); // Set as input for reading through signal pin for CD74HC4067 (2)
  // Initialize local Button Pins on ProMicro with INPUT_PULLUP without resistor in 5v
  pinMode(8, INPUT_PULLUP);
  pinMode(9, INPUT_PULLUP);
  pinMode(10, INPUT_PULLUP);

  pinMode(16, OUTPUT); // Masterwarn light

  // Initialize Joystick Library
  Joystick.begin();
}

// first pin from local arduino to map
const int pinToButtonMap = 8;

// Last state of the button
int lastButtonState[3] = {0, 0, 0};
int lastButtonStateM1[16] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0};
int lastButtonStateM2[16] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0};

void loop() {
  DcsBios::loop();
  read_mux_pins();
  read_local_pins();  
}

void read_local_pins() {
  for (int index = 0; index < 3; index++)
  {
    int currentButtonState = !digitalRead(index + pinToButtonMap);
    if (currentButtonState != lastButtonState[index])
    {
      Joystick.setButton(index+32, currentButtonState);
      lastButtonState[index] = currentButtonState;
    }
  }
  delay(5);
}

void read_mux_pins() {
   for (int i = 0; i < 16; i++)   {
      mux.channel(i); // Enter channel numbers from 0 - 15
      int currentButtonStateM1 = !digitalRead(signal_pin);
      if (currentButtonStateM1 != lastButtonStateM1[i])
        {
        Joystick.setButton(i, currentButtonStateM1);
        lastButtonStateM1[i] = currentButtonStateM1;
        }
      delay(5);    
   }

    for (int i = 0; i < 16; i++)  {
      mux.channel(i); // Enter channel numbers from 0 - 15
      int currentButtonStateM2 = !digitalRead(signal2_pin);
      if (currentButtonStateM2 != lastButtonStateM2[i])
        {
        Joystick.setButton(i+16, currentButtonStateM2);
        lastButtonStateM2[i] = currentButtonStateM2;
        }
      delay(5); 
    }
  }



