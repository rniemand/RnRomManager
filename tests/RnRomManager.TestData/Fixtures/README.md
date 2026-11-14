# Test fixtures

Binary fixtures that can't be generated at test time (spec §15.2). No copyrighted data: the archives hold output of
the synthetic builders (`SyntheticRomBuilder`, `SyntheticNdsRomBuilder`), and `nds-marker/` is an original homebrew
cartridge built from its own source.

## `basic.7z`

SharpCompress can read 7z but not write it, so this archive is committed.

- One entry: `Seven Zip Demo (World).gb` (32 KiB).
- Its bytes are `TestFixtures.Basic7zRom()`, which is `new SyntheticRomBuilder { Seed = 7007, Title = "SEVENZIP" }.Build()`.
  `ArchiveExtractorTests` checks that the extracted bytes equal the builder output, so fixture DAT hashes stay valid.
- Built with 7-Zip 25.01 (x64):

  ```
  7z a -t7z -mx=9 -mtm=off -mtc=off -mta=off basic.7z "Seven Zip Demo (World).gb"
  ```

  The `-mt*=off` switches leave the file times out, so a rebuild only differs if 7-Zip's encoder does.

To rebuild: write `TestFixtures.Basic7zRom()` to a file with that name, run the command above, and replace `basic.7z`.

## `nds.7z`

A real 7z with a Nintendo DS ROM and a companion file, for the DS archive-routing tests (`IngestArchiveTests`).

- Two entries: `Archive Tales (Europe).nds` (128 KiB) and `readme.txt` (56 bytes), which ingest skips.
- The ROM is `TestFixtures.Nds7zRom()`, which is
  `new SyntheticNdsRomBuilder { Seed = 7008, SizeBytes = 128 * 1024, Title = "ARCHIVE TALES", GameCode = "AATP", FillWithFF = true }.Build()`
  (a valid header CRC, then 0xFF, so the archive is 368 bytes). The companion is `TestFixtures.Nds7zCompanionBytes()`.
- Built with 7-Zip 26.06 (x64):

  ```
  7z a -t7z -mx=9 -mtm=off -mtc=off -mta=off nds.7z "Archive Tales (Europe).nds" readme.txt
  ```

  Two runs of that command give the same bytes (SHA-1 `1fbfb034258746e1272da49b3eaa0553eca621c2`).

To rebuild: write `TestFixtures.Nds7zRom()` and `TestFixtures.Nds7zCompanionBytes()` to files with those names, run the command
above, and replace `nds.7z`.

## `nds-marker/nds-marker.nds`

An original, runnable Nintendo DS cartridge for the DS browser-play E2E test (`mvp.spec.ts`). Its source, build
recipe and licence notices are in `nds-marker/`: `source/main.c`, `Makefile`, `build.sh`, `NOTICE.txt` and
`licenses/` (the linked devkitPro libraries: libnds, zlib; dswifi, MIT; maxmod, ISC-style; newlib).

- What it does: the top screen shows one solid colour and the lower screen another. The first stylus press turns the
  lower screen a third colour and writes the marker byte `0x5A` to address 0 of cartridge backup memory. At boot the
  lower screen shows the third colour at once if the marker is already there.
- Colours (RGB15 in the source, shown as about `v * 255 / 31` per channel; `NdsMarkerColours` in `mvp.spec.ts` copies
  them). Each differs by at least 64 in some channel from black, from white and from the other two:

  | Screen | RGB15 | RGB |
  |---|---|---|
  | Top | `RGB15(4, 12, 24)` | (33, 99, 197) |
  | Lower, before a press | `RGB15(24, 8, 4)` | (197, 66, 33) |
  | Lower, after a press or with the marker saved | `RGB15(8, 24, 8)` | (66, 197, 66) |

- Backup memory: a SPI EEPROM with 2-byte addresses. The ARM9 takes the slot-1 bus with `sysSetBusOwners(true, true)`
  and drives `REG_AUXSPICNT`/`REG_AUXSPIDATA` itself (`readMarker` and `writeMarker` in `main.c`): `READ` (0x03) at
  boot, then `WREN` (0x06), `WRITE` (0x02) and `RDSR` (0x05) polling on the first press. Each command holds
  chip-select (bit 6) and clears it for its last byte, as games do. libnds 1.8.2's `cardReadEeprom` and
  `cardWriteEeprom` keep the hold bit set and deselect by disabling the bus; DeSmuME then never sees a command end,
  never finishes save-type detection and drops the write. The game code `NMKE` is in no save-type database, so
  DeSmuME detects the type from the first read (`MC_TYPE_AUTODETECT`: 2 address bytes + 1 data byte = 3, so 2-byte
  addresses) and writes the `.dsv`.
- Header: title `NDS MARKER`, game code `NMKE`, maker code `00`, unit code 2 (ndstool's default), a valid header CRC
  (ndstool writes it), no banner. 81,920 bytes.
- `TestFixtures.NdsMarkerSha1` pins its SHA-1, `b33ad3b81088aed4e672764de256b8089d2d1a83`, and
  `NdsMarkerFixtureTests` checks it, the header and the `--nds-dat` entry E2E uses (staged as
  `Marker Test (World).nds`).
- Built with `devkitpro/devkitarm@sha256:c582c1e1e0c72649ea51fcc22ff5f8e5d7565d90fecbc862ce214b9e47f6087a` (the
  `20240511` image: devkitARM r64, GCC 14.1.0, libnds 1.8.2, default-arm7 0.7.4, ndstool 2.3.1). Newer images build
  on libnds 2 and calico, whose cartridges show two white screens in the DeSmuME core RnRomManager ships. To rebuild,
  run `sh tests/RnRomManager.TestData/Fixtures/nds-marker/build.sh` (Docker running; on Git Bash for Windows prefix
  `MSYS_NO_PATHCONV=1`). It builds in a copy under `/tmp` in the container and prints the SHA-1, which must equal the
  pinned one; two builds on 2026-11-14 gave the same bytes.
