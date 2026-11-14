# Test fixtures

Binary fixtures that can't be generated at test time (spec §15.2). No copyrighted data: everything here comes from
the synthetic builders (`SyntheticRomBuilder`, `SyntheticNdsRomBuilder`).

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
