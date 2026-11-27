---
title: Amiga hard-disk and extra floppy formats
status: draft
created: 2026-11-27
---

# Amiga hard-disk and extra floppy formats

## Summary
Commodore Amiga (`amiga`, `AmigaHandler`, core `puae`) also ingests hard-disk images (`.hdf`, `.hdz`), WHDLoad installs (`.lha`) and the other floppy formats PUAE opens (`.dms`, `.fdi`, `.raw`). The libretro TOSEC DAT lists none of them, so they always go to Review and keep their extension there. Browser play also loads optional Kickstart ROMs from the BIOS folder. WHDLoad needs a real Kickstart, so hard-disk games need `kick40068.A1200`.

## Out of scope
- Multi-disk sets (**All disks**) for `.dms`/`.fdi`/`.raw`. `DiscSets.FloppyExtension("amiga")` stays `.adf`. A disk in another format never joins a set and never breaks one (the set key includes the file name's extension).
- Amiga CD32/CDTV (spec `amiga-cd32-cdtv`) and their Kickstart/extended ROMs.
- Encrypted Amiga Forever ROMs (`rom.key`), and Kickstarts other than `kick34005.A500` and `kick40068.A1200`.
- Storing Amiga releases outside the ZIP, and HDFs over 64 MiB.
- Any other homebrew type in `BrowserCoreExtensionsTests`' `NotIngested` (spec `homebrew-console-formats`).

## Behaviour

### Ingest and Review (A)
- **A1** `AmigaHandler.RomExtensions` becomes `[".adf", ".adz", ".ipf", ".hdf", ".hdz", ".lha", ".dms", ".fdi", ".raw"]` (`.adf` stays `[0]`). `ingest/amiga/` takes them like the others. `.hdz` (a zipped HDF) and `.lha` aren't in `ArchiveExtractor.Extensions`, so ingest stores them whole. A `.zip`/`.7z` holding them is unpacked as today.
- **A2** Hashing and DAT matching are unchanged. They run against the TOSEC `Commodore - Amiga` DAT. That DAT (read 2026-11-27) lists only `.adf` (40,160), `.ipf` (3,783), `.exe` (92) and a few documents, so a file of a new type finds no match and goes to Review, as an unmatched `.adf` does. `NormalizeForHashing` still inflates only a gzip stream (by magic). An `.hdz` starts with `PK` and is hashed as is.
- **A3** `AmigaHandler.KeepsExtension(extension)` returns true for every extension except `.adf` and `.adz` (ignoring case). Review stores a new type under its own extension, as it does an `.ipf`. An `.adf` or an inflated `.adz` is still stored as `.adf`.
- **A4** `MaxRomSizeBytes` goes from 4 MiB to 64 MiB (`64L * 1024 * 1024`). An embedded ROM is read into one array at ingest, so 64 MiB is the in-ZIP ceiling, the same as `Nintendo64Handler`. A larger file fails `TooLarge` with the existing message (`<n> bytes; amiga ROMs are at most 67108864`). This limit also caps an `.adz`'s inflated size (`GzipImage.Inflate(rom, MaxRomSizeBytes)`). `MinRomSizeBytes` stays 1 KiB.
- **A5** Play needs no API change. These releases sit inside the ZIP, so `/download` serves them, and `canPlay` / `LibraryDtos.ToListItemDto`'s `playRomId` treat them like any `.adf` (only `.ipf` is refused). EmulatorJS 4.2.3 unpacks an `.hdz` by its ZIP magic and hands PUAE the HDF inside. It passes an `.lha` through unchanged, and PUAE's WHDLoad support (`puae_use_whdload`, default `enabled`) boots it. No core option is set.

### Kickstart (K)
- **K1** For every `puae` launch (one disk, a hard-disk image or a disk set), `play.js` GETs `kick34005.A500` and then `kick40068.A1200`, one after another, through `loadOptionalBios`. This happens after the `biosFor` check and before the content request. Exact sizes: 262,144 bytes (`kick34005.A500`) and 524,288 bytes (`kick40068.A1200`).
  - A 404 skips a file.
  - A wrong size stops the launch with `amigaKickstartSizeError(name)`.
  - Any other failure stops it with `optionalBiosError`'s message.
- **K2** Each Kickstart found goes into `EJS_externalFiles` at `/<name>`, which is RetroArch's system directory, the content root. PUAE's automatic model picks the A500 (Kickstart 1.3) for floppies and the A1200 (Kickstart 3.1) for hard-disk and WHDLoad content. A missing Kickstart falls back to PUAE's built-in AROS, which is today's behaviour. With neither file present, a floppy plays exactly as today after two 404s.
- **K3** A hard-disk release, meaning a `name` ending in `.hdf`, `.hdz` or `.lha` (any case; `isAmigaHardDisk(name)`), needs `kick40068.A1200`, because WHDLoad doesn't run on AROS. Without it the launch stops before the ROM request with `amigaHardDiskKickstartMissing`. A disk set's `name` is an `.m3u`, so a set is never a hard-disk release.
- **K4** Exact text (in `params.js`):
  - `amigaKickstartSizeError(name)`: `The Amiga Kickstart <name> in the BIOS folder (Paths:Bios) must be <256 KiB|512 KiB>: ask an Admin to replace it.`
  - `amigaHardDiskKickstartMissing`: `Amiga hard-disk games need a Kickstart: ask an Admin to put kick40068.A1200 in the BIOS folder (Paths:Bios).`
- **K5** Admin → BIOS info gets a group after `Arcade (FinalBurn Neo)`: `new("Commodore Amiga", Need.Optional, [Exact("kick34005.A500", 256 * KiB), Exact("kick40068.A1200", 512 * KiB)], "Without them PUAE uses its built-in AROS Kickstart; hard-disk games (.hdf, .hdz, .lha) need kick40068.A1200.")`. The tab renders the catalog generically, so nothing changes in the SPA. Both names pass `EmulatorEndpoints.IsBiosName`.
- **K6** `contentFileName` gets no rename rule for `puae`. No stored Amiga release can end in `.A500` or `.A1200`.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Systems.Commodore/AmigaHandler.cs` | A1, A3 and A4. The class summary also names the new types, says they always go to Review (A2) and that hard-disk play needs `kick40068.A1200`. |
| Api | `src/RnRomManager.Api/Endpoints/BiosInfo.cs` | K5 catalog entry. |
| Web | `src/RnRomManager.Web/static/emulator/params.js` | Adds `amigaKickstarts` (`[{ name: 'kick34005.A500', bytes: 262144 }, { name: 'kick40068.A1200', bytes: 524288 }]`), `amigaHardDiskExtensions` (`['.hdf', '.hdz', '.lha']`), `isAmigaHardDisk(name)`, `amigaKickstartSizeError(name)` and `amigaHardDiskKickstartMissing` (K1, K3, K4). |
| Web | `src/RnRomManager.Web/static/emulator/play.js` | K1–K3. Adds `const isAmiga = params.core === 'puae'` beside the other `is*` flags, an `amigaFirmware` loop after the TurboGrafx-CD block, and `if (isAmiga && Object.keys(amigaFirmware).length > 0) ejs.EJS_externalFiles = amigaFirmware;` beside the other assignments. |
| Tests | `tests/RnRomManager.Api.Tests/BrowserCoreExtensionsTests.cs` | Removes the `(NoDat, ["puae"], [".hdf", ".hdz", ".lha", ".fdi", ".raw", ".dms"])` `NotIngested` entry, which is now stale. |
| Docs | `README.md` | The Commodore Amiga row's Formats cell follows A1's order (`SupportedSystemsTableTests`). |
| Docs | `docs/features.md` | In the ingest paragraph (Commodore Amiga), replaces `` `.dms`, `.hdf` and WHDLoad `.lha` aren't supported.`` with the A1–A4 behaviour. In the Play paragraph (Commodore Amiga), replaces "boots its built-in AROS Kickstart (no BIOS, fair compatibility)" with K1–K3. |
| Docs | `docs/zip-format.md` | The `amiga/` line lists the new stored extensions. |
| Docs | `docs/configuration.md` | `Paths:Bios` lists `kick34005.A500` (256 KiB) and `kick40068.A1200` (512 KiB) for Commodore Amiga, optional, with the latter needed for hard-disk games. |
| Docs | `docs/development.md` | **Browser emulators** gets an **Amiga Kickstart** bullet: K1–K3, and that `EJS_externalFiles` uses the content root as for PlayStation. |

No migration or config key changes. Existing data is untouched. Raising the limit only admits files that used to fail `TooLarge`.

## Risks
- **Memory:** a 64 MiB HDF is read into one array at ingest and held whole in the browser to play. That's within the N64 precedent. Larger HDFs stay out (A4).
- **Unverified boot:** no real HDF, WHDLoad `.lha` or Kickstart is committed, so tests cover requests and wiring only. That a WHDLoad `.lha` boots in the vendored 4.2.3 PUAE build is unverified. The features doc says so, as it does for 3DS.
- **Two extra requests:** every Amiga launch now sends two BIOS GETs, which are 404s on a deployment without Kickstarts.
- **Saves:** WHDLoad writes its saves under `/data/saves` (persisted per browser). **Save … to library** may report `empty` for a hard-disk game, which is the existing behaviour for a core with no SRAM.
- **Overlap:** spec `amiga-cd32-cdtv` also touches `puae` in `play.js`, `BiosInfo` and `NotIngested`. Whichever lands second merges by adding its own entries.

## Steps
- [ ] **1. Ingest the new Amiga types**: A1–A4. Files: `src/RnRomManager.Systems.Commodore/AmigaHandler.cs`, `tests/RnRomManager.Systems.Commodore.Tests/AmigaHandlerTests.cs`, `tests/RnRomManager.Api.Tests/BrowserCoreExtensionsTests.cs`, `README.md`, `docs/features.md` (ingest paragraph), `docs/zip-format.md`. Tests:
  - `AmigaHandlerTests.Amiga_Contract` asserts the new `RomExtensions` and `(1024L, 64L * 1024 * 1024)`.
  - `Review_KeepsOnlyAnIpfsExtension` is renamed `Review_KeepsEveryExtensionButAdfAndAdz`, with cases `.ipf`, `.IPF`, `.hdf`, `.HDZ`, `.lha`, `.dms`, `.fdi` and `.raw` true, and `.adf`, `.ADF` and `.adz` false.
  - `NormalizeForHashing_KeepsACorruptOrOversizedGzip`'s bomb becomes `Gzip(new byte[Handler.MaxRomSizeBytes + 1])`.
  - `NormalizeForHashing_KeepsAZippedHdz` (new) keeps a `PK\x03\x04…` buffer as is.
  - Run `BrowserCoreExtensionsTests` and `SupportedSystemsTableTests`.
  
  Done when:
  - those tests pass;
  - `NotIngested` has no `puae` NoDat entry;
  - the README row lists the nine formats.
- [ ] **2. Kickstart for browser play**: K1–K6. Files: `src/RnRomManager.Api/Endpoints/BiosInfo.cs`, `src/RnRomManager.Web/static/emulator/params.js`, `src/RnRomManager.Web/static/emulator/play.js`, `src/RnRomManager.Web/src/lib/emulatorParams.test.ts`, `src/RnRomManager.Web/src/lib/emulatorPlay.test.ts`, `tests/RnRomManager.Api.Tests/Integration/EmulatorEndpointsTests.BiosInfo.cs`, `docs/features.md` (Play paragraph), `docs/configuration.md`, `docs/development.md`. Tests:
  - `emulatorParams.test.ts`: `isAmigaHardDisk` (`x.hdf`, `X.LHA` and `x.hdz` true; `x.adf` and `x.m3u` false), both `amigaKickstartSizeError` texts, and `amigaHardDiskKickstartMissing`.
  - `emulatorPlay.test.ts` has a new `describe('play page Amiga Kickstart (Amiga K1–K3)')`:
    - an `.adf` with both 404 sends `[kickA500, kickA1200, romUrl]` and sets no `EJS_externalFiles`;
    - both found go to `EJS_externalFiles` at `/kick34005.A500` and `/kick40068.A1200`;
    - a 100-byte `kick34005.A500` stops with its size error and no ROM request;
    - a 500 stops with `Couldn't load the BIOS (HTTP 500).`;
    - an `.lha` without `kick40068.A1200` stops with `amigaHardDiskKickstartMissing` before the ROM;
    - an `.hdf` with it loads.
  - The two `play page Amiga disk sets (Amiga F7)` tests serve both Kickstart URLs as 404. The first expects `urls()` to be `[kickA500, kickA1200, setUrl]` instead of `[setUrl]`.
  - `EmulatorEndpointsTests.BiosInfo_reports_valid_wrong_size_missing_and_other_files` also writes a 262,144-byte `kick34005.A500` and asserts the `Commodore Amiga` group is Valid/Missing and `ok`.
  
  Done when:
  - `npm run lint`, `npm run check` and `npx vitest run src/lib/emulatorParams.test.ts src/lib/emulatorPlay.test.ts` pass;
  - `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~EmulatorEndpointsTests"` passes;
  - the three docs describe K1–K5.

No Playwright change: no e2e plays Amiga, and synthetic images don't boot.

## Decisions
- 2026-11-27 Ingest `.hdf`, `.hdz`, `.lha`, `.dms`, `.fdi` and `.raw` on Amiga, reversing the 2026-11-23 NoDat left-out → yes (user)
- 2026-11-27 Does the TOSEC Amiga DAT list any of them? → No (only `.adf`, `.ipf`, `.exe`, documents), so every one goes to Review (code)
- 2026-11-27 Review naming → keep the source extension for everything but `.adf`/`.adz` (code: `.ipf` precedent)
- 2026-11-27 Size limit → 64 MiB in the ZIP, since embedded ROMs are read into one array (N64 precedent); HDFs stay embedded (code)
- 2026-11-27 Unpack `.hdz`/`.lha` at ingest? → No: they aren't `ArchiveExtractor.Extensions`; EmulatorJS unpacks `.hdz` itself (code)
- 2026-11-27 WHDLoad core option → none set; `puae_use_whdload` defaults to `enabled` (code: libretro-uae `libretro-core.c`)
- 2026-11-27 Hard-disk play without a Kickstart → stop with `amigaHardDiskKickstartMissing`: WHDLoad needs a real Kickstart (code: libretro-uae README)
- 2026-11-27 Self-review against the code (no Plan reviewer: the drafting fork can't start agents): Switch's `.hdf` is no conflict, since ingest resolves extensions within the folder's family; the Amiga F7 play tests' `urls()` assertion and the disk-set key's extension were checked (code)

### Open questions
1. Kickstart support for hard-disk play?
   - (a) **Recommended, as drafted:** load optional `kick34005.A500`/`kick40068.A1200` for every Amiga launch and require `kick40068.A1200` for `.hdf`/`.hdz`/`.lha`.
   - (b) No Kickstart work: make `.hdf`/`.hdz`/`.lha` library-only (`canPlay` and `playRomId` refuse them, as for `.ipf`), and drop step 2.
   - (c) No Kickstart work, and let hard-disk games try AROS (likely a black screen).

## Kickoff prompt
```text
/spec implement 2026-11-27-amiga-disk-formats.md
```
Starts the spec, or resumes it at the first unticked step.
