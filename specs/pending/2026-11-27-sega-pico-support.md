---
title: Sega Pico support
status: ready
created: 2026-11-27
---

# Sega Pico support

## Summary
Adds Sega Pico (`pico`) as a cartridge system in `RnRomManager.Systems.Sega`, identified against libretro's No-Intro `Sega - PICO` DAT and played in the browser on PicoDrive. The operator already sets up PicoDrive for 32X, so it needs no new option. This also closes the browser-play gap where PicoDrive lists `.pco`, but no system ingests it.

## Out of scope
- Storyware (the picture-book pages) and a pen overlay: EmulatorJS 4.2.3 shows the core's video only. The pen is driven from the gamepad as PicoDrive maps it (P6).
- Pico's ADPCM sound chip quirks, Sega Beena and Advanced Pico Beena (different hardware, no DAT here).
- A seeded Pico game and an e2e Play test: 32X already covers PicoDrive end to end, and with no seed game the e2e library menus and counts don't change (only the missing-core message assertion does, step 4).
- Re-homing Pico ROMs a user may have dropped in `ingest/md/` before this change: they stay where Review or ingest put them.

## Behaviour

**System**
- **P1** `SegaPicoHandler` (new, `src/RnRomManager.Systems.Sega/SegaPicoHandler.cs`), built like `Sega32xHandler`: `Id` `pico`, `DisplayName` `Sega Pico`, `ShortName` `Pico`, `Family` `pico`, `RomExtensions` `[".md", ".bin", ".pco"]` (`.md` first: the DAT's extension, used when Review renames), `MinRomSizeBytes` 8,192 (as `MegaDriveHandler`), `MaxRomSizeBytes` 4,194,304 (the DAT's largest, 4 MiB), `DatName` `Sega - PICO`, `DatFolder` `no-intro` (default), `MetadatKinds => []` (libretro has no `releaseyear`, `developer`, `publisher` or `genre` file for Pico; read 2026-11-27, only `no-intro`, `tosec` and `franchise` have one), `EmbedRomsInZip => true`, `KeepsSourceExtension` false (default), `BrowserCore => "picodrive"` (from step 4), box art `BoxArt.Lazy(typeof(SegaPicoHandler), "boxart.pico.png")`.
- **P2** Header: `ReadHeader(rom) => MegaDriveHeader.Read(rom, Id)`, as 32X does: the `SEGA` header at 0x100 (real Pico carts say `SEGA PICO`) is optional. The fields and classification match Mega Drive's (always `pico`). No new header code.

**Ingest**
- **P3** Pico ROMs go in `ingest/pico/`. Ingest picks the system by folder, so `.md`/`.bin` don't collide with Mega Drive or 32X. A Pico ROM in `ingest/md/` matches nothing in the Mega Drive DAT and goes to Review as `md`, as a 32X ROM there does today. A `.pco` in `ingest/md/` or `ingest/s32x/` fails `UnsupportedType`. A `.32x` in `ingest/pico/` fails the same way.
- **P4** A DAT match is stored under the DAT's name (`.md`). An unmatched file goes to Review (`NoDatMatch`) with the header title as a suggestion, and a manual resolve stores it as `.md` (`RomExtensions[0]`), keeping the source name in `originalFileName`.

**Play**
- **P5** Pico games play on PicoDrive, through the same operator-core path as 32X: a `HEAD /api/emulator/cores/picodrive` check, files served from `Emulator:PicodriveCorePath`, and the core chooser hidden (`hidesCoreChooser('picodrive')` is already true). No BIOS.
- **P6** Controls: `controlSchemeFor('picodrive', name)` returns `'segaMD'` when `name` ends in `.md` (any case), else `'sega32x'`. Every stored Pico release ends in `.md` (P4), and a 32X release never does: `s32x` doesn't ingest `.md`, and DAT matches and Review store `.32x`. This works like the `.gg` rule for `smsplus`. PicoDrive maps the pen to the D-pad and its buttons to the pad (unverified with a real dump; Risks).
- **P7** The missing-core message for PicoDrive becomes `32X and Pico games need the PicoDrive core files: ask an Admin to set them up (Emulator:PicodriveCorePath).` (`operatorCores.picodrive.system`: `'32X games'` → `'32X and Pico games'`).

