---
title: Homebrew console formats
status: ready
created: 2026-11-27
---

# Homebrew console formats

## Summary
Some existing systems' browser cores can open homebrew and alternative file types that ingest currently rejects as `UnsupportedType`. This spec makes those systems ingest them: NES UNIF, Game Boy `.sgb`, Master System `.bms`, Mega Drive `.68k`/`.sgd`/`.mdx`, Lynx `.o`, PlayStation `.exe`, PSP `.elf`/`.prx` and the VICE `.g41`/`.g4z` disk images. It reverses the 2026-11-23 "no DAT" left-outs in `BrowserCoreExtensionsTests.NotIngested`. No libretro DAT lists any of these types, so most files go to Review (`NoDatMatch`). The exceptions are `.sgb`, `.bms`, `.68k` and `.sgd`, which are plain ROMs under another name and can still match the DAT by hash.

## Out of scope
- Amiga types (`.hdf`, `.hdz`, `.lha`, `.dms`, `.fdi`, `.raw`): spec `amiga-disk-formats`.
- Nintendo DS `.bin` (D1 of `2026-11-23-core-file-type-parity` stands).
- Atari 7800 `.cdf`: dropped. It isn't a ROM (see S3).
- New systems such as SuperGrafx, Pico and SC-3000: other specs.
- Decoding `.mdx` at ingest so it can match the DAT (see S2).
- Checking that real homebrew boots in the browser. The tests cover file types and routing only.

## Behaviour

### NES (`nes`)
- **N1** `NesHandler.RomExtensions` becomes `[".nes", ".unf", ".unif"]`. `[0]` stays `.nes`. `NesHandler.KeepsExtension(extension)` returns true for `.unf` and `.unif`, ignoring case, and false otherwise, as `AmigaHandler.KeepsExtension` does for `.ipf`. Review therefore stores an unmatched UNIF file under its own extension. fceumm finds UNIF by its magic, but keeping the name means the stored file says what it is.
- **N2** `NesHeaderReader.Read` recognises UNIF. When `rom.Length >= 32` and `rom[..4]` is `UNIF` (ASCII), it returns `new RomHeader("nes", title, true, fields)`. `fields` holds exactly:
  - `headerValid` = true
  - `format` = `"UNIF"`
  - `board` = the `MAPR` chunk's string, or null
  - `romSizeBytes` = `rom.Length`

  `title` is the `NAME` chunk's string, or null. Chunks start at offset 32. Each chunk is a 4-byte ASCII id, a little-endian `uint32` length, then that many data bytes.
  - The walk stops when fewer than 8 bytes remain or a length runs past the end. Lengths are compared as `long`, so `0xFFFFFFFF` can't overflow.
  - When an id repeats, the first `NAME` or `MAPR` wins.
  - A chunk string is read up to its first NUL as UTF-8, then trimmed. An empty string is null.
  - `ReadHeader` gets at most the first 1 MiB (`RomFileHasher.HeaderBytes`). For a larger file, `romSizeBytes` is the bytes read (as for iNES today), and a `NAME`/`MAPR` chunk after a large `PRG0` is not found, so title and board are null. Accepted.

  Every other input keeps today's iNES behaviour exactly (same keys, same `IsPlausible`).
- **N3** No hashing change: a UNIF file is hashed whole. The No-Intro NES DAT lists no UNIF entries (checked 2026-11-27), so a UNIF file always goes to Review as `NoDatMatch`, with suggestions from the `NAME` title and the file name. Size limits are unchanged (4,112 bytes to 64 MiB + 528 bytes).

