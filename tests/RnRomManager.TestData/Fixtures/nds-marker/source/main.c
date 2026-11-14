// nds-marker: an original Nintendo DS test cartridge for RnRomManager's browser-play E2E test.
// Copyright (C) 2026 RnRomManager contributors. MIT licence, like the rest of the repository.
//
// The top screen shows TOP_COLOUR and the lower screen LOWER_COLOUR. The first stylus press turns the lower screen
// TOUCHED_COLOUR and writes MARKER to cartridge backup memory (a SPI EEPROM with 2-byte addresses). At boot the
// lower screen shows TOUCHED_COLOUR straight away if MARKER is already there, which proves the save persisted.
// No background layer is enabled, so each screen shows only its backdrop colour (palette entry 0).
#include <nds.h>

// RGB15 colours: each 5-bit channel v is shown as about v * 255 / 31 in 8 bits. Keep these in step with
// Fixtures/README.md and NdsMarkerColours in tests/e2e/specs/mvp.spec.ts.
#define TOP_COLOUR RGB15(4, 12, 24)    // about (33, 99, 197)
#define LOWER_COLOUR RGB15(24, 8, 4)   // about (197, 66, 33)
#define TOUCHED_COLOUR RGB15(8, 24, 8) // about (66, 197, 66)

#define MARKER 0x5A

// SPI backup memory, driven directly: each command keeps chip-select held (AUXSPICNT bit 6) and clears the hold for
// its last byte, so the chip is deselected after that byte (GBATEK "DS Cartridge Backup"). libnds' cardReadEeprom
// keeps the hold set and deselects by disabling the bus, which DeSmuME doesn't treat as the end of a command.
#define SPI_ENABLE 0xA000 // bit 15: slot-1 enable, bit 13: SPI mode
#define SPI_HOLD 0x0040   // bit 6: keep chip-select after this byte
#define SPI_BUSY 0x0080   // bit 7: transfer in progress

#define EEPROM_READ 0x03
#define EEPROM_WRITE 0x02
#define EEPROM_WREN 0x06
#define EEPROM_RDSR 0x05

static u8 spiByte(u8 value, bool last) {
	REG_AUXSPICNT = SPI_ENABLE | (last ? 0 : SPI_HOLD);
	REG_AUXSPIDATA = value;
	while (REG_AUXSPICNT & SPI_BUSY)
		;
	return REG_AUXSPIDATA;
}

static void spiEnd(void) {
	REG_AUXSPICNT = 0;
}

// Reads the byte at address 0 (a 2-byte address).
static u8 readMarker(void) {
	spiByte(EEPROM_READ, false);
	spiByte(0, false);
	spiByte(0, false);
	u8 value = spiByte(0, true);
	spiEnd();
	return value;
}

// Writes MARKER to address 0, then waits for the write to finish.
static void writeMarker(void) {
	spiByte(EEPROM_WREN, true);
	spiEnd();
	spiByte(EEPROM_WRITE, false);
	spiByte(0, false);
	spiByte(0, false);
	spiByte(MARKER, true);
	spiEnd();
	u8 status;
	do {
		spiByte(EEPROM_RDSR, false);
		status = spiByte(0, true);
		spiEnd();
	} while (status & 1);
}

int main(void) {
	videoSetMode(MODE_0_2D);
	videoSetModeSub(MODE_0_2D);
	lcdMainOnTop();
	BG_PALETTE[0] = TOP_COLOUR;

	// The ARM9 needs the slot-1 bus to reach backup memory.
	sysSetBusOwners(true, true);
	bool touched = readMarker() == MARKER;
	BG_PALETTE_SUB[0] = touched ? TOUCHED_COLOUR : LOWER_COLOUR;

	while (1) {
		swiWaitForVBlank();
		scanKeys();
		if (!touched && (keysDown() & KEY_TOUCH)) {
			touched = true;
			BG_PALETTE_SUB[0] = TOUCHED_COLOUR;
			writeMarker();
		}
	}
	return 0;
}
