---
title: Commodore 128 support
status: ready
created: 2026-11-24
---

# Commodore 128 support

## Summary
Adds the `c128` system (Commodore 128) to `RnRomManager.Systems.Commodore`: disk, tape, program and cartridge images stored in the game ZIP, with no DAT (libretro has no Commodore 128 DAT, so every file goes to Review, as for MS-DOS), and played in the browser with the operator-supplied EmulatorJS `vice_x128` core from the existing `Emulator:ViceCorePath` folder. It reuses `C64Header` (the `.crt`/`.tap`/`.g64` container headers) and follows `docs/adding-a-system.md`.

## Out of scope
- Multi-disk sets, **All disks** and `.m3u` ingest: each disk image is its own release and plays alone, as on C64 (C64 has no set handling; its spec ruled it out). A playlist has nothing to hash, so `.m3u` is never ingested.
- C64's other types (`.nib`, `.bin`, `.x64`, `.d80`, `.d82`, `.tcrt`, `.d2m`, `.d4m`, `.nbz`) and gzipped images (`.d6z`, `.d7z`, `.d8z`, `.g6z`, `.x6z`, `.gz`).
- Reading VICE's `C128 CARTRIDGE` CRT signature: `C64Header` recognises only `C64 CARTRIDGE   `, so a native C128 `.crt` gets no header (still ingested, through Review).
- A DAT, metadat or libretro thumbnails (none exist upstream).
- Machine-mode options (C64 mode, 40/80 columns): VICE x128's defaults and EmulatorJS's own menu apply.
- Seeded test data and a new e2e flow (see Decisions).