### Cartridge aliases
- **G1** `GameBoyHandler.RomExtensions` becomes `[".gb", ".dmg", ".sgb"]`. A `.sgb` is a Game Boy ROM with Super Game Boy enhancements. The cartridge header classifies it as usual. An unmatched loose CGB-flagged one is classified `gbc` and renamed to `.gbc` at ingest (`IngestFileMoves.RenameToClassifiedExtension`, because `gbc` doesn't list `.sgb`); an unmatched archive entry is classified `gbc` and stored as `.gbc` (`[0]`) through Review. A matched file takes the DAT's name (`.gb`). An unmatched one is stored as `.gb` (`[0]`) through Review. So a `.sgb` is never stored as `.sgb`, and gambatte (`gb|gbc|dmg`) never receives one.
- **S1** `MasterSystemHandler.RomExtensions` gains `.bms` at the end. A `.bms` is a TecToy (Brazil) Master System ROM: Genesis Plus GX's `loadrom.c` treats `.bms` exactly like `.sms`. It is stored as the DAT's `.sms`, or as `.sms` (`[0]`) through Review, so smsplus never receives a `.bms`.
- **S2** `MegaDriveHandler.RomExtensions` becomes `[".md", ".bin", ".gen", ".smd", ".68k", ".sgd", ".mdx"]`.
  - `.68k` and `.sgd` are Mega Drive ROMs under other names. Genesis Plus GX loads them as Mega Drive, with the same 512-byte-header detection. They follow `.gen`: matched → DAT name, unmatched → `.md`. `NormalizeForHashing` already detects SMD interleaving by content, so a copier `.sgd` is deinterleaved like a `.smd`.
  - `.mdx` is XOR-0x40 encoded behind a 4-byte prefix with 1 trailing byte, and Genesis Plus GX decodes it only when the file name ends in `.mdx`. So `MegaDriveHandler.KeepsExtension(extension)` returns true for `.mdx` (ignoring case) and false otherwise. Its bytes never match the DAT, so it always goes to Review and is stored as `.mdx`. `MegaDriveHeader.Read` finds no `SEGA` at `0x100` in the encoded bytes, which counts as plausible (as today), so it is never `InvalidHeader`.
- **S3** Atari 7800 `.cdf` is not ingested. In ProSystem a `.cdf` is a text cartridge description starting `ProSystem`. It names a separate binary file, and optionally BupChip sample files, to load from the content's folder. A release here is one file, so it can't work. Its `NotIngested` entry stays, with a new reason (T1).
- **L1** `AtariLynxHandler.RomExtensions` becomes `[".lnx", ".lyx", ".bll", ".o"]`. A `.o` is BLL homebrew, the same format as `.bll`: Handy's `system.cpp` detects `BS93` at offset 6 whatever the extension. `LynxHeader.Read` returns null for it, so an unmatched one goes to Review and is stored as `.lnx` (`[0]`), as a `.bll` is today.

### Executables on the disc systems (external storage)
- **P1** `PlayStationHandler.RomExtensions` gains `.exe` at the end. A PS-X EXE is a standalone release. External releases keep their original extension (`ReviewResolver` T9), and PCSX ReARMed picks its EXE loader by `.exe` in the path (`frontend/libretro.c`), which the play page keeps.
  - `PlayDisc.ShapeProblem`'s generic single-file rule also accepts a lone `.exe` (any case), but only when `systemId == PlayDisc.SystemId` (`psx`). The error text stays exactly `a single-file disc must be a .bin, .iso, .chd, .img, .pbp, .cbn or .nrg`, because a PlayStation `.exe` never reaches it.
  - `play-disc` streams the `.exe` unchanged as `application/octet-stream`, like a `.cbn`.
  - The PlayStation BIOS check runs as for any disc. A `.exe` is never part of a multi-disc set (`DiscSets.MainExtensions` is unchanged).
- **P2** `PlayStationPortableHandler.RomExtensions` becomes `[".iso", ".cso", ".pbp", ".chd", ".elf", ".prx"]`. PPSSPP's `Identify_File` boots an ELF-magic file named `.elf` or `.prx` as a PSP executable.
  - `PlayDisc.ShapeProblem`'s PSP rule accepts a single `.elf` or `.prx` too. Its message becomes `a PSP disc must be a single .iso, .cso, .pbp, .chd, .elf or .prx`.
  - `params.js` `discError`'s PSP `InvalidPlayDisc` text becomes `This UMD image can't be played in the browser: it must be one non-empty .iso, .cso, .pbp, .chd, .elf or .prx whose name matches the release.`
  - Nothing else on the PSP page changes.
- **P3** Both types go to Review as `NoDatMatch` (Redump lists neither). Neither handler reads a header, so neither is ever `InvalidHeader`. `ReviewResolver.IsDismissible` treats a lone `.exe`, `.elf` or `.prx` like a `.cbn` (dismissible, since it isn't a set member). The existing logic already gives this. Step 3 tests only the `psx` `.exe` case. PSP `.elf`/`.prx` aren't set-member extensions by construction, and `IsDismissible_CloneCdB7`'s registry has no PSP handler.

### VICE (`c64`, `pet`, `vic20`, `c128`)
- **V1** `.g41` is a 1541 GCR image with the same `GCR-1541` signature as a `.g64` (`C64Header.Read` already reads it as `format: "g64"`). `.g4z` is its gzipped form. Appended at the end of each list:
  - `C64Handler` gains `".g41", ".g4z"`.
  - `PetHandler` gains `".g41", ".g4z"`.
  - `Vic20Handler` gains `".g41", ".g4z"`.
  - `C128Handler` gains `".g41"` only. C128 takes no gzipped images (C128 spec scope).
  - Plus/4 gains neither, because it takes no `.g64` (Plus/4 spec scope).
- **V2** `ViceImages.Gzipped` gains `[".g4z"] = ".g41"`. A `.g4z` is inflated (as a `.g6z` is) and stored as `.g41`. `Game.g41.gz` already maps to `.g41` once `.g41` is in the list. Review keeps the extension (`KeepsSourceExtension` is true on all four handlers).

### Guard test and docs
- **T1** `BrowserCoreExtensionsTests`:
  - `NotIngested`:
    - Remove `fceumm` `.unf`/`.unif`, `handy` `.o`, `ppsspp` `.elf`/`.prx`, `pcsx_rearmed` `.exe`, and `genesis_plus_gx` `.mdx`/`.68k`/`.sgd`.
    - Remove `.g41`/`.g4z` for `vice_x64sc`, `vice_xpet` and `vice_xvic`, and `.g41` for `vice_x128`.
    - Move `mgba` `.sgb`, `genesis_plus_gx` `.bms` and `picodrive` `.68k`/`.sgd` to the `OtherSystem` group.
    - Move `vice_x128` `.g4z` to the `C128Scope` group, and `vice_xplus4` `.g41`/`.g4z` to the `Plus4Scope` group.
    - Give `prosystem` `.cdf` its own reason: `"a ProSystem cartridge description: a text file naming a separate ROM (and BupChip sample) file, not a ROM; a release is one file"`.
    - After this, the `NoDat` group holds only its `puae` entries (owned by `amiga-disk-formats`).
  - `Exceptions` gains:
    - `[("gb", ".sgb")] = "an SGB-enhanced Game Boy ROM: stored as the DAT's .gb or, through Review, as .gb ([0]); never stored as .sgb"`
    - `[("sms", ".bms")] = "a TecToy Brazilian Master System ROM: stored as the DAT's .sms or, through Review, as .sms ([0]); never stored as .bms"`
  - `ProblemsAreNamed`'s expected list gains `"gb .sgb: stale exception (not one of the system's extensions)"` and `"sms .bms: stale exception (no such system)"`.
- **T2** Every handler test that asserts `RomExtensions` with `Is.EqualTo` is updated to the new list, and each new `KeepsExtension` override gets a `[TestCase]` test.
- **D1** Docs (doc rule):
  - README systems table: the Formats column of `nes`, `gb`, `sms`, `md`, `lynx`, `psx`, `psp`, `c64`, `pet`, `vic20` and `c128` (`SupportedSystemsTableTests`).
  - `docs/features.md`: the ingest bullet that starts "Drop `.gb`/`.dmg`/`.gbc` files" (line 229). In the NES sentence, drop `UNIF` from what "aren't supported", and add that `.unf`/`.unif` are accepted, always go to Review and keep their extension. Add `.sgb` to the Game Boy types, `.bms` to Master System, `.68k`/`.sgd`/`.mdx` to Mega Drive (`.mdx` keeps its extension and never matches), `.o` to Lynx, `.exe` to PlayStation, `.elf`/`.prx` to PSP, and `.g41`/`.g4z` to the C64/PET/VIC-20 sentences and the gzipped-image list (`.g41` to C128). In the `InvalidHeader` sentence, "for NES it checks the `NES` + EOF magic" becomes "for NES it checks the `NES` + EOF magic, or `UNIF` for a UNIF file".
  - `docs/features.md`: the PlayStation Play bullet (line 160): after "or `.cbn` (PCSX ReARMed's compressed BIN, sent unchanged)" add "or a `.exe` (a PS-X EXE, sent unchanged)". The `play-disc` API bullet (line 459): "(PlayStation also `.img`, `.pbp`, `.cbn` and `.exe`; PSP: only a single `.iso`, `.cso`, `.pbp`, `.chd`, `.elf` or `.prx`; …)".
  - `docs/features.md`: the PSP Play bullet (line 161). Both type lists become `.iso`, `.cso`, `.pbp`, `.chd`, `.elf` or `.prx`.
  - `docs/features.md`: the disc "No match" bullet (line 273). Add `.exe`, `.elf` and `.prx` to the lone files that can be resolved.
  - `README.md` line 15: "PSP opens a `.iso`, `.cso`, `.pbp`, `.chd`, `.elf` or `.prx`".
  - `docs/zip-format.md`:
    - `nes/` line: "(headered .nes, header included; an unmatched UNIF .unf/.unif keeps its extension)".
    - `md/` line: "(.md, or .bin for the one lock-on entry, as the DAT names it; an unmatched .mdx keeps its extension)".
    - `psx/` line: `.bin/.iso/.chd/.img/.pbp/.cbn/.exe`. `psp/` line: `.iso, .cso, .pbp, .chd, .elf, .prx`.
    - `c64/`, `c128/` and `pet/` lines: `.g41` after `.g64`. `vic20/` line: `.g41` appended (it lists no `.g64`).
    - The Nintendo Entertainment System `header` paragraph (line 227) gains: "A UNIF file's `header` is `headerValid` (true), `format` (`UNIF`), `board` (the `MAPR` chunk, or null) and `romSizeBytes`; its title is the `NAME` chunk. Only the first 1 MiB is read."
    - `gb/`, `sms/` and `lynx/` are unchanged (those types are never stored under their own extension).
  - `docs/deployment.md` line 89:
    - `ingest/nes/`: "(NES: headered `.nes`, or UNIF `.unf`/`.unif`)".
    - `ingest/sms/`: "`.sms`, `.sg`, `.bin`, `.rom` or `.bms`".
    - `ingest/md/`: "`.md`, `.bin`, `.gen`, `.smd`, `.68k`, `.sgd` or `.mdx`, interleaved `.smd` deinterleaved".
    - `ingest/psx/`: "or `.bin`/`.iso`/`.chd`/`.img`/`.pbp`/`.exe`". `ingest/psp/`: "`.iso`, `.cso`, `.pbp`, `.chd`, `.elf` or `.prx`".
    - `ingest/c64/`: `.g41` after `.g64`, and `.g4z` after `.g6z`. `ingest/c128/`: `.g41` after `.g64`. `ingest/pet/` and `ingest/vic20/`: `.g4z` after `.g6z` (neither lists `.g64`).
    - `ingest/gb/` lists no types and there is no `ingest/lynx/` entry: unchanged.
  - `docs/development.md`: the PSP page bullet (line 133). The fetched types become `.iso`, `.cso`, `.pbp`, `.chd`, `.elf` or `.prx`.

No UI, API-shape, permission, setting, job, migration or backup change. Existing libraries are unaffected: only files dropped from now on can use the new types.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core (system) | `src/RnRomManager.Systems.Nes/NesHandler.cs`, `NesHeaderReader.cs` | N1, N2, and the doc comments that list types: `NesHandler`'s class summary ("Headered `.nes` dumps only") and `NesHeaderReader`'s summary (UNIF has a title) |
| Core (system) | `src/RnRomManager.Systems.GameBoy/GameBoyHandler.cs` | G1 |
| Core (system) | `src/RnRomManager.Systems.Sega/MasterSystemHandler.cs`, `MegaDriveHandler.cs` | S1, S2, and `MegaDriveHandler`'s class summary (`.68k`/`.sgd` stored as `.md`, `.mdx` kept) |
| Core (system) | `src/RnRomManager.Systems.Atari/AtariLynxHandler.cs` | L1 |
| Core (system) | `src/RnRomManager.Systems.Sony/PlayStationHandler.cs`, `PlayStationPortableHandler.cs` | P1, P2, and the doc comments that list types: `PlayStationHandler.RomExtensions`'s summary, and `PlayStationPortableHandler`'s class summary and `BrowserCore` summary |
| Core (system) | `src/RnRomManager.Systems.Commodore/C64Handler.cs`, `PetHandler.cs`, `Vic20Handler.cs`, `C128Handler.cs`, `ViceImages.cs` | V1, V2 (and the handlers' doc comments that list types) |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | P1, P2 `ShapeProblem` and its doc comment |
| Web | `src/RnRomManager.Web/static/emulator/params.js` | P2 message |
| Tests | `tests/RnRomManager.Api.Tests/BrowserCoreExtensionsTests.cs` | T1 |
| Tests | handler tests (per step), `NesHeaderReaderTests`, `LibraryEndpointsTests.PlayDisc.cs`, `ReviewResolverTests`, `src/RnRomManager.Web/src/lib/emulatorParams.test.ts`, `emulatorPlay.test.ts` | T2, P1–P3 |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/development.md` | D1 |

## Risks
- **Archives now keep more "unhandled" entries.** `IngestJob.Unhandled` counts any entry whose extension some system takes. So an archive with a loose `.exe`, `.o`, `.elf` or `.prx` it doesn't ingest (for example a `.7z` in `ingest/dos/`, which ingests only `.dosz`) is now kept as `Failed` instead of deleted. That's the safe direction (nothing is lost). Accepted.
- **`.md` and `.o` are common non-ROM names.** `.md` already has this problem (see features.md). A `.o` object file inside an archive in `ingest/lynx/` fails `TooSmall` (under 1 KiB) or goes to Review. Accepted, same as `.md`.
- **Merge conflicts with sibling specs:**
  - `sg1000-sc3000-system` changes `MasterSystemHandler.RomExtensions` (S1) and the README `sms` row.
  - `amiga-disk-formats` edits the same `NotIngested` block.
  - `sega-pico-support` and `sg1000-sc3000-system` edit the `picodrive` `OtherSystem` row (T1 adds `.68k`/`.sgd` to it).
  - `amiga-cd32-cdtv` edits `PlayDisc.ShapeProblem` and adds cases to `PlayDisc_AStandaloneChd_StreamsUnchanged` (P1, step 3).
  - Whichever lands second regenerates those lines from both specs' rules, and `docs/codemap.md` is regenerated, never merged by hand.
- **Boot not verified:** PSP `.elf`/`.prx` homebrew that loads assets from its own folder won't find them, because only the one file is sent. Some UNIF boards may be unsupported by fceumm. Both are in the library either way, and the Play button shows as for other types.

## Steps
- [ ] **1. NES UNIF**: N1–N3, the `fceumm` part of T1, D1 for `nes`. Files: `NesHandler.cs`, `NesHeaderReader.cs`, `BrowserCoreExtensionsTests.cs`, `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`. Tests: `NesHeaderReaderTests` (new cases: a synthetic UNIF file with `NAME` and `MAPR` chunks reads `format`, `board`, title and `IsPlausible` true; one without chunks has null title and board; a chunk length past the end, and a length of `0xFFFFFFFF`, stop the walk without throwing; a repeated `NAME` keeps the first; a 31-byte `UNIF` file falls back to the iNES path), `NesHandlerTests` (extension list; `KeepsExtension` `[TestCase]`s `.unf`, `.UNIF` true and `.nes` false), `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests`. Done when: those pass, and no iNES test changes.
- [ ] **2. Cartridge aliases**: G1, S1–S3, L1, their T1 parts (`Exceptions`, `ProblemsAreNamed`, the `OtherSystem` moves and the `.cdf` reason), D1 for `gb`/`sms`/`md`/`lynx`. Files: `GameBoyHandler.cs`, `MasterSystemHandler.cs`, `MegaDriveHandler.cs`, `AtariLynxHandler.cs`, `BrowserCoreExtensionsTests.cs`, README and the docs above. Tests: `GameBoyHandlerTests`, `MasterSystemHandlerTests`, `MegaDriveHandlerTests` (list; `KeepsExtension` `[TestCase]`s `.mdx`, `.MDX` true and `.68k`, `.md` false), `AtariLynxHandlerTests`, `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests`. Done when: those pass.
- [ ] **3. PlayStation and PSP executables**: P1–P3, their T1 parts, D1 for `psx`/`psp` (features.md lines 160, 161, 273 and 459, README line 15 and `development.md`). Files: `PlayStationHandler.cs`, `PlayStationPortableHandler.cs`, `PlayDisc.cs`, `params.js`, `BrowserCoreExtensionsTests.cs`, README and docs. Tests:
  - `PlayStationHandlerTests` and `PlayStationPortableHandlerTests` (lists).
  - `LibraryEndpointsTests.PlayDisc.cs`:
    - `[TestCase("psx", "Chd Solo (USA).exe")]` and `[TestCase("psx", "Chd Solo (USA).EXE")]` on `PlayDisc_AStandaloneChd_StreamsUnchanged`.
    - `[TestCase("Packed (USA).elf")]` and `[TestCase("Packed (USA).PRX")]` on `PlayDisc_APspCsoPbpOrChd_StreamsUnchanged`.
    - In `PlayDisc_APspReleaseThatIsNotOneIso_OrIsEmpty_OrMisnamed_Is409InvalidPlayDisc`, for the `bin` game, assert the problem `title` `Does.Contain("a PSP disc must be a single .iso, .cso, .pbp, .chd, .elf or .prx")`.
    - A new test next to `PlayDisc_ALoneMdf_NamesTheSingleFileShapes`: a lone `X (USA).exe` on `saturn` is `409 InvalidPlayDisc`, with a title containing the unchanged `a single-file disc must be a .bin, .iso, .chd, .img, .pbp, .cbn or .nrg`.
  - `ReviewResolverTests.IsDismissible_CloneCdB7`: `[TestCase("psx", "Game.exe", false, true)]`.
  - `src/RnRomManager.Web/src/lib/emulatorParams.test.ts` (:441) and `emulatorPlay.test.ts` (:690) with the new text.
  - `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests`.

  Done when: those pass, plus `npm run lint` and `npm run check`.
- [ ] **4. VICE `.g41`/`.g4z`**: V1, V2, their T1 parts, D1 for the four Commodore systems. Files: `C64Handler.cs`, `PetHandler.cs`, `Vic20Handler.cs`, `C128Handler.cs`, `ViceImages.cs`, `BrowserCoreExtensionsTests.cs`, README and docs. Tests: `C64HandlerTests`, `PetHandlerTests` and `Vic20HandlerTests` (lists; `[TestCase("Game.g4z", ".g41")]` and `[TestCase("Game.g41.gz", ".g41")]` on their `SourceExtension` tests), `C128HandlerTests` (list), `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests`. Done when: those pass, and `NotIngested` has no `NoDat` entry outside `puae`.

No Playwright change: no UI changes.

## Decisions
- 2026-11-27 Ingest the homebrew/no-DAT types the cores open (reversing the 2026-11-23 left-outs), as their own spec next to the Amiga one → yes (user)
- 2026-11-27 Does any libretro DAT list these types? → No. Checked the NES, 7800, GB, GBC, MD, SMS, Lynx, PSP, PSX, C64, PET and VIC-20 DATs (code)
- 2026-11-27 `.sgb` on which system? → `gb`. It's a GB ROM, and `gba`'s mGBA is the wrong system. It's stored as `.gb`, hence the `Exceptions` entry (code)
- 2026-11-27 `.bms` on `md` (Genesis Plus GX lists it)? → No, on `sms`. It's a TecToy Master System ROM, stored as `.sms` (code)
- 2026-11-27 `.mdx`: decode at ingest so it matches the DAT? → No. The format can't be detected from bytes, and `NormalizeForHashing` gets no file name. It keeps its extension and Genesis Plus GX decodes it (code)
- 2026-11-27 7800 `.cdf`? → Dropped. It's a text descriptor naming other files, not a ROM (code)
- 2026-11-27 PSX `.exe` and the generic single-file error text? → Accepted for `psx` only, text unchanged (code)
- 2026-11-27 Review round 1: added the PSP/disc doc lines (features.md 161 and 271, README 15) and the handler doc comments; tests for `.EXE`, a Saturn `.exe` being refused and the PSP title; IsDismissible tests psx only; UNIF reads only the first 1 MiB, the first chunk wins, lengths are compared as long; round 2: Step 3 names every doc line (reviewer)
- 2026-11-27 Deep review: features.md line numbers (229, 273, 459) and the NES `InvalidHeader` sentence; zip-format.md NES `header` paragraph and exact line edits; deployment.md has no `ingest/lynx/` entry and `ingest/gb/` lists no types; sibling-spec conflicts with `sega-pico-support`, `sg1000-sc3000-system` and `amiga-cd32-cdtv` (code)
- 2026-11-27 `.g41`/`.g4z` scope → C64/PET/VIC-20 both, C128 `.g41` only, Plus/4 none, following those systems' existing `.g64`/gzip scope (code)

## Kickoff prompt
```text
/spec implement 2026-11-27-homebrew-console-formats.md
```
Starts the spec, or resumes it at the first unticked step.
