//----------------------------------------------------------------------
// A-10C II Cockpit CDU
// STM32F401 https://github.com/stm32duino/BoardManagerFiles/raw/main/package_stmicroelectronics_index.json
//TFT_eSPI
//https://doc-tft-espi.readthedocs.io/latest/tft_espi/methods/constructor/
// STM32F401CCu6 
//STM32F401   4 Zoll SPI ST7796_DRIVER
//  5v        LED
//  5v        VCC
//  Gnd       Gnd
//  A2        Reset
//  A3        DC/RS
//  A4        CS
//  A5        SCK
//  A6         MISO
//  A7        SDI MOSI
//
// Freejoy input for STM32F103C8 https://github.com/FreeJoy-Team/FreeJoy
//#define DCSBIOS_IRQ_SERIAL
#define DCSBIOS_DEFAULT_SERIAL
#define DCSBIOS_DISABLE_SERVO

#include "DcsBios.h"

#include <TFT_eSPI.h> // Hardware-specific library
#include <SPI.h>

#include "Free_Fonts.h" // Include the header file attached to this sketch

TFT_eSPI tft = TFT_eSPI();       // Invoke custom library

void printChar(int row, int col, unsigned char c) {
  int16_t x = 13 + col * 19;
  int16_t y = row * 32 + 6;
  tft.drawChar(x, y, c, TFT_GREEN, TFT_BLACK, 3);
} 


void onCduLine0Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(0, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine0Buffer(0x11c0, onCduLine0Change);

void onCduLine1Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(1, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine1Buffer(0x11d8, onCduLine1Change);

void onCduLine2Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(2, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine2Buffer(0x11f0, onCduLine2Change);

void onCduLine3Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(3, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine3Buffer(0x1208, onCduLine3Change);

void onCduLine4Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(4, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine4Buffer(0x1220, onCduLine4Change);

void onCduLine5Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(5, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine5Buffer(0x1238, onCduLine5Change);

void onCduLine6Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(6, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine6Buffer(0x1250, onCduLine6Change);

void onCduLine7Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(7, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine7Buffer(0x1268, onCduLine7Change);

void onCduLine8Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(8, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine8Buffer(0x1280, onCduLine8Change);

void onCduLine9Change(char* newValue) {
  for(int i = 0; i < 24; i++){
    printChar(9, i, newValue[i]);
  }
}
DcsBios::StringBuffer<24> cduLine9Buffer(0x1298, onCduLine9Change);



void setup() {
  DcsBios::setup();

  tft.init();
  tft.setRotation(3);
  tft.fillScreen(TFT_BLACK);
  tft.setTextFont(GLCD);     // Select the orginal small GLCD font by using NULL or GLCD
  tft.setTextColor(TFT_GREEN,TFT_BLACK);
}

void loop() {
  DcsBios::loop();
}



