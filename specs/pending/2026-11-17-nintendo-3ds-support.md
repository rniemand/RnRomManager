---
title: Nintendo 3DS support
status: ready
created: 2026-11-17
---

# Nintendo 3DS support

## Summary
Admins can import Nintendo 3DS dumps (`.3ds`, `.cci`, `.cia`) into the library. They're identified by SHA1 against libretro's No-Intro `Nintendo - Nintendo 3DS` DAT and stored as files next to the game's ZIP, like Nintendo DS and Switch, since 3DS dumps reach 4 GiB. There is no browser play: EmulatorJS 4.2.3 ships no 3DS core.

## Out of scope
- **Browser play.** No EmulatorJS core exists for 3DS (checked 2026-11-17: the 4.2.3 system→core map, the `@emulatorjs/core-*` packages on npm, no Citra/Azahar/Lime3DS). `BrowserCore` stays null, so no Play button. A later spec can add it if a core appears.
- Serial (product-code) matching for unmatched dumps. Decrypted or trimmed dumps never hash-match the DAT's encrypted dumps and go to Review.
- Decrypting, encrypting, trimming or converting dumps (`.3ds` ↔ `.cia`), and `.cxi`, `.3dsx`, `.app`, `.bcmdl` (the DAT's one `.bcmdl` entry is filtered out).
- Renaming `.cci` to `.3ds`: an external system keeps the source extension (see Decisions).
- Reading the title from the SMDH (it's inside the encrypted ExeFS), so `RomHeader.Title` is null.
- Moving any existing data: `3ds` is a new system with no legacy games (`LegacyEmbeddedRoms` false).

## Behaviour
- **B1 Handler.** `Nintendo3DsHandler` in `RnRomManager.Systems.NintendoDs` (no new project): `Id` `3ds`, `DisplayName` `Nintendo 3DS`, `ShortName` `3DS`, `Family` `3ds` (its own; DS files are never eligible), `RomExtensions` `[".3ds", ".cci", ".cia"]`, `MinRomSizeBytes` 16,384 (16 KiB, room for small homebrew `.cia`), `MaxRomSizeBytes` 4,294,967,296 (4 GiB inclusive, the DAT's largest entry), `DatName` `Nintendo - Nintendo 3DS`, `DatSource` Libretro, `DatFolder` `no-intro`, `MetadatKinds` `[]` (libretro has no 3DS metadat: every `metadat/<kind>/Nintendo - Nintendo 3DS.dat` 404s, so year/developer/publisher/genre are empty, as for Commodore 64), `EmbedRomsInZip` false, `FileSets` false, `LegacyEmbeddedRoms` false, `KeepsSourceExtension` false (irrelevant: external systems always keep the source extension), `BrowserCore` null, identity `NormalizeForHashing`.
- **B2 Ingest.** Files come from `ingest/3ds/` (created at startup). Matching is case-insensitive on the extension. A DAT match (by SHA1) is stored next to the ZIP under the DAT stem with the source file's extension lower-cased (existing external naming, `zip-format.md`). The DAT's `.3ds` and `.cia` entries are both eligible (its `.bcmdl` entry is filtered by B1's extensions). Out-of-range sizes fail `TooSmall`/`TooLarge`; other extensions fail `UnsupportedType`. Input archives keep today's caps (100 entries, 256 MiB combined extracted), so most 3DS dumps must be dropped loose; an over-cap archive fails `ArchiveUnsafe` as today.
- **B3 Header** (`Nintendo3DsHeaderReader.Read(span)`, given at most the first MiB; never throws; every offset computed in `long` and bounds-checked before slicing):
  - Span shorter than 0x200 bytes → null.
  - First little-endian `u32` is `0x2020` (a CIA header size) → null. An unmatched `.cia` therefore goes to Review, never `InvalidHeader`.
  - `NCSD` (ASCII) at 0x100 → `RomHeader("3ds", Title: null, IsPlausible: true, Fields)` with `format` `"NCSD"`, `mediaId` (u64 LE at 0x108, 16 upper-case hex digits), and, when partition 0's offset (u32 LE at 0x120, × 0x200) plus 0x200 lies inside the span and `NCCH` is at offset + 0x100: `productCode` (16 bytes at +0x150, ASCII up to the first NUL, trailing spaces trimmed, null when empty), `makerCode` (2 bytes at +0x110) and `programId` (u64 LE at +0x118, 16 upper-case hex digits); otherwise those three are null. Text fields keep bytes 0x20–0x7E and turn others into `?`, like the DS reader.
  - Anything else → `RomHeader("3ds", null, IsPlausible: false, { format: null })`, so an unmatched non-CIA file without `NCSD` is `InvalidHeader`; a DAT match still wins.
- **B4 Box art.** A drawn 512×512 `3ds.png` (with its `3ds.svg` source) in the project's `Resources/boxart/`, loaded as `boxart.3ds.png`; libretro-thumbnails folder `Nintendo - Nintendo 3DS` is used as for any libretro system.
- **B5 UI.** The system appears everywhere handlers are enumerated (filters, `/api/systems` sorted by `DisplayName`, Systems tab). Its badge colour is a new `--bs-3ds` (`#f783ac`, a light pink no other system uses, clearly apart from N64's `danger` red next to it in sorted lists; black text, AAA at ≥ 7:1), same in both themes, with `.text-bg-3ds` in `app.css` and an entry in `systemColours.ts` (key quoted: `'3ds'`, `foreground: '#000'`), plus `'3ds'` added to the `SystemColour.name` string-literal union. Icon: no mapping (controller fallback). `systemEmulators.json` gets `"3ds"`: Azahar (`https://azahar-emu.org/`, windows/macos/linux/android).
- **B6 Docs.** README supported-systems row: `| Nintendo 3DS | \`3ds\` | Next to ZIP | \`.3ds\`, \`.cci\`, \`.cia\` | No-Intro | — |` (exact form per `SupportedSystemsTableTests`), plus the intro sentence about systems stored next to the ZIP and the upgrade "Refresh DATs" list. `docs/zip-format.md`: folder tree line, external-systems list, `system` id list, and a `Nintendo 3DS header` paragraph (B3). `docs/features.md`: systems list (line 3), the `/ingestion` ingest-folder list, and the external-storage section (title and body). `docs/deployment.md`: the `/app/ingest` folder row, an "Upgrading to the release that adds Nintendo 3DS" block (Refresh DATs after upgrading, as Amiga's), and the "External ROM storage" section's title and body. `docs/development.md`: the `src/RnRomManager.Systems.NintendoDs` project-table row names `3ds` too. `docs/adding-a-system.md`: `DatFolder`/`MetadatKinds` example list, the box-art list, project list (`3ds` lives in `RnRomManager.Systems.NintendoDs`).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core (system) | `src/RnRomManager.Systems.NintendoDs/Nintendo3DsHandler.cs`, `Nintendo3DsHeaderReader.cs`, `Resources/boxart/3ds.{svg,png}`, `Resources/boxart/README.md` | New handler, reader, box art (B1, B3, B4). The csproj already embeds `Resources/boxart/*.png`. |
| Api | `src/RnRomManager.Api/AppHost.cs` | `services.AddSystemHandler<Nintendo3DsHandler>();` after the Amiga line (project already referenced). |
| Tests | `tests/RnRomManager.Systems.NintendoDs.Tests/Nintendo3DsHandlerTests.cs`, `Nintendo3DsHeaderReaderTests.cs` | Synthetic NCSD/NCCH/CIA buffers; no real ROMs. |
| Test data | `tests/RnRomManager.TestData/IngestSeeder.cs` | `ThreeDsDat = "Nintendo - Nintendo 3DS"`, `ingest/3ds` folder, `[ThreeDsDat] = []` in the fixture DAT map (No-Intro layout, no metadat). |
| Tests (fixtures) | Every integration test that lists all systems' DATs or system ids (the files Amiga touched: `DatEndpointsTests`, `LibraryEndpointsTests`, `SystemStartupTests`, `IngestEndpointsTests`, `ReviewEndpointsTests`, `IngestReviewTests`, and the other `Integration/*Tests.cs` with a `[AmigaDat] = []` line), `IngestSeederTests`, `tests/e2e/specs/mvp.spec.ts` | Add `3ds` / `ThreeDsDat`; bump counts (registry +1, DAT rows +1, DAT requests +1, embedded count unchanged). |
| Web | `src/RnRomManager.Web/src/app.css`, `src/lib/systemColours.ts`, `src/lib/systemColours.test.ts`, `src/lib/systemEmulators.json` | B5. |
| Docs | `README.md`, `docs/zip-format.md`, `docs/features.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md` | B6. |

No migration (`system_id` is a `VARCHAR`), no config keys, no API contract change beyond the new system in existing lists. No new vendored core or licence text.

## Risks
- **Large files.** 4 GiB dumps are hashed in one streamed read like Switch/DS (up to 64 GiB already supported); no new memory pressure since `ReadHeader` gets 1 MiB.
- **Most dumps go to Review** if users have decrypted dumps (common from GodMode9 "decrypt" or Citra-era tooling). Accepted; B3's `productCode` and `mediaId` are shown in Review to help pick the DAT entry.
- **Fixture churn.** Registering a handler breaks every test that enumerates systems/DATs until updated; step 2 does both in one commit.

## Steps
- [ ] **1. Handler, header reader and box art**: B1, B3, B4, not yet registered. Files: `src/RnRomManager.Systems.NintendoDs/Nintendo3DsHandler.cs`, `Nintendo3DsHeaderReader.cs`, `Resources/boxart/3ds.svg`, `3ds.png`, `Resources/boxart/README.md`. Tests: `Nintendo3DsHeaderReaderTests` (short span → null; CIA `0x2020` → null; NCSD + NCCH at 0x4000 → all fields, plausible; NCSD with partition offset outside the span → `productCode`/`makerCode`/`programId` null, still plausible; NCSD with huge partition offset (overflow) → no throw; no magic → not plausible; non-printable product-code bytes → `?`), `Nintendo3DsHandlerTests` (B1 property values; box art is a valid 512×512 PNG). Done when: both classes pass; `dotnet build` with warnings as errors is clean.
- [ ] **2. Register the system**: B2, B5's `systemEmulators.json`, B6's README table row. Files: `AppHost.cs`, `IngestSeeder.cs`, the integration/e2e fixtures in § Changes, `systemEmulators.json`, `README.md` (table row only). Tests: `SystemStartupTests`, `IngestSeederTests`, `DatEndpointsTests`, `LibraryEndpointsTests`, `IngestEndpointsTests`, `ReviewEndpointsTests`, `IngestReviewTests`, `SupportedSystemsTableTests`, `SystemEmulatorsCatalogTests`, plus a new `IngestPipelineTests` case: a synthetic `.3ds` whose SHA1 is in a fixture `ThreeDsDat` entry is stored next to the ZIP with its DAT stem and `.3ds` extension, and a synthetic unmatched `.cci` without `NCSD` fails `InvalidHeader`, and an unmatched `.cia` (CIA header) goes to Review. Done when: those classes pass (Docker running); `LibraryEndpointsTests` asserts `/api/systems` lists `3ds` (`Nintendo 3DS`, `3DS`) with `embedRoms` false; the README row's Play column is `—` (no Play, also covered by step 1's `BrowserCore` null test).
- [ ] **3. Web colour**: B5's colour. Files: `app.css`, `systemColours.ts`, `systemColours.test.ts`. Tests: `systemColours.test.ts`; `npm run lint`, `npm run check`. Done when: `'3ds'` is in the `SystemColour.name` union and the colour map; `.text-bg-3ds` gives black on `#f783ac` in both themes; lint, check and the test pass.
- [ ] **4. Docs**: B6 except the README row (done in step 2). Files: `README.md`, `docs/zip-format.md`, `docs/features.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md`. Tests: none (docs). Done when: every B6 item (README intro + upgrade list; zip-format tree, external list, id list, header paragraph; features systems list, ingest folders, external storage; deployment ingest row, upgrade block, external storage; development project row; adding-a-system lists) is present and `SupportedSystemsTableTests` still passes.

## Decisions
- 2026-11-17 Browser play has no EmulatorJS 3DS core → import only, `BrowserCore` null, README Play column `—` (user).
- 2026-11-17 Formats → `.3ds`, `.cci` and `.cia` (user).
- 2026-11-17 Serial matching for decrypted/trimmed dumps → no; Review handles them (user).
- 2026-11-17 Storage → next to the ZIP (`EmbedRomsInZip` false), as requested (user).
- 2026-11-17 `.cci` is kept as `.cci`, not renamed to `.3ds`: every external system keeps the source extension (`ReviewResolver`, external naming), and renaming would need new ingest code for one alias (code).
- 2026-11-17 Lives in `RnRomManager.Systems.NintendoDs` as its own `3ds` family, reusing its box-art loader and test project (code; precedent GBA in `Systems.GameBoy`).
- 2026-11-17 Badge colour `#f783ac` with black text, not the 3DS red, which was too close to N64's `danger` red beside it in sorted lists (reviewer).
- 2026-11-17 Docs also cover `deployment.md`, `development.md` and the features ingest-folder list, as the Amiga change did (reviewer).
- 2026-11-17 Size range 16 KiB–4 GiB; `MetadatKinds` `[]` since libretro has no 3DS metadat (code: probed libretro-database 2026-11-17).

## Kickoff prompt
```text
/spec implement 2026-11-17-nintendo-3ds-support.md
```
Starts the spec, or resumes it at the first unticked step.
