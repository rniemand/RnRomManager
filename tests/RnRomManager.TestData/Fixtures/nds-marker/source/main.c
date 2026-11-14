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
#define MARKER_ADDRESS 0
// libnds' EEPROM address type 2: a 64 KiB SPI EEPROM, addressed with 2 bytes.
#define EEPROM_ADDRESS_TYPE 2

int main(void) {
	videoSetMode(MODE_0_2D);
	videoSetModeSub(MODE_0_2D);
	lcdMainOnTop();
	BG_PALETTE[0] = TOP_COLOUR;

	// The ARM9 needs the slot-1 bus to reach backup memory; without it the cartridge still shows its colours.
	bool card = ntrcardOpen();
	u8 marker = 0xFF;
	if (card)
		cardReadEeprom(MARKER_ADDRESS, &marker, 1, EEPROM_ADDRESS_TYPE);
	bool touched = marker == MARKER;
	BG_PALETTE_SUB[0] = touched ? TOUCHED_COLOUR : LOWER_COLOUR;

	while (pmMainLoop()) {
		swiWaitForVBlank();
		scanKeys();
		if (!touched && (keysDown() & KEY_TOUCH)) {
			touched = true;
			BG_PALETTE_SUB[0] = TOUCHED_COLOUR;
			if (card) {
				u8 value = MARKER;
				cardWriteEeprom(MARKER_ADDRESS, &value, 1, EEPROM_ADDRESS_TYPE);
			}
		}
	}
	return 0;
}
