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
`licenses/` (the linked devkitPro libraries: libnds, zlib; calico, ZPL-2.1; newlib).

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

- Backup memory: a SPI EEPROM with 2-byte addresses (libnds address type 2, a 64 KiB EEPROM). The ARM9 takes the
  slot-1 bus with `ntrcardOpen()`, reads the marker with `cardReadEeprom(0, &marker, 1, 2)` and writes it with
  `cardWriteEeprom(0, &value, 1, 2)`. The game code `NMKE` is in no save-type database, so DeSmuME detects the type
  from these commands (`MC_TYPE_AUTODETECT`).
- Header: title `NDS MARKER`, game code `NMKE`, maker code `00`, unit code 2 (ndstool's default), a valid header CRC
  (ndstool writes it), no banner. 110,592 bytes.
- `TestFixtures.NdsMarkerSha1` pins its SHA-1, `46e5d09f6629957c04f40f758626d5e97d9ee956`, and
  `NdsMarkerFixtureTests` checks it, the header and the `--nds-dat` entry E2E uses (staged as
  `Marker Test (World).nds`).
- Built with `devkitpro/devkitarm@sha256:116afba8df8453961de2936ffab20dd441edf4d682856c1ec8b0e53d7ed0bbf5`
  (devkitARM r68, GCC 16.1.0, libnds 2.0.2, calico 1.2.0, newlib 4.6.0.20260123, ndstool 2.3.1). To rebuild, run
  `sh tests/RnRomManager.TestData/Fixtures/nds-marker/build.sh` (Docker running; on Git Bash for Windows prefix
  `MSYS_NO_PATHCONV=1`). It builds in a copy under `/tmp` in the container and prints the SHA-1, which must equal the
  pinned one; two builds on 2026-11-14 gave the same bytes.
