---
title: Commodore Plus/4 support
status: in-progress
created: 2026-11-25
---

# Commodore Plus/4 support

## Summary
Adds the `plus4` system (Commodore Plus/4) to `RnRomManager.Systems.Commodore`. Its disk, tape, program and cartridge images are stored in the game ZIP and matched against libretro's No-Intro `Commodore - Plus-4` DAT, which lists five `.bin` cartridges; every other file goes to Review. Games play in the browser with the operator-supplied EmulatorJS `vice_xplus4` core, from the existing `Emulator:ViceCorePath` folder. Multi-disk `.d64` games named `(Disk n of m)` get **All disks**, which plays them through a generated `.m3u` playlist, as Amiga floppy sets do. The handler follows `docs/adding-a-system.md`, reuses `C64Header` (which learns VICE's `C16-TAPE-RAW` tape signature) and copies the VIC-20 and C128 patterns.

## Out of scope
- Ingesting `.m3u` files. A playlist has nothing to hash, so an `.m3u` dropped into `ingest/plus4/` fails `UnsupportedType`. Sets come from release names (B11), as on Amiga.
- Sets of tapes or of mixed media. Only `.d64` disks form sets.
- Other `vice_xplus4` types (`.d71`, `.d81`, `.g64`, `.p00`, `.x64`, the 8050/8250, NIB and D2M/D4M images, `.tcrt`) and gzipped images (`.gz`, `.d6z`, …).
- Reading VICE's native Plus/4 `.crt` signature. Only the C64 signatures and `C16-TAPE-RAW` are read (B4).
- Commodore 16 as its own system, and machine options (C16 vs Plus/4 memory, PAL/NTSC). VICE xplus4's defaults and EmulatorJS's own menu apply.
- Sets for C64, C128 or any other system.
- A new e2e flow (see Decisions).

## Behaviour
- **B1 Identity.** `Plus4Handler` (new, `src/RnRomManager.Systems.Commodore/Plus4Handler.cs`):
  - `Id` `plus4`, `DisplayName` `Commodore Plus/4`, `ShortName` `Plus/4`.
  - `Family` `plus4` (its own family, like `vic20`).
  - `DatName` `Commodore - Plus-4`, `DatSource` the default (`Libretro`), `DatFolder` the default (`no-intro`), `MetadatKinds` `[]` (libretro has no Plus/4 metadat).
  - `EmbedRomsInZip` `true`, `KeepsSourceExtension` `true` (each extension is a distinct format, as on VIC-20 and C128).
- **B2 Formats.** `RomExtensions`, in this order: `.d64`, `.t64`, `.tap`, `.prg`, `.crt`, `.bin`.
  - The DAT's five entries are all `.bin`, so the `RomExtensions` DAT filter keeps all of them.
  - `SourceExtension` and `NormalizeForHashing` keep the interface defaults. There are no gzipped types, so neither `ViceImages` nor `GzipImage` is used.
  - A `.zip`/`.7z` dropped into `ingest/plus4/` is unpacked (`ArchiveExtractor.IsArchiveFor`).
  - An `.m3u` or any other unlisted type is listed as pending, then fails `UnsupportedType` (`IngestPipeline` step 1) and moves to `_failed/`, as on C128.
- **B3 Sizes.** `MinRomSizeBytes` 256 (small `.prg` programs), `MaxRomSizeBytes` 8 MiB (`8 * 1024 * 1024`). Both match VIC-20 and C128. A `.d64` is 174,848–196,608 B and the DAT's carts are 16 KiB.
- **B4 Header.** `C64Header.Read` gains one branch, after the `C64-TAPE-RAW` one: `rom.Length >= 20 && rom.StartsWith("C16-TAPE-RAW"u8)` returns `Plain(systemId, "tap", rom[0x0C])`. That is the same `format`/`version` fields as a C64 tape; VICE writes this signature for C16/Plus/4 tapes, with the version at 0x0C.
  - `Plus4Handler.ReadHeader(rom) => C64Header.Read(rom, Id)`.
  - So a `.tap` (`C16-TAPE-RAW` or `C64-TAPE-RAW`) and a C64-signature `.crt` get a header classified `plus4`. Every other file returns null: `.d64`, `.t64`, `.prg`, a `.bin` cart, and a native Plus/4 `.crt`. (`.g64` and `.nib` aren't Plus/4 types.)
  - On `c64` and `c128`, a `C16-TAPE-RAW` file now also gets a `tap` header classified as the caller. That's harmless: it's plausible, so an unmatched file still goes to Review, as it did with a null header.
- **B5 Ingest.**
  - A `.bin` whose SHA1 is in the DAT is ingested, stored in the ZIP under the DAT ROM name.
  - Every other accepted file goes to Review as `NoDatMatch` with the DAT's suggestions (`hasDat` true). A Manual, Flag or Dat resolution keeps the source extension.
  - A file already in the library or pending review (any system, e.g. a `.d64` under `c64`) is `Duplicate` first, as everywhere.
  - Ingestion's `DatsMissing` preflight blocks ingestion until an Admin runs **Refresh DATs** after the upgrade. That's the normal rule for a new libretro system; it is documented in deployment (step 5).
- **B6 Box art.** `DefaultBoxArtPng` loads `boxart.plus4.png` through `EmbeddedResources.Load`. The source is `Resources/boxart/plus4.svg` (new, drawn for this project, MIT, in the style of `c128.svg`), rendered to a 512×512 `plus4.png` (new) as the box-art README describes. The `.csproj` already embeds `Resources/boxart/*.png`. Missing box art is looked up from libretro-thumbnails' `Commodore_-_Plus-4` repo (it exists) through `DatName`, as for every libretro system.
- **B7 Browser play.** `BrowserCore` is `vice_xplus4`.
  - Play is offered for releases in the ZIP. It's not in `LibraryEndpoints.ExternalBrowserCores`.
  - EmulatorJS 4.2.3's `plus4` group holds only `vice_xplus4` (vendored `emulator.js` line 37), and the core is already in its `disableCue` list (line 819).
  - It doesn't need threads (`requiresThreads` doesn't list it), so it plays in the modal like the other VICE cores, never a threaded payload.
  - No BIOS: VICE embeds the Plus/4 ROMs (`biosFor` unchanged). `controlSchemeFor` returns null for it.