**Presentation**
- **P8** System colour `#94d82d` (lime) with black text (`foreground: '#000'`, about 12:1, AA; clearly apart from NES's `warning` yellow and Neo Geo Pocket Color's gold), the same in both themes. No icon mapping: the controller fallback applies, as for 32X.
- **P9** IGDB platform `339` (`Sega Pico`).
- **P10** Systems tab emulator links (`systemEmulators.json` `"pico"`): RetroArch only: `{ "name": "RetroArch", "url": "https://www.retroarch.com/?page=platforms", "os": ["windows", "macos", "linux", "android", "ios"], "note": "RetroArch core: PicoDrive" }`.
- **P11** README supported-systems row, between `Sega CD` and `Sufami Turbo` (sorted by display name): `` | Sega Pico | `pico` | In ZIP | `.md`, `.bin`, `.pco` | No-Intro | `picodrive` | `` (Play cell `—` until step 4).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | `src/RnRomManager.Systems.Sega/SegaPicoHandler.cs` (new), `Resources/boxart/pico.svg` (new), `Resources/boxart/pico.png` (new, 512×512), `Resources/boxart/README.md` | P1, P2; box art made for this project (MIT), exported as the README describes. The csproj's `Resources/boxart/*.png` glob embeds it |
| Api | `src/RnRomManager.Api/AppHost.cs` | `services.AddSystemHandler<SegaPicoHandler>();` after `WonderSwanColorHandler` (the `RnRomManager.Systems.Sega` using and project reference already exist) |
| Core | `src/RnRomManager.Core/Media/IgdbPlatforms.cs` | `["pico"] = 339,` between `["pet"]` and `["plus4"]` |
| Web | `src/app.css` | after the `wsc` block: `/* Sega Pico system colour (Sega Pico P8): lime, same in both themes; black on it is AA (~12:1). */`, `--bs-pico: #94d82d;`, `.text-bg-pico` with `color: #000` |
| Web | `src/lib/systemColours.ts`, `src/lib/types/SystemColour.ts` | `pico: { name: 'pico', foreground: '#000' }` after `wsc`; `\| 'pico'` after `\| 'wsc'` |
| Web | `src/lib/systemEmulators.json` | P10, between `"pet"` and `"plus4"` |
| Web | `static/emulator/params.js` | P6 in `controlSchemeFor` (and its doc comment: "Sega Pico P6: a `.md` on PicoDrive is a Pico release and gets the Mega Drive scheme"); P7 |
| Test data | `tests/RnRomManager.TestData/IngestSeeder.cs` | `public const string PicoDat = "Sega - PICO";` after `WonderSwanColorDat`; `PicoDat` after `Sega32xDat` in `EmptyDats`; `Directory.CreateDirectory(Path.Combine(ingest, "pico"));` after the `wsc` one; `[PicoDat] = []` at the end of the `FixtureDatWriter.Write` dictionary. Not added to `RedumpLayouts` (No-Intro layout) |
| Tests (new) | `tests/RnRomManager.Systems.Sega.Tests/SegaPicoHandlerTests.cs` | P1, P2, box art |
| Tests (existing) | every manifest listing `IngestSeeder.WonderSwanColorDat` (19 files under `tests/RnRomManager.Api.Tests/Integration/`, § Steps 2), `DatEndpointsTests`, `IngestSeederTests`, `SystemStartupTests`, `IngestEndpointsTests`, `LibraryEndpointsTests`, `IngestPipelineTests`, `BrowserCoreExtensionsTests`, `IgdbPlatformsTests`, `SystemEmulatorsCatalogTests`, `SupportedSystemsTableTests` (via README), `systemColours.test.ts`, `emulatorParams.test.ts`, `emulatorPlay.test.ts`, `tests/e2e/specs/mvp.spec.ts` ("Play: 32X explains its missing PicoDrive core": expected text becomes P7's) | see Steps |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/configuration.md`, `docs/development.md`, `docs/adding-a-system.md` | last step |

No migrations (`system_id` is a `VARCHAR`) and no config keys. API: `GET /api/systems` and `GET /api/admin/dats` each gain one row (additive). Existing data is untouched.

## Risks
- **Unverified boot:** no real Pico dump is used. PicoDrive detects Pico mode from the `SEGA PICO` header. If EmulatorJS 4.2.3's PicoDrive build mishandles Pico input or video, games still store and catalogue correctly, and Play works only as far as the core allows. features.md says so (step 5).
- **IGDB id:** 339 is IGDB's `Sega Pico` platform as far as the author knows. Step 2 checks it against IGDB's `platforms` endpoint (`fields id,name; where name = "Sega Pico";`) when IGDB credentials are at hand, and corrects P9 and § Decisions if it differs. Without credentials, commit 339 and list it as unverified in the PR body.
- **Upgrade:** like every new system, ingestion refuses to start with `DatsMissing` until an Admin runs **Refresh DATs** (deployment.md note, step 5).
- **Parallel specs:** other pending specs also add systems and touch the same lists and counts (`AppHost`, `IngestSeeder`, `DatEndpointsTests`, `SystemStartupTests`, `BrowserCoreExtensionsTests.NotIngested`). Re-read every count before editing. Merge conflicts in those files are positional, not semantic.

## Steps
Steps 2–4 run integration tests (category `Integration`), which need Docker.

- [ ] **1. Handler and box art** (P1 without `BrowserCore`, P2): `SegaPicoHandler`, `pico.svg`/`pico.png`, box-art README (both lists gain `pico.svg`/`pico.png`). `BrowserCore` stays null; not registered yet.
  - Files: `src/RnRomManager.Systems.Sega/SegaPicoHandler.cs`, `Resources/boxart/*`, `tests/RnRomManager.Systems.Sega.Tests/SegaPicoHandlerTests.cs`.
  - Tests: `SegaPicoHandlerTests` (new, as `Sega32xHandlerTests`, builder `SyntheticMegaDriveRomBuilder { Seed = 1971, Size = 524288, SystemType = "SEGA PICO" }`): `SegaPico_Contract` (every P1 value), `BrowserCore_IsNull_WithEmbeddedRoms` (renamed to `BrowserCore_IsPicodrive_WithEmbeddedRoms` in step 4), `ReadHeader_ReadsTheMegaDriveFields_ClassifiedPico` (`systemType` `SEGA PICO`), `ReadHeader_MegaDriveSystemType_IsStillClassifiedPico`, `ReadHeader_NoSegaOrTooShort_IsNull`, `NormalizeForHashing_ReturnsTheInputUnchanged`, `DefaultBoxArt_IsA512x512Png_DifferentFromMegaDrives`.
  - Done when: `dotnet test tests/RnRomManager.Systems.Sega.Tests` passes and `dotnet build` passes with warnings as errors.
- [ ] **2. Registration and existing tests** (P8–P11 with Play `—`): `AppHost.cs`, `IgdbPlatforms.cs` (verify the id if possible; Risks), `systemEmulators.json`, `app.css`, `systemColours.ts`, `SystemColour.ts`, README row, `IngestSeeder` (§ Changes).
  - Every test file that lists `IngestSeeder.WonderSwanColorDat` in a manifest (grep `tests/`; today `AuditLibraryEditTests`, `DatEndpointsTests`, `GamePlayedEndpointTests`, `ImageEndpointsTests`, `ImageUrlEndpointsTests`, `IngestArchiveTests`, `IngestDeleteEndpointsTests`, `IngestEndpointsTests`, `IngestPipelineTests`, `IngestRestoreEndpointsTests`, `IngestReviewTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `NoteEndpointsTests`, `ReportEndpointsTests`, `ReviewEndpointsTests`, `ZipEntryEndpointsTests`) adds `[IngestSeeder.PicoDat] = []` right after it. Before committing, check that every manifest listing `WonderSwanColorDat` also lists `PicoDat`.
  - Counts and orders (today's values; re-read first): `DatEndpointsTests` names 46 → 47 (message "33 libretro systems, …"); the ordinal admin-rows list gains `IngestSeeder.PicoDat` between `IngestSeeder.SegaCdDat` and `IngestSeeder.SaturnDat`; refresh requests 152 → 153 (the message's "1 for …" list gains "Sega Pico" after "Commodore Amiga"). `IngestSeederTests` `*.dat` count 162 → 167 (the fixture's No-Intro layout writes the main DAT plus 4 metadat files). `SystemStartupTests` both id arrays end `"wsc", "pico"`, and so does `IngestEndpointsTests.Pending_ListsEverySystem_IgnoringJunk`. `LibraryEndpointsTests.Systems_ListsEveryRegisteredSystem_WithCountsAndSortedRegions`: the id array gains `"pico"` between `"segacd"` and `"sufami"`; `Has.Exactly(31).True` → `32`; after the `wsc` displayName/shortName assert, `Find("pico")` → `("Sega Pico", "Pico")`.
  - Tests: the files above, `IngestSeederTests`, `SeedIngestTests` (counts unchanged), `SystemStartupTests`, `SupportedSystemsTableTests`, `IgdbPlatformsTests`, `SystemEmulatorsCatalogTests`, `systemColours.test.ts` (new `it('gives Sega Pico lime with black text (Sega Pico P8)', …)` after the WonderSwan case: `systemColour('pico')` → `{ name: 'pico', foreground: '#000' }`); web `npm run lint`, `npm run check`, `npx vitest run src/lib/systemColours.test.ts`.
  - Done when: those pass (Docker running).
- [ ] **3. Ingest behaviour** (P3, P4), in `IngestPipelineTests`. `Pals` = `new SyntheticMegaDriveRomBuilder { Seed = 1972, Size = 65536, SystemType = "SEGA PICO", OverseasTitle = "PEN PALS" }.Build()` as a static field beside `Relay`; the manifest's `[IngestSeeder.PicoDat] = []` becomes `[new FixtureGame("Pen Pals (Japan)", "Pen Pals (Japan).md", Pals)]`. New tests after `Sega32x_DatMatchedBin_IsStoredAs32x`:
  - `Pico_DatMatchedPco_IsStoredUnderPicoAsMd_WithItsHeader`: `Pals` as `ingest/pico/pals.pco` → `Ingested`, `pico/P/Pen Pals.zip`, `(System, FileName)` `("pico", "Pen Pals (Japan).md")`, header `systemType` `SEGA PICO`.
  - `Pico_UnmatchedHeadered_GoesToReview_WithTheHeaderTitle_AndManualIsStoredAsMd`: builder `{ Seed = 1973, Size = 65536, SystemType = "SEGA PICO", OverseasTitle = "PEN PALS" }` as `ingest/pico/mystery.pco` → suggestions contain `Pen Pals`; `ResolveGameGearAsync(item, GameGearManual("Pen Pals Proto"))` → `("Pen Pals Proto (World).md", "mystery.pco")`.
  - `SegaFile_InTheWrongFolderOrUnsupported_IsUnsupportedType` gains `[TestCase("pico", "relay.32x")]`, `[TestCase("md", "pals.pco")]` and `[TestCase("s32x", "pals.pco")]` (its body picks `Comet` for any name not ending `.sms`, fine for a type check).
  - `Sega_OutsideTheSizeLimits_Fails` gains `[TestCase("pico", "odd.md", 8191, "TooSmall")]` and `[TestCase("pico", "odd.md", 4194305, "TooLarge")]`.
  - Done when: `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~IngestPipelineTests"` passes (Docker running).
- [ ] **4. Play on PicoDrive** (P1 `BrowserCore`, P5–P7, P11 Play cell): `BrowserCore => "picodrive"` with a doc comment ("Sega Pico P5: PicoDrive, whose files the operator supplies for 32X too"); `params.js` P6 and P7; README Play cell `` `picodrive` ``.
  - `BrowserCoreExtensionsTests.NotIngested`: `(OtherSystem, ["picodrive"], […])` drops `.md` (now ingested by `pico`), and `(NewSystem, ["picodrive"], [".sc", ".pco"])` becomes `[".sc"]`. No `Exceptions` entry is needed (PicoDrive's list holds `md`, `bin` and `pco`).
  - Tests: `SegaPicoHandlerTests` (`BrowserCore_IsPicodrive_WithEmbeddedRoms`); `BrowserCoreExtensionsTests`; `LibraryEndpointsTests` (`[TestCase("pico", false, true, "picodrive")]` after `Games_QuickPlay_IsThePrimaryReleaseWhenPlayable`'s `s32x` case, `[TestCase("pico", "picodrive")]` after `GameDetail_BrowserCore_IsTheSystemHandlers`'s `s32x` case); `SupportedSystemsTableTests`; `emulatorParams.test.ts` (in `describe('32X (B3)')`, the `uses the 32X controls for %s` list drops `'X.md'` and gains `'X.bin'`; new `it.each(['Pen Pals (Japan).md', 'X.MD'])('uses the Mega Drive controls for the Pico release %s (Sega Pico P6)', …)` → `'segaMD'`; the missing-core message assertion becomes P7's text); `emulatorPlay.test.ts` (the 32X case's expected 404 message becomes P7's text); `tests/e2e/specs/mvp.spec.ts` ("Play: 32X explains its missing PicoDrive core": the expected message becomes P7's text; runs in Finish).
  - Done when: `npm run lint`, `npm run check`, `npx vitest run src/lib/emulatorParams.test.ts src/lib/emulatorPlay.test.ts` and `npx tsc --noEmit` in `tests/e2e` pass; `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~BrowserCoreExtensionsTests|FullyQualifiedName~SupportedSystemsTableTests|FullyQualifiedName~LibraryEndpointsTests"` (Docker running) and `dotnet test tests/RnRomManager.Systems.Sega.Tests` pass.
- [ ] **5. Docs**:
  - `README.md`: the Play list (line 15) gains `Sega Pico` after `32X`, and the same line's operator-core sentence "Genesis, 32X, SNES, …" becomes "Genesis, 32X, Sega Pico, SNES, …"; the upgrade note (line 140) gains `Sega Pico`; attribution (line 179) `PicoDrive (32X)` → `PicoDrive (32X and Sega Pico)`.
  - `docs/features.md`: the systems list (line 3) gains Sega Pico after 32X; the Play list (line 154) gains `Sega Pico` after `32X`; a Play sub-bullet after the 32X one (line 165): "**Sega Pico** also uses PicoDrive (the 32X setup turns both on) with the Mega Drive gamepad: the pen follows the D-pad as PicoDrive maps it. Storyware pages aren't shown, and booting a real dump hasn't been verified."; the ingestion intro's folder list gains `ingest/pico/` after `ingest/s32x/`; in the line-228 ingest paragraph, "Interleaved `.smd` dumps, Mega-CD and Pico aren't supported." → "Interleaved `.smd` dumps and Mega-CD aren't supported.", the separate-families list gains "Sega Pico" after "32X", and the size limits gain "8 KiB–4 MiB for Sega Pico" after "16 KiB–4 MiB for 32X"; an ingest bullet after the 32X one: "Drop `.md`, `.bin` or `.pco` files (or archives of them) into `ingest/pico/`: they're identified against the No-Intro `Sega - PICO` DAT and stored as `.md`. The `SEGA` header at 0x100 is optional, as for 32X. ROMs are 8 KiB to 4 MiB."
  - `docs/zip-format.md`: `pico/                 Sega Pico (.md, .bin, .pco)` after the `s32x/` line; `pico` after the last id of the `system` list; after the 32X `header` line: "Sega Pico `header`: the same fields and rules as 32X (system type such as `SEGA PICO`)."
  - `docs/deployment.md`: the line-240 parenthesised list of systems gains "Sega Pico" after "32X"; the `/app/ingest` row gains `` `ingest/pico/` (Sega Pico: `.md`, `.bin`, `.pco`) `` after the `ingest/s32x/` entry; line 423 "32X play needs" → "32X and Sega Pico play need"; an upgrade section in the form of the WonderSwan one: "Upgrading to the release that adds Sega Pico:" with "- **Refresh DATs after upgrading.** Pico games are identified against libretro's No-Intro `Sega - PICO` DAT (there's no metadat for it). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `ingest/pico/` is created at startup." and "- Browser play uses the PicoDrive files you already set up for 32X; nothing else is needed."
  - `docs/configuration.md`: line 28 "(32X play off)" → "(32X and Sega Pico play off)"; line 53 "32X plays with PicoDrive … To turn 32X play on" → "32X and Sega Pico play with PicoDrive … To turn their play on".
  - `docs/development.md`: the `RnRomManager.Systems.Sega` row (line 34) gains `pico` ("`md`, `s32x` and `pico` (all read the optional `SEGA` header at 0x100 through the shared `MegaDriveHeader`)"); the operator-cores bullet (line 136) heading list gains "Sega Pico" after "32X"; the play-page bullet (line 130) "`picodrive` (32X)" → "`picodrive` (32X and Sega Pico)".
  - `docs/adding-a-system.md`: the `BrowserCore` row (line 61) gains "`pico` returns `picodrive` (operator-supplied, shared with `s32x`)"; the `DefaultBoxArtPng` row (line 62) list gains "/Sega Pico" after "/32X"; line 103 "PicoDrive for `s32x`" → "PicoDrive for `s32x` and `pico`".
  - Tests: none. Done when: every item is in its doc, `SupportedSystemsTableTests` passes and `pwsh scripts/codemap.ps1` is clean.

## Decisions
- 2026-11-27 Which systems and types does this cover? → Sega Pico on PicoDrive, as one of seven specs from the browser-play gap audit (user)
- 2026-11-27 Id, names, family → `pico`, `Sega Pico`, `Pico`, own `pico` family. "Pico" alone could be read as PICO-8 (code: libretro has a `PICO-8` DAT)
- 2026-11-27 Extensions → `.md` (the DAT's only extension, so it comes first), `.bin` (the house alias for Sega carts), `.pco` (PicoDrive's own) (code)
- 2026-11-27 Metadat → none: libretro has no `releaseyear`/`developer`/`publisher`/`genre` file for `Sega - PICO` (code)
- 2026-11-27 Size limits → 8 KiB (as Mega Drive) to 4 MiB (the DAT's largest entry; 339 games, 512 KiB to 4 MiB, DAT 2026.11.14) (code)
- 2026-11-27 Header → reuse `MegaDriveHeader.Read` as 32X does; no `SEGA PICO` check (code)
- 2026-11-27 Control scheme → `segaMD` for a `.md` release on PicoDrive, else `sega32x`; the play page knows only the core and name, as with the `.gg` rule (code)
- 2026-11-27 New core option? → No: Pico reuses `Emulator:PicodriveCorePath` (user, in the brief)
- 2026-11-27 Seed game and e2e Play? → No: 32X covers PicoDrive end to end, and no seed keeps every e2e count unchanged (code)
- 2026-11-27 Review pass 1: e2e missing-core assertion named; features.md line 228 (Pico was "not supported", families, sizes), README/development/adding-a-system/deployment lines added; colour yellow → lime `#94d82d` (NES uses `warning` yellow); IGDB fallback without credentials pinned (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-27-sega-pico-support.md
```
Starts the spec, or resumes it at the first unticked step.
