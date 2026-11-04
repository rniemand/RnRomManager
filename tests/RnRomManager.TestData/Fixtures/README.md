# Test fixtures

Binary fixtures that can't be generated at test time (spec §15.2). No copyrighted data: everything here comes from
`SyntheticRomBuilder`.

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