- **B8 Operator core.** `OperatorCoreManifest.Pinned` gains `VicePlus4` = `"vice_xplus4"`: display name `VICE (Plus/4)`, `o => o.ViceCorePath`, `EmulatorEndpoints.ViceCoreMismatch`. The files come from `@emulatorjs/core-vice_xplus4@4.2.3` (https://registry.npmjs.org/@emulatorjs/core-vice_xplus4/-/core-vice_xplus4-4.2.3.tgz; payloads at the package root, the report from `reports/`), packed and hashed 2026-11-25:

  | File | Bytes | SHA-256 | Content type |
  |---|---|---|---|
  | `vice_xplus4-wasm.data` | 1,413,246 | `f96b6660211a403b687e5f905394fda655c60250db7619a335ef2ccc160bfb30` | `application/octet-stream` |
  | `vice_xplus4-legacy-wasm.data` | 1,413,262 | `568e282a78c363f459f59c32b2745e2c26b245a6e55771edba5028a66a8b4063` | `application/octet-stream` |
  | `vice_xplus4.json` | 125 | `838318e4ee8d1ad2cfdd9fdf00350f57ea3eed1ca3d84491535f67074774e5d6` | `application/json` |

  The generic `GET /api/emulator/cores/vice_xplus4/<file>` route serves them from `Emulator:ViceCorePath`; no new option. A mismatch is `409 ViceCoreMismatch`. The two `-thread` payloads aren't used.
- **B9 Play-page text.** `params.js` `operatorCores.vice_xplus4` = `{ system: 'Commodore Plus/4 games', display: 'VICE (Plus/4)', short: 'VICE', option: 'Emulator:ViceCorePath' }`. Through `operatorCoreError` (unchanged):
  - a missing core (404) reads *Commodore Plus/4 games need the VICE (Plus/4) core files: ask an Admin to set them up (Emulator:ViceCorePath).*;
  - a mismatch (409) reads *The VICE (Plus/4) core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:ViceCorePath).*;
  - a `401` is the session-expired text; a network failure reads *Couldn't load the VICE core.*, any other status *Couldn't load the VICE core (HTTP <status>).*
