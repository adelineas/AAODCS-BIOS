// https://github.com/wayoda/LedControl/tree/master
// https://forum.dcs.world/topic/254273-need-help-for-f-16-caution-panel-matrix-led-wired/
#include <LedControl.h>

//#define DCSBIOS_DEFAULT_SERIAL
//#define DCSBIOS_IRQ_SERIAL
#define DCSBIOS_RS485_SLAVE 6
#define TXENABLE_PIN 2  //The Arduino pin that is connected to the RE and DE pins on the RS-485 transceiver.
#include <DcsBios.h>

LedControl lc=LedControl(12,10,11,1);//DIN,CLK,LOAD/CS,# OF IC's

unsigned char cl_row_map[48] = {
  0, 2, 4, 6,
  0, 2, 4, 6,
  0, 2, 4, 6,
  0, 2, 4, 6,
  0, 2, 4, 6,  
  0, 2, 4, 6,
  1, 3, 5, 7,
  1, 3, 5, 7,
  1, 3, 5, 7,
  1, 3, 5, 7, 
  1, 3, 5, 7,
  1, 3, 5, 7,
  };
#define SEG_DP (1<<7)
#define SEG_A (1<<6)
#define SEG_B (1<<5)
#define SEG_C (1<<4)
#define SEG_D (1<<3)
#define SEG_E (1<<2)
#define SEG_F (1<<1)
#define SEG_G (1<<0)

unsigned char cl_mask_map[48]= {
  SEG_DP, SEG_DP, SEG_DP, SEG_DP,
  SEG_B, SEG_B, SEG_B, SEG_B,
  SEG_C, SEG_C, SEG_C, SEG_C,
  SEG_D, SEG_D, SEG_D, SEG_D,
  SEG_E, SEG_E, SEG_E, SEG_E,
  SEG_G, SEG_G, SEG_G, SEG_G,
  SEG_G, SEG_G, SEG_G, SEG_G,
  SEG_E, SEG_E, SEG_E, SEG_E,
  SEG_D, SEG_D, SEG_D, SEG_D,
  SEG_C, SEG_C, SEG_C, SEG_C,
  SEG_B, SEG_B, SEG_B, SEG_B,
  SEG_DP, SEG_DP, SEG_DP, SEG_DP,
  };
  

unsigned char max7219_rows[8];

void setup() {
  DcsBios::setup();
  memset(max7219_rows, 0xff, sizeof(max7219_rows)); // all led's on

  lc.shutdown(0,false); //turn on the display
  lc.setIntensity(0,2); //set the brightness
  lc.clearDisplay(0);   //clear rthe display and get ready for new data
}
  
void updateCautionLights(unsigned int address, unsigned int data) {
    unsigned char clp_row = (address - 0x10d4) * 2;
    unsigned char start_index = clp_row * 4;
    unsigned char column = 0;
    unsigned char i;

    bool is_on;
    for (i=0; i<16; i++) {
        is_on = data & 0x01;
        // set caution light state (clp_row, column, is_on)
        if (is_on) {
          max7219_rows[cl_row_map[start_index+i]] |= cl_mask_map[start_index+i];
        } else {
          max7219_rows[cl_row_map[start_index+i]] &= ~(cl_mask_map[start_index+i]);
        }
        data >>= 1;
        column++;
        if (column == 4) {
           clp_row++;
           column = 0;
        }
    }
}

void onClpData1Change(unsigned int newValue) {
    updateCautionLights(0x10d4, newValue);
}
DcsBios::IntegerBuffer clpData1(0x10d4, 0xffff, 0, onClpData1Change);

void onClpData2Change(unsigned int newValue) {
    updateCautionLights(0x10d6, newValue);
}
DcsBios::IntegerBuffer clpData2(0x10d6, 0xffff, 0, onClpData2Change);

void onClpData3Change(unsigned int newValue) {
    updateCautionLights(0x10d8, newValue);
}
DcsBios::IntegerBuffer clpData3(0x10d8, 0xffff, 0, onClpData3Change);

void loop() {
  DcsBios::loop();
  
  // update MAX7219
  unsigned char i;
  for (i=0; i<8; i++) {
    lc.setRow(0, i, max7219_rows[i]);
  }
}