## Behaviour
- **B1 Identity.** `C128Handler` (new, `src/RnRomManager.Systems.Commodore/C128Handler.cs`): `Id` `c128`, `DisplayName` `Commodore 128`, `ShortName` `C128`, `Family` `c128` (its own family, like `vic20`), `DatName` `Commodore - 128` (unique among handlers; with `DatSource.None` it names no file), `DatSource` `DatSource.None`, `MetadatKinds` `[]`, `EmbedRomsInZip` `true`, `KeepsSourceExtension` `true` (each extension is a distinct format, as on C64).
- **B2 Formats.** `RomExtensions` in this order: `.d64`, `.d71`, `.d81`, `.g64`, `.t64`, `.tap`, `.prg`, `.p00`, `.crt`. `[0]` is `.d64` (never used for a rename, since `KeepsSourceExtension` is true and there is no DAT). `SourceExtension` and `NormalizeForHashing` keep the interface defaults (no gzipped types, so neither `ViceImages` nor `GzipImage` is used). A `.zip`/`.7z` dropped into `ingest/c128/` is unpacked as for every other system that doesn't list it (`ArchiveExtractor.IsArchiveFor`); an `.m3u` or any other unlisted type is ignored by the scanner, as for C64.
- **B3 Sizes.** `MinRomSizeBytes` 256 (small `.prg` programs, as on VIC-20), `MaxRomSizeBytes` 8 MiB (`8 * 1024 * 1024`, as on C64; a `.d81` is 819,200 B, tapes run to a few MiB).
- **B4 Header.** `ReadHeader(rom) => C64Header.Read(rom, Id)`: a `.crt` (`C64 CARTRIDGE   `), `.tap` (`C64-TAPE-RAW`) or `.g64` (`GCR-1541`) gets the same `Fields` as on C64, classified as `c128`; every other file returns null. `C64Header` is unchanged.
- **B5 Ingest.** With no DAT, every new accepted file goes to Review as `NoDatMatch` with no suggestions; the Review page offers Attach and Manual only, Manual's title taken from the file name (`hasDat` false), exactly as for MS-DOS. A file whose SHA1 is already in the library or pending review (on any system, e.g. a `.d64` stored under `c64`) comes out as `Duplicate` first, as everywhere (`IngestPipeline.IdentifyAsync`). Refresh DATs downloads nothing for `c128` and ingestion's `DatsMissing` preflight skips it, so upgrading needs no DAT refresh.
- **B6 Box art.** `DefaultBoxArtPng` loads `boxart.c128.png` through `EmbeddedResources.Load`, from `Resources/boxart/c128.svg` (new, drawn for this project, MIT, in the style of `c64.svg`) rendered to a 512×512 `c128.png` (new) as the box-art README describes. The `.csproj` already embeds `Resources/boxart/*.png`.
- **B7 Browser play.** `BrowserCore` `vice_x128`. Play is offered for releases in the ZIP (not in `LibraryEndpoints.ExternalBrowserCores`). EmulatorJS 4.2.3's `c128` group holds only `vice_x128`. The core doesn't need threads (`requiresThreads` lists only `ppsspp` and `dosbox_pure`), so it plays in the modal like the other VICE cores, never a threaded payload. No BIOS: VICE embeds the C128's ROMs (`biosFor` unchanged). `controlSchemeFor` returns null for it (default scheme).
- **B8 Operator core.** `OperatorCoreManifest.Pinned` gains `ViceC128` = `"vice_x128"`, display name `VICE (C128)`, `o => o.ViceCorePath`, `EmulatorEndpoints.ViceCoreMismatch`, from `@emulatorjs/core-vice_x128@4.2.3` (https://registry.npmjs.org/@emulatorjs/core-vice_x128/-/core-vice_x128-4.2.3.tgz; payloads at the package root, the report from `reports/`), read 2026-11-24:

  | File | Bytes | SHA-256 | Content type |
  |---|---|---|---|
  | `vice_x128-wasm.data` | 1,595,414 | `dbac85e530b006c2d17e200c445de582c7ea272bacf333e4b4f8c1e391ed8506` | `application/octet-stream` |
  | `vice_x128-legacy-wasm.data` | 1,596,799 | `71383f4b81943b936c2c0b4258af231f6adcbf3139c2d0a17b8f20a239712c86` | `application/octet-stream` |
  | `vice_x128.json` | 146 | `bf4d43822a61a423e00e0d2bffd063ca139a77e78b66507aeb90343fcc221af3` | `application/json` |

  The generic `GET /api/emulator/cores/vice_x128/<file>` route then serves them from `Emulator:ViceCorePath` (no new option); a mismatch is `409 ViceCoreMismatch`. Any of the four VICE sets works without the others. The two `-thread` payloads aren't used.
- **B9 Play-page text.** `params.js` `operatorCores.vice_x128` = `{ system: 'Commodore 128 games', display: 'VICE (C128)', short: 'VICE', option: 'Emulator:ViceCorePath' }`, so a missing core reads *Commodore 128 games need the VICE (C128) core files: ask an Admin to set them up (Emulator:ViceCorePath).* and a mismatch uses the existing VICE wording.
- **B10 Core extensions.** The `valid_extensions` compiled into `vice_x128_libretro.wasm` (read from `vice_x128-wasm.data`, 2026-11-24) are identical to `vice_x64sc`'s: `d64|d71|d80|d81|d82|g64|g41|x64|t64|tap|prg|p00|crt|bin|zip|7z|gz|d6z|d7z|d8z|g6z|g4z|x6z|cmd|m3u|vfl|vsf|nib|nbz|d2m|d4m|tcrt`. Every B2 type is in it. The ones C128 doesn't ingest get `BrowserCoreExtensionsTests.NotIngested` reasons (step 1).
- **B11 UI.** System colour `c128`, in `app.css` after the VIC-20 block, in the sibling pattern: comment `/* Commodore 128 system colour: a burnt orange, same in both themes; white on it is AA (~6.2:1). */`, `:root { --bs-c128: #9c4a00; }`, `.text-bg-c128 { color: #fff !important; background-color: var(--bs-c128) !important; }`. `systemColours.ts` adds `'c128'` to the `SystemColour['name']` union and `c128: { name: 'c128', foreground: '#fff' }` to the map. No `icons.ts` entry (controller fallback, as for `c64`). The system filter sorts by `DisplayName`, so `Commodore 128` lists right before `Commodore 64`.
- **B12 IGDB.** `IgdbPlatforms.ById["c128"]` = 15 (IGDB's *Commodore C64/128/MAX*, shared with `c64`).
- **B13 Emulator links.** `systemEmulators.json` `c128`: VICE (`https://vice-emu.sourceforge.io/`, windows/macos/linux) and RetroArch (`https://www.retroarch.com/?page=platforms`, windows/macos/linux/android/ios, note `RetroArch core: VICE x128`), mirroring `c64`.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Systems.Commodore/C128Handler.cs` (new) | B1–B7. |
| Core | `src/RnRomManager.Systems.Commodore/Resources/boxart/c128.svg`, `c128.png` (new), `Resources/boxart/README.md` | B6; README lists `c128.svg` with the others. |
| Core | `src/RnRomManager.Core/Media/IgdbPlatforms.cs` | B12, with `// shared with c64` on the entry. |
| Api | `src/RnRomManager.Api/AppHost.cs` | `services.AddSystemHandler<C128Handler>();` after `Vic20Handler` (the project is already referenced; no `Dockerfile`/`.slnx` change). |
| Api | `src/RnRomManager.Api/Endpoints/OperatorCoreManifest.cs` | B8: `ViceC128` const and pinned entry after `ViceVic`. |
| Web | `static/emulator/params.js` | `vice_x128` in the core allowlist (after `vice_xvic`), `operatorCores` (B9), `hidesCoreChooser` (after `vice_xvic`); the `operatorCores` JSDoc lists "C128 VICE x128". |
| Web | `static/emulator/play.js` | The pinned-cores comment reads "VICE x64sc, xpet, xvic and x128". |
| Web | `scripts/vendor-emulatorjs.mjs` | `"vice_x128"` after `"vice_xvic"` in the explicit-core patch, the non-threaded guard and the no-CDN guard; the header comment adds "vice_x128 of the c128 group", and the threads comment (~line 62) reads "VICE (x64sc, xpet, xvic and x128)". |
| Tests | `tests/RnRomManager.TestData/IngestSeeder.cs` | `Directory.CreateDirectory(Path.Combine(ingest, "c128"));` after `vic20` (folder only, no ROM). |
| Web | `src/lib/systemColours.ts`, `src/app.css` | B11. |
| Web | `src/lib/systemEmulators.json` | B13. |
| Docs | `README.md`, `docs/adding-a-system.md`, `docs/configuration.md`, `docs/deployment.md`, `docs/development.md`, `docs/features.md`, `docs/zip-format.md` | Step 3. |

No migration (`system_id` is a `VARCHAR`), no new config key, no API contract change beyond `c128` appearing in `/api/systems` and `browserCore` `vice_x128`.

## Risks
- **Unverified boot:** that `vice_x128` boots a `.d64`/`.d71`/`.d81` in C128 mode and autostarts it through EmulatorJS 4.2.3 is read from the code (`emulator.js`'s `c128` group, the core's extension list), not run with a real dump. Finish includes one manual check if the maintainer has the core files and a dump; otherwise the PR says so.
- **No DAT:** every C128 file needs a manual Review step; that is the intended MS-DOS-style flow, not a regression.
- **C64 files in the C128 folder** ingest as C128 (own family, no cross-family check), which is right for C128-mode software and harmless for C64 software (the C128 boots it in C64 mode only if the user switches; documented in features).
- Licensing: the core embeds Commodore ROMs and is never bundled, vendored or committed, as for the other VICE cores.

## Steps
- [ ] **1. Handler, registration and operator core**: B1–B8, B10, B12, B13. Files: `C128Handler.cs`, `Resources/boxart/c128.svg`, `c128.png`, boxart `README.md`, `AppHost.cs`, `IgdbPlatforms.cs`, `OperatorCoreManifest.cs`, `systemEmulators.json`, `IngestSeeder.cs`, and the `README.md` supported-systems row `| Commodore 128 | `c128` | In ZIP | `.d64`, `.d71`, `.d81`, `.g64`, `.t64`, `.tap`, `.prg`, `.p00`, `.crt` | None | `vice_x128` |` before Commodore 64 (so `SupportedSystemsTableTests` passes in this step). Tests:
  - `LibraryEndpointsTests.Systems_ListsEveryRegisteredSystem`: `"c128"` before `"c64"` in the id list (sorted by `DisplayName`), `Has.Exactly(28).True` for `embedRoms`, and a `("Commodore 128", "C128")` displayName/shortName assertion next to the `c64` one.
  - `SupportedSystemsTableTests` passes with the README row.
  - `tests/RnRomManager.Systems.Commodore.Tests/C128HandlerTests.cs` (new, after `Vic20HandlerTests`): identity and B1 flags; exact `RomExtensions`; sizes; `DatSource.None` and empty `MetadatKinds`; a synthetic `C64 CARTRIDGE   ` CRT and a `C64-TAPE-RAW` TAP give a header classified `c128` with C64's fields; a `.prg`/`.d64` byte array gives null; `BrowserCore` `vice_x128`; box art is a valid 512×512 PNG.
  - `SystemStartupTests` and `IngestEndpointsTests` (the `/api/ingest` systems list): insert `"c128"` after `"vic20"` in both id arrays (registration order).
  - `IgdbPlatformsTests.PlatformIds_AreDistinctAndPositive`: assert `ById["c128"] == ById["c64"]` and exclude `c128` from the uniqueness check, as `fbneo` is.
  - `BrowserCoreExtensionsTests`: `CoreExtensions["vice_x128"]` (B10 string) and its payload SHA-256 `dbac85e5…8506` in the pinned-payload map; add `"vice_x128"` to the `Vice` array (so the existing `Archive`, `Playlist`, `NotAGame` and `NoDat` rows cover it); a new reason constant `C128Scope` = `"left out of Commodore 128 by its spec (Out of scope); ingested on C64"` and `NotIngested` row `(C128Scope, ["vice_x128"], [".bin", ".x64", ".d80", ".d82", ".nib", ".nbz", ".d2m", ".d4m", ".tcrt", ".gz", ".d6z", ".d7z", ".d8z", ".g6z", ".x6z"])` (adjust only if the test reports a type the other rows already cover).
  - `EmulatorEndpointsTests.OperatorCores`: the pinned-ids array gains `"vice_x128"` after `"vice_xvic"`; a `vice_x128` assertion `("VICE (C128)", "ViceCoreMismatch", "/cores/vice")` with "shares the C64 folder"; a new `PinnedManifest_HoldsExactlyTheViceC1284_2_3Files` after `PinnedManifest_HoldsExactlyTheViceVic4_2_3Files`, asserting the three B8 files.
  - `LibraryEndpointsTests`: `[TestCase("c128", false, true, "vice_x128")]` in `Games_QuickPlay_IsThePrimaryReleaseWhenPlayable` and `[TestCase("c128", "vice_x128")]` in the ~line 749 case list, both after `vic20`.
  - `ReviewEndpointsTests`: a new case beside `Detail_HasDat_IsFalseForDos_AndTrueForGb` (~line 1403): a `.prg` dropped into `ingest/c128/` goes to Review with `(IngestOutcome.Review, "NoDatMatch", "c128")`, its detail has `hasDat` false, and a Manual resolution stores it inside the game ZIP keeping `.prg`.
  - `SystemEmulatorsCatalogTests` passes with B13.
  Done when: those classes plus `C64HandlerTests`, `C64HeaderTests` and `Vic20HandlerTests` pass; `dotnet build` is warning-free.
- [ ] **2. Web**: B7, B9, B11. Files: `params.js`, `play.js`, `vendor-emulatorjs.mjs`, `systemColours.ts`, `app.css`. Tests:
  - `src/lib/emulatorParams.test.ts`: the operator-core list (~line 165) gains `'vice_x128'`; a new `describe('Commodore 128')` mirroring the VIC-20 block: `isOperatorCore`, `hidesCoreChooser`, `controlSchemeFor('vice_x128', 'X.d64')` null, `operatorCorePaths('vice_x128')`, and the B9 404 text.
  - `src/lib/vendorEmulatorjs.test.ts`: the three guarded-edit strings (~lines 125, 134, 141) and the two `includes(this.getCore())` expectations (~698, 702) gain `"vice_x128"` after `"vice_xvic"`; new: `resolve('vice_x128', saved)` stays `vice_x128` for saved `vice_x128`/`opera`/null and `resolve('vice_x128', null, true)` is `c128`; `downloadCore(source, 'vice_x128')` requests `cores/reports/vice_x128.json` then `cores/vice_x128-legacy-wasm.data`, never a `-thread` payload, and a `-1` payload gives `Error downloading core (vice_x128-legacy-wasm.data)`.
  - `src/lib/systemColours.test.ts`: `systemColour('c128')` is `{ name: 'c128', foreground: '#fff' }`.
  Done when: those files, `npm run lint`, `npm run check` pass and `npm run predev` vendors without error.
- [ ] **3. Docs**: `README.md` Commodore 128 in the Play list (with `vice_x128` among the VICE cores), the upgrade sentence (no DAT refresh needed for it, as for MS-DOS) and the core attribution (not included); `docs/configuration.md` § VICE core: a C128 paragraph and B8 table after VIC-20's; `docs/deployment.md` § VICE core (the `vice_x128` package line) and an "Upgrading to the release that adds Commodore 128" note (no DAT refresh; add the `vice_x128` files for play); `docs/features.md` intro system list, the no-DAT sentence ("MS-DOS and Commodore 128 have no DAT"), and a Commodore 128 play bullet after VIC-20's (each disk/tape is its own release; no **All disks**; native C128 `.crt` has no header; files in `ingest/c128/` are stored as C128 unless already in the library, which makes them `Duplicate`); `docs/development.md` Commodore project row and operator-cores list; `docs/zip-format.md` folder list (`c128/  Commodore 128 (.d64, .d71, .d81, .g64, .t64, .tap, .prg, .p00, .crt)`); `docs/adding-a-system.md` (`BrowserCore` list `c128` → `vice_x128`, operator-cores VICE list, box-art list, `DatSource.None` now names MS-DOS and Commodore 128, `C64Handler` header sentence adds that `C128Handler` reuses it). Tests: `SupportedSystemsTableTests`. Done when: it passes and `pwsh scripts/codemap.ps1` is committed.

## Decisions
- 2026-11-24 Multi-disk sets and `.m3u`? → no sets, no `.m3u` ingest; each disk is its own release, as on C64 (C64 has no set handling to reuse) (user)
- 2026-11-24 Which formats? → exactly the requested nine; other `vice_x128` types get `NotIngested` reasons (user)
- 2026-11-24 DAT? → `DatSource.None`, as MS-DOS: libretro-database has no Commodore 128 file in `no-intro`, `tosec` or `dat` (user, code)
- 2026-11-24 Core files and extensions → `@emulatorjs/core-vice_x128@4.2.3` packed and hashed 2026-11-24; its `valid_extensions` equal `vice_x64sc`'s; no threads (code)
- 2026-11-24 Reviewer round 1: `Systems_ListsEveryRegisteredSystem` and `SupportedSystemsTableTests` break in step 1, so the README row moved there; colour `#3d4a5c` too close to DOS/Saturn → `#9c4a00`; Duplicate wording, C128 NotIngested reason, `hasDat`/Manual test, vendor and IGDB comments, seeder folder (reviewer)
- 2026-11-24 Seeded data and e2e? → only the `ingest/c128` folder, no ROM; no e2e: the DAT-less Review flow is covered by the MS-DOS e2e and `ReviewEndpointsTests`, and the e2e stack has no VICE core to play with (code)

## Kickoff prompt
```text
/spec implement 2026-11-24-commodore-128-support.md
```
Starts the spec, or resumes it at the first unticked step.