- **B10 Core extensions.** The `valid_extensions` compiled into `vice_xplus4_libretro.wasm` (read from `vice_xplus4-wasm.data`, 2026-11-25) are identical to `vice_x64sc`'s and `vice_x128`'s: `d64|d71|d80|d81|d82|g64|g41|x64|t64|tap|prg|p00|crt|bin|zip|7z|gz|d6z|d7z|d8z|g6z|g4z|x6z|cmd|m3u|vfl|vsf|nib|nbz|d2m|d4m|tcrt`. Every B2 type is in it.
- **B11 Disk sets.** `DiscSets` extends the Amiga floppy rules (Amiga spec F1/F2) to `plus4`, with `.d64` in place of `.adf` and a looser key:
  - A release's token is the one `(Disk n of m)` in its `FileName` (case ignored), as on Amiga.
  - The set key on Plus/4 is `m` **alone**: within one game, every tokened release with the same `m` is in one group. Amiga keeps its key (`m` plus the name without the token and every `[…]` group). Plus/4 disks almost all come through Review, which renames them (Manual: `Title (flags).d64`; Attach → Release: source stem + ` (Unverified)`), so names rarely match beyond the token. Inside the floppy finder, `keyByName` is `systemId == "amiga"` (no extra parameter).
  - A set needs exactly one release per disk `1..m`, `2 ≤ m ≤ 20`, every `FileName` ending in `.d64` (case ignored).
  - The name is Disk 1's stem without its token, with trailing whitespace trimmed (`TrimEnd()`), plus `.m3u`: `Pixel Quest (Disk 1 of 2).d64` → `Pixel Quest.m3u`. The trim applies to Amiga too; TOSEC names have no space before the token, so Amiga names are unchanged.
  - **Supported Review paths** (documented in features): every disk's file name must carry its token. **Manual** with the same title and the flag `Disk <n> of <m>` gives `<Title> (Disk <n> of <m>).d64`; with no target it joins the one Plus/4 game that has that base title, creates one if none has it, and fails `AmbiguousGroup` if several do (`IngestPipeline`, `ListGameIdsByBaseTitleAsync`). **Attach → Release** onto the game keeps the source stem plus ` (Unverified)`, so a TOSEC name such as `Pixel Quest (1986)(Synth)(Disk 2 of 2).d64` keeps its token. The two can be mixed. Attach as Hack or Translated also keeps the source stem (plus ` (Hack)`/` (Translated)`), so its token stays. A **Dat** pick names the file after the chosen DAT cart plus ` (Unverified)`, so that disk loses its token and joins no set (not documented beyond this spec).
  - `MaxDiscs("plus4")` is 20: vice-libretro's `DC_MAX_SIZE` in `libretro/libretro-dc.h`, which is the same as PUAE's.
  - A new `public static string? FloppyExtension(string systemId)` returns `.adf` for `amiga`, `.d64` for `plus4`, and null for any other system. `Find` sends any system with a floppy extension to the floppy finder, now `FindDisks(string systemId, string extension, IReadOnlyList<RomRow> roms)`, which caps sets at `MaxDiscs(systemId)` (not Amiga's).
  - `discSet` on `GET /api/games/{id}` (`LibraryEndpoints.DetailAsync`, which already calls `DiscSets.Find` for every game) is filled for Plus/4 Disk 1 releases.
- **B12 Endpoint.** `GET /api/games/{id}/roms/{romId}/play-disc-set` serves Plus/4 sets exactly as Amiga sets (Amiga F5: access, headers, response shape, errors, no 2 GiB cap).
  - `HandleSetAsync` routes every game whose `DiscSets.FloppyExtension(game.SystemId)` is non-null to `FloppySetAsync`, passing that extension.
  - `FloppySetAsync`'s guard becomes `d.SystemId != game.SystemId || d.ExternalFileName is not null` (404). The system half is defensive only: `RomRow.SystemId` always comes from the game (`MirrorEntry.ToRows`), so only the external half is testable.
  - `PlayDisc.AmigaSystemId` (public const, no other users) is removed; `HandleSetAsync` no longer names a system.
  - Its entry names become `Disk <n><extension>`. Plus/4 gets `Disk 1.d64`, `Disk 2.d64`…; Amiga is unchanged (`Disk <n>.adf`).
  - The playlist lists them one per line, LF-terminated, UTF-8 without BOM.
  - The empty-disk title is unchanged: `The disc can't be played: Disk <n>: '<FileName>' is empty.`
- **B13 Play URL and player.** `params.js` gains `export const diskSetCores = ['puae', 'vice_xplus4'];` (new). It is used in three places:
  - `parsePlayParams` accepts `set=1` with a `diskSetCores` core only when `disc=1` is also given. This replaces the `core === 'puae'` test.
  - `discError` uses the PUAE disk wording for every `diskSetCores` core: *These disks can't be played in the browser: one of them is empty.* for `InvalidPlayDisc`, else *Couldn't load the disks.* / *Couldn't load the disks (HTTP <status>).*; `401` stays the session-expired text.
  - `play.js` shows `Downloading disks…` for a `diskSetCores` core, else `Downloading disc…`.

  EmulatorJS then unpacks the ZIP and boots the `.m3u`. `vice_xplus4`'s `core.json` lists `m3u`, the ZIP holds no `.cue`/`.ccd`/`.toc`, and the core is in the `disableCue` list, so `downloadRom()` picks the playlist as it does for PUAE. vice-libretro's `dc_parse_m3u` attaches Disk 1 and autostarts it; the **Disks** menu swaps disks. No vendored-loader edit is needed.
- **B14 Game page.** `emulator.ts`'s `setMedia` returns `'disks'` for `puae` and `vice_xplus4`, else `'discs'`. A Plus/4 set's Disk 1 therefore shows the Amiga button: text `All disks`, `aria-label="Play all disks of <name>"`, tooltip `Play all disks in one session`. The modal (Amiga F9) is unchanged: title `Play <name>`, battery saves on Disk 1, and **Save state to library** is hidden.
- **B15 UI colour.** System colour `plus4`, in `app.css` after the C128 block, in the sibling pattern:

  ```css
  /* Commodore Plus/4 system colour: a dark olive, same in both themes; white on it is AAA (~7.1:1). */
  :root {
  	--bs-plus4: #5f5a1e;
  }
  .text-bg-plus4 {
  	color: #fff !important;
  	background-color: var(--bs-plus4) !important;
  }
  ```

  `systemColours.ts` adds `'plus4'` to the `SystemColour['name']` union and `plus4: { name: 'plus4', foreground: '#fff' }` to the map, each after `pet`. There's no `icons.ts` entry (controller fallback, as for the other Commodores). The system filter keeps `/api/systems`' `DisplayName` order: `Commodore PET`, `Commodore Plus/4`, `Commodore VIC-20`.
- **B16 IGDB.** `IgdbPlatforms.ById["plus4"]` = 94 (IGDB's *Commodore Plus/4*), right after `["pet"]`. Implement checks the id at https://www.igdb.com/platforms/commodore-plus-slash-4 before committing; if it differs, use IGDB's id and note it in § Decisions.
- **B17 Emulator links.** `systemEmulators.json` key `plus4`, right after `pet`: VICE (`https://vice-emu.sourceforge.io/`, windows/macos/linux) and RetroArch (`https://www.retroarch.com/?page=platforms`, windows/macos/linux/android/ios, note `RetroArch core: VICE xplus4`), mirroring `c128`.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Systems.Commodore/Plus4Handler.cs` (new) | B1–B7. Its summary names the DAT's `.bin` carts, the Review flow and the reused header. |
| Core | `src/RnRomManager.Systems.Commodore/C64Header.cs` | B4: the `C16-TAPE-RAW` branch; the class summary adds "and VICE's C16/Plus/4 tape". |
| Core | `src/RnRomManager.Systems.Commodore/Resources/boxart/plus4.svg`, `plus4.png` (new), `Resources/boxart/README.md` | B6. The README's sources sentence reads "`c64.svg`, `c128.svg`, `pet.svg`, `plus4.svg`, `vic20.svg` and `amiga.svg` are the sources (made for this project, MIT)." |
| Core | `src/RnRomManager.Core/Library/DiscSets.cs` | B11: `FloppyExtension`, `MaxDiscs("plus4")` 20, and the floppy finder takes the extension. The class, `MaxDiscs` and `FindDisks` summaries name Plus/4 and vice-libretro's `DC_MAX_SIZE`. |
| Core | `src/RnRomManager.Core/Media/IgdbPlatforms.cs` | B16. |
| Api | `src/RnRomManager.Api/AppHost.cs` | `services.AddSystemHandler<Plus4Handler>();` right after `C128Handler`. The project is already referenced, so there's no `Dockerfile`/`.slnx` change. |
| Api | `src/RnRomManager.Api/Endpoints/OperatorCoreManifest.cs` | B8: the `VicePlus4` const after `ViceC128`, and the pinned entry after C128's. Its comment: `// Commodore Plus/4: https://registry.npmjs.org/@emulatorjs/core-vice_xplus4/-/core-vice_xplus4-4.2.3.tgz. Embeds the` / `// Plus/4's ROMs like vice_x64sc, so it's never bundled; its files sit in the same folder as the C64 core's.` |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | B12. The `HandleSetAsync`, `FloppySetAsync` and `FirmwareNames` summaries say "Amiga and Plus/4 floppies". |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | B11: comment only. The `DiscSets.Find` comment in `DetailAsync` (~line 541) reads `// Multi-disc M3 (Saturn/Sega CD S2, Amiga F3, Plus/4 B11): PlayStation, Saturn, Sega CD, Amiga and Plus/4; other systems have none. Keyed by each set's Disc 1.` |
| Web | `static/emulator/params.js` | `'vice_xplus4'` after `'vice_x128'` in the `cores` allowlist, in `operatorCores` (B9, entry after `vice_x128`) and in `hidesCoreChooser`. The `operatorCores` JSDoc list adds ", Plus/4 VICE xplus4" after "C128 VICE x128". Also B13 (`diskSetCores`, `parsePlayParams`, `discError`; the set comment reads "Amiga F6, Plus/4 B13: a disk-set core too, with `disc=1`"). |
| Web | `static/emulator/play.js` | B13 status: `diskSetCores` joins the `import { … } from './params.js'` list and the line reads `show(diskSetCores.includes(params.core) ? 'Downloading disks…' : 'Downloading disc…');`. The pinned-cores comment (~line 757) reads "VICE x64sc, xpet, xvic, x128 and xplus4". |
| Web | `scripts/vendor-emulatorjs.mjs` | `"vice_xplus4"` after `"vice_x128"` in the explicit-core patch, the non-threaded guard and the no-CDN guard (~lines 89, 100, 110). Comments: on line 53, "vice_xvic of the vic20 group and vice_x128 of the c128 group." becomes "vice_xvic of the vic20 group, vice_x128 of the c128 group and vice_xplus4 of the plus4 group."; line 62 adds "Plus/4" after "C128"; line 63 reads "VICE (x64sc, xpet, xvic, x128 and xplus4)". |
| Web | `src/lib/emulator.ts` | B14 `setMedia` (`core === 'puae' \|\| core === 'vice_xplus4'`; the SPA can't import `params.js`); its comment reads `/** Amiga F8, Plus/4 B14: what a set's All button plays: floppy disks for PUAE and VICE (xplus4), discs for every other core. */`. |
| Web | `src/lib/systemColours.ts`, `src/app.css` | B15. |
| Web | `src/lib/systemEmulators.json` | B17. |
| Tests | `tests/RnRomManager.TestData/IngestSeeder.cs` | `public const string Plus4Dat = "Commodore - Plus-4";` after `Vic20Dat`; `Directory.CreateDirectory(Path.Combine(ingest, "plus4"));` after the `c128` line; `[Plus4Dat] = []` after `[Vic20Dat] = …` in the `FixtureDatWriter.Write` dictionary (line ~437). |
| Docs | `README.md`, `docs/adding-a-system.md`, `docs/configuration.md`, `docs/deployment.md`, `docs/development.md`, `docs/features.md`, `docs/zip-format.md` | Step 5. |

The change needs:
- no migration (`system_id` is a `VARCHAR`);
- no new config key.

API contracts change only additively: `plus4` appears in `/api/systems` and `/api/admin/dats`, `browserCore` can be `vice_xplus4`, and `discSet` is non-null on Plus/4 Disk 1 releases.

## Risks
- **Unverified boot:** nothing here has been run with a real dump. These come from reading the code (`emulator.js`'s `plus4` group and `downloadRom()`, `core.json`, vice-libretro's `dc_parse_m3u`):
  - that `vice_xplus4` autostarts a `.d64`/`.tap`/`.prg` and attaches a `.bin` or `.crt` cart through EmulatorJS 4.2.3;
  - that it boots a set's `.m3u` and swaps disks.

  Finish includes one manual check (a single `.d64` and a two-disk set) if the maintainer has the core files and dumps; otherwise the PR says so.
- **Upgrade blocks ingestion until Refresh DATs** (B5), as with every libretro system. The deployment note says so.
- **Shared header change:** B4 touches `C64Header`, which `c64` and `c128` use. Only files starting `C16-TAPE-RAW` change, from no header to a plausible `tap` header. `C64HeaderTests` pins it.
- **Shared set code:** B11/B12 generalise the Amiga floppy path. The existing Amiga `DiscSetsTests` and `LibraryEndpointsTests.PlayDiscSet.cs` cases must pass unchanged.
- **Variant ambiguity:** as on Amiga, a game holding two dumps of one disk gets no All disks. Each disk still plays alone.
- **Licensing:** the core embeds Commodore ROMs. It's never bundled, vendored or committed, as for the other VICE cores.

## Steps
- [x] **1. Handler and header**: B1–B4, B6, B7 (`BrowserCore` value only). Files: `Plus4Handler.cs`, `C64Header.cs`, `Resources/boxart/plus4.svg`, `plus4.png`, boxart `README.md`. It isn't registered yet. Tests:
  - `tests/RnRomManager.Systems.Commodore.Tests/Plus4HandlerTests.cs` (new, modelled on `C128HandlerTests`):
    - `Plus4_Contract`: `(Id, DisplayName, ShortName, Family, DatName)` = `("plus4", "Commodore Plus/4", "Plus/4", "plus4", "Commodore - Plus-4")`; exact `RomExtensions`; sizes `(256L, 8L * 1024 * 1024)`; `(EmbedRomsInZip, DatSource, DatFolder)` = `(true, DatSource.Libretro, DatKind.NoIntro)`; empty `MetadatKinds`; `(FileSets, LegacyEmbeddedRoms, KeepsSourceExtension)` = `(false, false, true)`; `BrowserCore` `vice_xplus4`.
    - `ReadHeader_ReadsAPlus4Tape`: a 20-byte `C16-TAPE-RAW` with version 1 at 0x0C gives `ClassifiedSystemId` `plus4`, `IsPlausible` true, `format` `tap` and `version` 1.
    - `ReadHeader_IsNullForOtherFiles`: a 300-byte `[0x01, 0x10, …]` `.prg`-like array, a zeroed 174,848-byte `.d64` and a 16 KiB `.bin` of `new Random(16).NextBytes` all give null, as does a 64-byte native Plus/4 `.crt` starting `C264 CARTRIDGE  `.
    - `DefaultBoxArt_IsA512x512Png`.
  - `C64HeaderTests`: `C16Tap_ReadsFormatAndVersion` (new, after `Tap_ReadsFormatAndVersion`, mirroring it with `WithMagic("C16-TAPE-RAW", 20)`; classified `c64`, the same two fields) and `[TestCase("C16-TAPE-RAW", 19)]` in `OneByteShortOfTheMinimum_IsNull`.

  Done when: `Plus4HandlerTests`, `C64HeaderTests`, `C64HandlerTests`, `C128HandlerTests` and `Vic20HandlerTests` pass, and `dotnet build` is warning-free.
- [x] **2. Registration, DAT fixtures and operator core**: B5, B8, B10, B16, B17. Files: `AppHost.cs`, `IngestSeeder.cs`, `IgdbPlatforms.cs`, `OperatorCoreManifest.cs`, `systemEmulators.json`, and the `README.md` supported-systems row `` | Commodore Plus/4 | `plus4` | In ZIP | `.d64`, `.t64`, `.tap`, `.prg`, `.crt`, `.bin` | No-Intro | `vice_xplus4` | ``, right after the Commodore PET row (`No-Intro` as on the VIC-20 row). Tests:
  - Every test file listing `IngestSeeder.Vic20Dat` in a full-registry manifest adds `[IngestSeeder.Plus4Dat] = []` right after it. Today that's `AuditLibraryEditTests`, `DatEndpointsTests`, `GamePlayedEndpointTests`, `ImageEndpointsTests`, `ImageUrlEndpointsTests`, `IngestArchiveTests`, `IngestDeleteEndpointsTests`, `IngestEndpointsTests`, `IngestPipelineTests`, `IngestRestoreEndpointsTests`, `IngestReviewTests` (2), `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `NoteEndpointsTests`, `ReportEndpointsTests`, `ReviewEndpointsTests` (3) and `ZipEntryEndpointsTests`. Grep again when implementing.
  - `DatEndpointsTests`:
    - the names count 43 → 44, with message "30 libretro systems, the 10 disc systems, Switch, the test titledb system, MAME and FBNeo";
    - the admin-rows list gains `IngestSeeder.Plus4Dat` right after `IngestSeeder.PetDat` (`Commodore - PET` < `Commodore - Plus-4` < `Commodore - VIC-20`);
    - refresh requests 141 → 142, with message "…, 1 for Commodore 64, Commodore PET, Commodore Plus/4, Commodore VIC-20, Commodore Amiga and each disc system (no metadat), …".
  - `LibraryEndpointsTests`:
    - `Systems_ListsEveryRegisteredSystem_WithCountsAndSortedRegions`: `"plus4"` between `"pet"` and `"vic20"` in the id array, and `"Commodore Plus/4"` between `"Commodore PET"` and `"Commodore VIC-20"` in the display-name array; `Has.Exactly(29).True` for `embedRoms` (was 28); and, after the `vic20` assertion, `Assert.That((Find("plus4").GetProperty("displayName").GetString(), Find("plus4").GetProperty("shortName").GetString()), Is.EqualTo(("Commodore Plus/4", "Plus/4")));`.
    - `[TestCase("plus4", false, true, "vice_xplus4")]` in `Games_QuickPlay_IsThePrimaryReleaseWhenPlayable` and `[TestCase("plus4", "vice_xplus4")]` in `GameDetail_BrowserCore_IsTheSystemHandlers`, each right after its `vic20` case.
  - `SystemStartupTests.Startup_RegistersEverySystem_AndCreatesTheirIngestFoldersOnly` (both id arrays) and `IngestEndpointsTests.Pending_ListsEverySystem_IgnoringJunk`: `"plus4"` right after `"c128"` (registration order).
  - `IgdbPlatformsTests.Map_HasEveryRegisteredSystem_AndNothingElse` passes with B16, and `PlatformIds_AreDistinctAndPositive` passes unchanged (94 is unique).
  - `BrowserCoreExtensionsTests`:
    - `CoreExtensions["vice_xplus4"]` = the B10 string and `OperatorPayloadSha256["vice_xplus4"]` = `"f96b6660211a403b687e5f905394fda655c60250db7619a335ef2ccc160bfb30"`, each right after its `vice_x128` entry;
    - `"vice_xplus4"` appended to the `Vice` array, so the `Archive`, `Playlist`, `NotAGame` and `NoDat` rows cover its `.zip`, `.7z`, `.m3u`, `.vsf`, `.cmd`, `.vfl`, `.g41`, `.g4z`;
    - a new constant `Plus4Scope` = `"left out of Commodore Plus/4 by its spec (Out of scope)"` after `C128Scope`, and the `NotIngested` row `(Plus4Scope, ["vice_xplus4"], [".d71", ".d80", ".d81", ".d82", ".g64", ".x64", ".p00", ".nib", ".nbz", ".d2m", ".d4m", ".tcrt", ".gz", ".d6z", ".d7z", ".d8z", ".g6z", ".x6z"])` after the `C128Scope` row. These 18, the 8 above and the 6 B2 types make the core's 32.
    - `EveryCoreExtension_IsIngestedOrListed`, `EveryHandlerExtension_IsOpenedByItsBrowserCore` and `OperatorCoreLists_WereReadFromThePinnedPayloads` pass.
  - `EmulatorEndpointsTests.OperatorCores.cs`:
    - in `PinnedManifest_NamesEachCore`, the pinned-ids array gains `"vice_xplus4"` after `"vice_x128"`;
    - after the `c128` assertion, `var plus4 = OperatorCoreManifest.Pinned.Find("vice_xplus4")!;` and `Assert.That((plus4.DisplayName, plus4.MismatchCode, plus4.PathOf(options)), Is.EqualTo(("VICE (Plus/4)", "ViceCoreMismatch", "/cores/vice")), "shares the C64 folder");`;
    - a new `PinnedManifest_HoldsExactlyTheVicePlus44_2_3Files` after `PinnedManifest_HoldsExactlyTheViceC1284_2_3Files`, asserting the three B8 `OperatorCoreFile`s through `OperatorCoreManifest.VicePlus4`.
  - `IngestPipelineTests`:
    - a new field `Plus4Cart = Atari(16 * 1024, 2711)` after `StarDuel` (2711 is unused in the class);
    - the class manifest's `[IngestSeeder.Plus4Dat] = []` becomes `[new FixtureGame("Pixel Pilot (USA, Europe)", "Pixel Pilot (USA, Europe).bin", Plus4Cart)]`;
    - `Plus4_ADatMatchedCart_IsStoredUnderTheDatsName` (new, after `Sufami_ADatMatchedCart_IsStoredUnderTheDatsName`, modelled on it): `DropAged("plus4", "pilot.bin", Plus4Cart)` gives `(IngestOutcome.Ingested, "plus4")`, and `roms/plus4/P/Pixel Pilot.zip` holds one release `Pixel Pilot (USA, Europe).bin`;
    - `Plus4_AnUnmatchedTape_GoesToReview_WithItsHeader` (new, right after): a 300-byte `C16-TAPE-RAW` `game.tap` (version 1 at 0x0C, above the 256 B floor) gives `(IngestOutcome.Review, "plus4")`, and the review item's stored header has `format` `tap` and `version` 1.
  - `SupportedSystemsTableTests.ReadmeTable_MatchesTheRegisteredHandlers` and `SystemEmulatorsCatalogTests.Catalog_MatchesTheRegisteredHandlers_AndFollowsTheRules` pass.

  Done when: those classes pass (integration ones with Docker), and `dotnet build` is warning-free.
- [x] **3. Disk sets (Core + API)**: B11, B12. Files: `DiscSets.cs`, `PlayDisc.cs`, `LibraryEndpoints.cs` (comment). Tests:
  - `tests/RnRomManager.Core.Tests/Library/DiscSetsTests.cs`:
    - `[TestCase("plus4", 20)]` in `MaxDiscs_IsPerSystem`;
    - `FloppyExtension_IsPerSystem` (new): `amiga` → `.adf`, `plus4` → `.d64`, `psx` and `c64` → null;
    - `Plus4_TwoDisks_AreOneSet_NamedFromDiskOne` (new): `Pixel Quest (1986)(Synth)(Disk 1 of 2).d64` and `…(Disk 2 of 2).d64` form one set named `Pixel Quest (1986)(Synth).m3u`, in disk order;
    - `Plus4_DisksWithDifferentNames_AreOneSet` (new): `Pixel Quest (Disk 1 of 2).d64` and `Pixel Quest (1986)(Synth)(Disk 2 of 2) (Unverified).d64` form one set named `Pixel Quest.m3u`, in disk order;
    - `Plus4_TwoDumpsOfOneDisk_AreNoSet` (new): two `(Disk 1 of 2)` releases and one `(Disk 2 of 2)` give no set;
    - `Amiga_DisksWithDifferentNames_AreNoSet` (new): the same names on `amiga` (as `.adf`) give no set, pinning that Amiga's key is unchanged;
    - `Plus4_AdfDisks_AreNoSet` and `Amiga_D64Disks_AreNoSet` (new): the wrong system's extension gives no set;
    - `Plus4_TakesUpToTwentyDisks` (new, mirroring `Amiga_TakesUpToTwentyDisks`): 20 disks form a set, 21 don't;
    - `C64_DiskTokens_AreNoSet` (new): `Find("c64", …)` on two tokened `.d64`s is empty;
    - every existing Amiga and disc case passes unchanged.
  - `tests/RnRomManager.Api.Tests/Integration/LibraryEndpointsTests.PlayDiscSet.cs`:
    - `AmigaGame` is generalised to a private `FloppyGame(string system, string title, int seed, Func<…>? shape, params string[] fileNames)`, which sets `System = system` and writes the ZIP under `$"{system}/{title[..1]}/{title}.zip"`. The two `AmigaGame` overloads stay as one-line wrappers, so the Amiga tests are untouched. The Plus/4 tests use seeds 9301, 9311 and 9321.
    - `Detail_DiscSet_IsOnPlus4DiskOne_WithTheNameAndOrderedIds_AndNullElsewhere` (new): a `plus4` game with `Pixel Quest (Disk 1 of 2).d64` and `Pixel Quest (1986)(Synth)(Disk 2 of 2) (Unverified).d64` has `discSet` `{ name: "Pixel Quest.m3u", romIds: [disk 1, disk 2] }` on Disk 1 and null on Disk 2.
    - `PlayDiscSet_APlus4Set_IsThePlaylist_ThenEachDiskAliased` (new, after the Amiga one): entries `["Pixel Quest (1986)(Synth).m3u", "Disk 1.d64", "Disk 2.d64"]`, playlist bytes `"Disk 1.d64\nDisk 2.d64\n"`, disks byte-exact, `X-Content-Bytes`.
    - `PlayDiscSet_APlus4SetWithAnExternalDisk_Is404` (new, modelled on `PlayDiscSet_AmigaDiskTwo_AnAmbiguousGame_OrAnExternalDisk_Is404`): Disk 2's mirrored row shaped with an `ExternalFileName` gives 404 (the B12 guard).
    - The existing Amiga and PlayStation/Saturn/Sega CD set tests pass unchanged.

  - `ReviewEndpointsTests.Plus4_ManualDiskOne_ThenAttachReleaseDiskTwo_MakeASet` (new, after `AttachRelease_TagsFromTheFileName_Unverified_NotPrimary_A2`): two 174,848-byte `.d64`s of `new Random(seed).NextBytes` (seeds 960 and 961) are written to `ingest/plus4/` as `Pixel Quest (1986)(Synth)(Disk 1 of 2).d64` and `…(Disk 2 of 2).d64` and ingested as `Attach_ToALegacyGame_IsStorageMigrationPending_AndStaysPending` does (aged 5 minutes, `new IngestFile("plus4", …)`), each giving `IngestOutcome.Review`; Disk 1 is resolved with `new { action = "Manual", title = "Pixel Quest", regions = Array.Empty<string>(), languages = Array.Empty<string>(), flags = new[] { "Disk 1 of 2" } }` (file `Pixel Quest (Disk 1 of 2).d64`), Disk 2 with `new { action = "Attach", targetGameId = <Disk 1's game>, flag = "Release" }`; `GET /api/games/{id}` has `discSet` on Disk 1 with name `Pixel Quest.m3u` and both ids in disk order.

  - `ReviewEndpointsTests.Plus4_ManualOnBothDisks_JoinsOneGame_AndMakesASet` (new, right after): the same drops with seeds 962 and 963, both resolved with that Manual body (flags `["Disk 1 of 2"]` then `["Disk 2 of 2"]`, no `targetGameId`), land in one game, and `discSet` on Disk 1 is `Pixel Quest.m3u` with both ids in disk order.

  Done when: `DiscSetsTests`, `LibraryEndpointsTests` and `ReviewEndpointsTests` pass.
- [x] **4. Web**: B9, B13–B15. Files: `params.js`, `play.js`, `vendor-emulatorjs.mjs`, `emulator.ts`, `systemColours.ts`, `app.css`. Tests:
  - `src/lib/emulatorParams.test.ts`:
    - the `accepts the %s core` list (~line 168) gains `'vice_xplus4'` after `'vice_x128'`;
    - `describe('Commodore Plus/4')` (new) right after `describe('Commodore 128')`, mirroring it with the B8 files and B9 texts;
    - next to the PUAE `set` cases (~line 134), `parsePlayParams` accepts `core: 'vice_xplus4', disc: '1', set: '1'` and refuses it without `disc`;
    - after the PUAE `discError` cases (~line 362), `discError(409, 'InvalidPlayDisc', 'vice_xplus4', true)`, `discError(null, null, 'vice_xplus4', true)` and `discError(500, null, 'vice_xplus4', true)` give the three B13 texts.
  - `src/lib/vendorEmulatorjs.test.ts`:
    - the three guarded-edit strings (~lines 131, 140, 147) and the two `includes(this.getCore())` expectations (~705, 709) gain `"vice_xplus4"` after `"vice_x128"`;
    - two new `it.each(['src/emulator.js', 'emulator.min.js'])` cases right after the two `vice_x128` ones (~858–882), mirroring them: `%s pins an explicit vice_xplus4, in the plus4 group` and `%s never picks a threaded VICE (Plus/4) payload, and shows a local error for a failed one`.
  - `src/lib/emulatorPlay.test.ts`, in `describe('play page Amiga disk sets (Amiga F7)')`: its `playDisks(name)` helper gains a second parameter `core = 'puae'` (Amiga calls unchanged), and a new `it('loads a Plus/4 set from play-disc-set, saying it downloads disks (Plus/4 B13)')` serves `/api/emulator/cores/vice_xplus4` (204) and the set URL, calls `playDisks('Pixel Quest.m3u', 'vice_xplus4')`, and expects `urls()` `['/api/emulator/cores/vice_xplus4', setUrl]`, the status `Downloading disks…` while the set loads, `EJS_core` `vice_xplus4`, `EJS_gameName` and the `File` name `Pixel Quest.m3u`, and `EJS_paths` the three `/api/emulator/cores/vice_xplus4/<file>` URLs.
  - `src/lib/emulator.test.ts`, in `describe('setMedia (Amiga F8)')`: `expect(setMedia('vice_xplus4')).toBe('disks');` after the `puae` line.
  - `src/routes/games/game-page.test.ts`: `it('Disk 1 of a Plus/4 disk set has All disks, which plays the set (Plus/4 B14)')`, right after the Amiga case (~line 389), mirroring it with `system` `plus4`, `browserCore` `vice_xplus4` and `discSet` `{ name: 'Pixel Quest.m3u', romIds: [1, 2] }`: button `Play all disks of Pixel Quest.m3u`, text `All disks`, and iframe `src` `/emulator/play.html?game=<id>&rom=1&core=vice_xplus4&name=Pixel%20Quest.m3u&disc=1&set=1`.
  - `src/lib/systemColours.test.ts`: in the Commodore case, `expect(systemColour('plus4')).toEqual({ name: 'plus4', foreground: '#fff' });` after the `c128` line.

  Done when: those files, `npm run lint` and `npm run check` pass, and `npm run predev` vendors without error.
- [ ] **5. Docs**: "Commodore Plus/4" goes right after "Commodore PET" in every list that has it, else right after "Commodore 64" (features' intro, **Play** and separate-families lists have no PET; adding the missing PET/VIC-20 there is out of scope), and `vice_xplus4` right after `vice_x128` wherever cores are.
  - `README.md`:
    - the **Game detail** Play list: "Commodore PET, Commodore Plus/4, Commodore VIC-20"; after "…plays up to 20 disks in one session with in-game disk swapping;" add " Commodore Plus/4 multi-disk `.d64` games get **All disks** the same way;"; the operator-core sentence reads "…Commodore PET, Commodore Plus/4, Commodore VIC-20 and Nintendo 64 need … VICE (`vice_x64sc`, `vice_xpet`, `vice_xvic`, `vice_x128`, `vice_xplus4`) …";
    - the core attribution reads "VICE (Commodore 64, 128, PET, Plus/4 and VIC-20) isn't included either";
    - the "Upgrading from a version without …" list: "Commodore PET, Commodore Plus/4, Commodore VIC-20".
  - `docs/configuration.md`:
    - the `Emulator:ViceCorePath` row reads "empty (Commodore 64, 128, PET, Plus/4 and VIC-20 play off)";
    - § VICE core gains, after the C128 table (before "The archive's two `-thread` payloads aren't used."), "Commodore Plus/4 plays with VICE (xplus4), which embeds the Plus/4's ROMs for the same reason. Its three files from [`@emulatorjs/core-vice_xplus4@4.2.3`](https://registry.npmjs.org/@emulatorjs/core-vice_xplus4/-/core-vice_xplus4-4.2.3.tgz) go in the same folder too (`GET /api/emulator/cores/vice_xplus4/<file>`, the same `409 ViceCoreMismatch`):" and the B8 table (File, Bytes, SHA-256).
  - `docs/deployment.md`:
    - the `/app/ingest` row gains "`ingest/plus4/` (Commodore Plus/4: `.d64`, `.t64`, `.tap`, `.prg`, `.crt` or `.bin`)," after the `ingest/pet/` entry;
    - right after the "Upgrading to the release that adds PC-FX:" note (the newest system's), "Upgrading to the release that adds Commodore Plus/4:" with two bullets, mirroring VIC-20's: "**Refresh DATs after upgrading.** Commodore Plus/4 is identified against libretro's No-Intro `Commodore - Plus-4` DAT (one file, no metadat). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `ingest/plus4/` is created at startup." (not `roms/plus4/`, which the first game creates, per `SystemStartupTests`) and "**For Commodore Plus/4 browser play,** add the `vice_xplus4` files to the [VICE core](#vice-core) folder.";
    - § VICE core gains, after the Commodore 128 line, "For Commodore Plus/4, likewise with `@emulatorjs/core-vice_xplus4@4.2.3` (`vice_xplus4-wasm.data`, `vice_xplus4-legacy-wasm.data`, `reports/vice_xplus4.json`)."
  - `docs/features.md`:
    - the intro system list and the **Play** list: "Commodore 64, Commodore Plus/4"; the core list: "VICE (x64sc) for Commodore 64, VICE (xplus4) for Commodore Plus/4";
    - a bullet after the C128 one: "**Commodore Plus/4** uses VICE (xplus4), from the same folder; it needs no BIOS and starts a disk image, tape, program or cartridge by its extension. Disk images named `(Disk n of m)` (one `.d64` per disk, 2–20 disks) get **All disks**, which plays them in one session; the player's **Disks** menu swaps them.";
    - § Ingest, after the Commodore 128 sentences (ending "Any other type fails `UnsupportedType`.") and before "Drop Commodore Amiga": "Drop Commodore Plus/4 disk images (`.d64`), tapes (`.t64`, `.tap`), programs (`.prg`) and cartridges (`.crt`, `.bin`), or archives of them, into `ingest/plus4/`: a `.bin` cartridge in the No-Intro DAT is matched; anything else goes to Review and keeps its extension. A `.tap` with VICE's `C16-TAPE-RAW` signature keeps its tape header. A `.m3u` or any other type fails `UnsupportedType`. For **All disks**, every disk's release needs its `(Disk n of m)` token: resolve each disk with **Manual** (the same title and the flag `Disk n of m`) or **Attach → Release** onto Disk 1's game (a source name with the token keeps it)."; the separate-families list: "Commodore 64, Commodore Plus/4, Commodore VIC-20"; the size list: "256 bytes–8 MiB for Commodore PET, Commodore Plus/4 and Commodore VIC-20";
    - `GET /api/games/{id}`: "or Disk 1 of an Amiga or Commodore Plus/4 disk set" and "(an Amiga or Plus/4 set's is Disk 1's file name without its `(Disk 1 of m)` token and trailing spaces, as `.m3u`)";
    - `play-disc-set`: "An **Amiga** or **Commodore Plus/4** disk set is sent the same way from inside the game's ZIP: the playlist (`Disk 1.adf`, `Disk 2.adf`… on Amiga, `Disk 1.d64`, `Disk 2.d64`… on Plus/4, one per LF-terminated line)…", and its errors "isn't Disk 1 of an Amiga or Plus/4 set".
  - `docs/development.md`:
    - the `src/RnRomManager.Systems.Commodore` row: "…the `c128` (Commodore 128) handler, which reuses it, the `plus4` (Commodore Plus/4) handler, which reuses it too (it also reads VICE's `C16-TAPE-RAW` tape), the headerless `pet`…";
    - the operator-cores bullet: "(Genesis, 32X, SNES, 3DO, Commodore 64, 128, PET, Plus/4, VIC-20, Nintendo 64, Arcade)" and `vice_xplus4` after `vice_x128`;
    - the Multi-disc sets bullet: `set` is taken "with `pcsx_rearmed`, `yabause`, or `genesis_plus_gx`, `puae` or `vice_xplus4` plus `disc=1`"; after "…makes the game page's button **All disks**." add "Commodore Plus/4 floppy sets (`vice_xplus4`, `DC_MAX_SIZE` 20 in vice-libretro's `libretro-dc.h`) work the same way with `.d64` (`DiscSets.FloppyExtension`), aliased `Disk <n>.d64`, but group a game's tokened releases by `m` alone, since Review renames them; `params.js`'s `diskSetCores` lists both cores for `set`, `discError` and *Downloading disks…*, and `setMedia` returns `'disks'` for both."
  - `docs/zip-format.md`:
    - `  plus4/                Commodore Plus/4 (.d64, .t64, .tap, .prg, .crt, .bin)` after the `pet/` line;
    - `` `plus4` `` after `` `c64` `` in the `system` id list (it has no `pet`);
    - after the Commodore 128 `header` paragraph: "Commodore Plus/4 `header` is the same (classified `plus4`) for a `.tap` (`C16-TAPE-RAW` or `C64-TAPE-RAW`) or a C64-signature `.crt`, and null for every other file, including a native Plus/4 `.crt`. A `C16-TAPE-RAW` tape gets the same `tap` header on Commodore 64 and 128."
  - `docs/adding-a-system.md`:
    - the `BrowserCore` row: "`vic20` returns `vice_xvic`, `c128` returns `vice_x128` and `plus4` returns `vice_xplus4`, `n64` returns …";
    - the `DefaultBoxArtPng` list: "Commodore PET/Commodore Plus/4/Commodore VIC-20";
    - the `ReadHeader` row's last sentence: "`C128Handler` and `Plus4Handler` reuse it (`C64Header.Read(rom, Id)`), which also reads VICE's `C16-TAPE-RAW` tape.";
    - § Operator-supplied cores reads "(VICE for `c64`, `c128`, `pet`, `plus4` and `vic20`)".

  Tests: `SupportedSystemsTableTests`. Done when: it passes and `pwsh scripts/codemap.ps1` is committed.

## Decisions
- 2026-11-25 What do ".m3u sets" mean? → Amiga-style disk sets from `(Disk n of m)` `.d64` releases with **All disks** and a generated playlist; `.m3u` files aren't ingested (user)
- 2026-11-25 DAT? → libretro No-Intro `Commodore - Plus-4` (5 `.bin` carts, no metadat), so `.bin` joins the requested formats (user)
- 2026-11-25 Header? → `C64Header` also reads VICE's `C16-TAPE-RAW` tape; Plus/4 reuses it (user)
- 2026-11-25 Core files, extensions and set limit → `@emulatorjs/core-vice_xplus4@4.2.3` packed and hashed 2026-11-25; its `valid_extensions` equal `vice_x64sc`'s; no threads; vice-libretro `DC_MAX_SIZE` is 20 (code)
- 2026-11-25 Reviewer round 1: tape test above the 256 B floor with a header assertion; unused seed 2711; the cross-system 404 test is unbuildable (`RomRow.SystemId` comes from the game), replaced by an external-disk 404; `FindDisks` caps by its own system; `PlayDisc.AmigaSystemId` removed; CSS in Prettier form (reviewer)
- 2026-11-25 Reviewer R3: Review renames Plus/4 disks, so Amiga's name-based key would rarely group → Plus/4 groups tokened releases by `m` alone within a game; supported path is Manual (`Disk 1 of m`) then Attach → Release (user)
- 2026-11-25 Reviewer round 2: Manual joins the game owning the base title (or creates it; `AmbiguousGroup` if several), so Manual works for every disk; `keyByName` derived from the system id (reviewer)
- 2026-11-25 Reviewer round 3: the features.md Review-path sentence pinned; review stopped after 3 rounds with nothing open (reviewer)
- 2026-11-25 Deep review: zip-format's `system` list and features' intro, Play and families lists have no PET, so Plus/4 goes after Commodore 64 there; `.g64` dropped from the Plus/4 header text (not a Plus/4 type); the deployment upgrade note mirrors VIC-20's and follows PC-FX's; Dat/Hack/Translated Review naming for disks stated; every step-5 doc edit, the `LibraryEndpoints` and vendor-script comments and the Review tests' bytes, drop path and Manual body pinned (reviewer)
- 2026-11-25 Deep review pass 2: pinned the Plus/4 detail test's names, `FloppyGame`'s ZIP path and seeds, the `playDisks` core parameter and expected requests, the game-page case's URL, the `setMedia`/`discError` test lines, and `play.js`'s `diskSetCores` import (reviewer)
- 2026-11-25 Deep review pass 3: B9 gains the 401 and `(HTTP <status>)` texts `operatorCoreError` gives; the deployment note says only `ingest/plus4/` is created at startup, since `SystemStartupTests` pins that `roms/<id>/` isn't (reviewer)
- 2026-11-25 Deep review pass 4: step 1's test inputs pinned (the `.bin` seed, a native `C264 CARTRIDGE  ` `.crt` as C128's test does, `C16Tap_ReadsFormatAndVersion`'s shape); every other test that walks all registered systems derives its list from the registry (reviewer)
- 2026-11-25 e2e? → none: the e2e stack has no VICE core to play with, the DAT/Review flows are covered by existing e2e, and sets by the Amiga API and vitest cases this spec mirrors (code)
- 2026-11-25 Step 2: IGDB's platform page refuses automated fetches (403), so the id 94 couldn't be checked; kept as specced, the maintainer to confirm (code)

## Kickoff prompt
```text
/spec implement 2026-11-25-commodore-plus4-support.md
```
Starts the spec, or resumes it at the first unticked step.
