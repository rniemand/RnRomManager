# Builds nds-marker.nds. Run it inside the pinned devkitARM image with build.sh, not on the host.
# Original for RnRomManager (MIT); it uses devkitARM's ds_rules for the toolchain and libnds paths.

ifeq ($(strip $(DEVKITARM)),)
$(error "Run this in the devkitpro/devkitarm image: see build.sh")
endif

include $(DEVKITARM)/ds_rules

TARGET  := nds-marker
ARCH    := -march=armv5te -mtune=arm946e-s
CFLAGS  := -Wall -Werror -O2 -ffunction-sections -fdata-sections $(ARCH) -DARM9 -I$(LIBNDS)/include
LDFLAGS := -specs=ds_arm9.specs $(ARCH) -Wl,--gc-sections -Wl,-Map,build/$(TARGET).map
LIBS    := -L$(LIBNDS)/lib -lnds9

.PHONY: all clean
all: $(TARGET).nds

# No banner (-b): the cartridge carries no icon. -g sets the game code, maker code and 12-character title; ndstool
# writes a valid header CRC. The ARM7 binary is libnds' default (default.elf).
$(TARGET).nds: build/$(TARGET).elf
	ndstool -c $@ -9 $< -7 $(DEVKITPRO)/libnds/default.elf -g NMKE 00 "NDS MARKER"

build/$(TARGET).elf: build/main.o
	$(CC) $(LDFLAGS) $^ $(LIBS) -o $@

build/main.o: source/main.c
	@mkdir -p build
	$(CC) $(CFLAGS) -c $< -o $@

clean:
	rm -rf build $(TARGET).nds
