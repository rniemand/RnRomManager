---
title: System sort, game info table and ColecoVision
status: in-progress
created: 2026-11-09
---

# System sort, game info table and ColecoVision

## Summary
Three changes for anyone browsing the library. (1) `GET /api/systems` returns systems sorted by display name, so the Library page's System filter (and every other system dropdown) is alphabetical. (2) The game page's information table never scrolls: long values wrap instead. (3) ColecoVision (`coleco`) joins as the eighth system, in a new `RnRomManager.Systems.Coleco` project.

## Out of scope
- Changing the registry order (`ISystemRegistry.All`): ingestion and DAT refresh keep registration order; the Admin DATs list (`GET /api/admin/dats`) keeps its existing DAT-name order.
- ColecoVision MegaCart / homebrew ROMs over 32 KiB, Coleco ADAM, Super Game Module, a dedicated `coleco` icon (the controller fallback is used) and an automatic DAT refresh after upgrading (documented manual step).
- Other tables on the game page (the release cards and the Header modal keep their current layout).

## Behaviour

### System sort
- **S1.** `GET /api/systems` returns the same items as today, ordered by `displayName` with `StringComparer.OrdinalIgnoreCase`. Resulting order: Atari 2600, ColecoVision, Game Boy, Game Boy Advance, Game Boy Color, Nintendo 64, Nintendo Entertainment System, Super Nintendo Entertainment System. Every SPA page that reads the list (Library, Collection, Review, Duplicates, Missing images, Audit) shows that order unchanged; no SPA code changes. "All systems" stays first in the Library filter.

### Game info table
- **G1.** The game page's key/value card (System, Year, Developers, Publishers, Genres) has no `table-responsive` wrapper, so it never shows a scrollbar. Value cells get Bootstrap `text-break`, so a long value (many developers, an unbroken string) wraps inside the card at any width, including 375 px. The System cell's `SystemBadge` (a `.badge`, `white-space: nowrap`) gets Bootstrap `text-wrap` there: `SystemBadge.svelte` gains an optional `class` prop appended to its class list, and the game page passes `text-wrap`, so "Super Nintendo Entertainment System" wraps too.

### ColecoVision
- **C1 Registration.** `ColecoVisionHandler`, in a new `src/RnRomManager.Systems.Coleco` project, is registered after Atari 2600. Registry order: `gb`, `gbc`, `gba`, `n64`, `nes`, `snes`, `a2600`, `coleco`.
- **C2 Contract values.** `Id` = `coleco`, `DisplayName` = `ColecoVision`, `Family` = `coleco`, `RomExtensions` = `[".col", ".rom", ".bin"]`, `MinRomSizeBytes` = `8192`, `MaxRomSizeBytes` = `32768`, `DatName` = `Coleco - ColecoVision`; `NormalizeForHashing` is the default. Facts (libretro DAT 2026.10.07, checked 2026-11-09): all five DAT files and the `Coleco - ColecoVision` thumbnails folder exist; the DAT has 179 entries, all `.col`, 8192–32768 bytes.
- **C3 Header.** `ReadHeader` reads the first two bytes. `AA 55` or `55 AA` → `RomHeader("coleco", null, true, { "magic": "AA55" | "55AA" })`. Anything else (including a file shorter than 2 bytes) → `RomHeader("coleco", null, false, { "magic": "<up to the first two bytes as upper-case hex: empty for 0 bytes, one byte's hex for 1>" })`. Never returns null.
- **C4 Ingestion.** The existing pipeline. A DAT-matched dump (any extension) is stored as `roms/coleco/<bucket>/<title>.zip` with the DAT's ROM name (`.col`); matched files are never header-checked. An unmatched file with valid magic → Review (`NoDatMatch`, suggestions from the file name, header stored); resolving it stores it with `RomExtensions[0]` (`.col`). An unmatched file with invalid magic → `_failed/` with `InvalidHeader`. Under 8192 bytes → `TooSmall`, over 32768 → `TooLarge`. A `.col` in another family's folder → `UnsupportedType`; another family's extension (e.g. `.nes`) in `ingest/coleco/` → `UnsupportedType`. `.bin` stays valid in `ingest/a2600/` too (extensions are scoped per family folder). Releases show the Header button with the `magic` field.
- **C5 Badge colour.** Coleco blue, same in both themes: `app.css` `--bs-coleco: #1d3f8f` on `:root` and `.text-bg-coleco { color: #fff !important; background-color: var(--bs-coleco) !important; }` next to the Atari rules (white on it is AA); `systemColours` gets `coleco: { name: 'coleco', foreground: '#fff' }` (the `name` union gains `'coleco'`).
- **C6 Default box art.** `coleco.svg` (drawn for this project, MIT) and its 512×512 `coleco.png` render (headless Edge, as the Atari README shows), embedded and loaded like Atari's, with a `README.md`.
- **C7 Upgrade.** Ingestion refuses with `DatsMissing` until an Admin runs **Admin → DATs → Refresh DATs** (documented).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | S1: `OrderBy(displayName, OrdinalIgnoreCase)` on `/systems` |
| Web | `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte`, `lib/components/SystemBadge.svelte` (optional `class` prop), `routes/games/game-page.test.ts` | G1 |
| Systems | new `src/RnRomManager.Systems.Coleco/` (`ColecoVisionHandler.cs`, `Resources/boxart/{coleco.svg,coleco.png,README.md}`, csproj) | C2, C3, C6 |
| Build | `RnRomManager.slnx`, `Dockerfile` (restore layer) | new src + test projects |
| Api | `AppHost.cs`, `RnRomManager.Api.csproj` | C1 |
| Web | `app.css`, `lib/systemColours.ts`, `systemColours.test.ts`, `Pagination.test.ts` (`SystemBadge`) | C5 |
| Test data | `tests/RnRomManager.TestData/IngestSeeder.cs` (`ColecoVisionDat`, `ingest/coleco/`, seed game) | C4 seed |
| Tests (new) | `tests/RnRomManager.Systems.Coleco.Tests/ColecoVisionHandlerTests.cs` | |
| Tests (existing, broken by an 8th system) | the 16 files that reference `IngestSeeder.Atari2600Dat` (`AuditLibraryEditTests`, `DatEndpointsTests`, `ImageEndpointsTests`, `ImageUrlEndpointsTests`, `IngestArchiveTests`, `IngestDeleteEndpointsTests`, `IngestEndpointsTests`, `IngestPipelineTests`, `IngestReviewTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `NoteEndpointsTests`, `ReportEndpointsTests`, `ReviewEndpointsTests`) | add `[IngestSeeder.ColecoVisionDat] = []` |
| Tests (existing, counts/order) | `DatEndpointsTests` (DAT names + `coleco`, requests 35 → 40), systems 7 → 8 and S1 order (`LibraryEndpointsTests` systems test rewritten in step 1 to check by id list, `IngestEndpointsTests`), `SystemStartupTests`, `IngestSeederTests` (DAT files 35 → 40; Basic/Full counts), `SeedIngestTests`, `tests/e2e/specs/mvp.spec.ts` | |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md` | see step 6 |

No migrations or config keys (`system_id` is a `VARCHAR`). API: `GET /api/systems` order changes (S1) and gains `coleco`; `GET /api/admin/dats` gains `coleco` (DAT-name order puts `Coleco - ColecoVision` second, after `Atari - 2600`; `DatEndpointsTests` expects that).

## Risks
- **API order change (S1):** a consumer relying on registration order from `/api/systems` sees a new order. Only the SPA consumes it and it renders the list as given; tests that index into the list are updated.
- **`.bin`/`.rom` are generic:** a stray `.bin` in `ingest/coleco/` without valid magic lands in `_failed/` (recoverable; the Ingestion page can delete it). With valid magic it goes to Review.
- **Homebrew without magic or over 32 KiB** is rejected (`InvalidHeader` / `TooLarge`). Accepted: out of scope.

## Steps
- [x] **1. System sort and game info table**: S1, G1. Files: `LibraryEndpoints.cs`, `LibraryEndpointsTests.cs` (rewrite `Systems_ListsEveryRegisteredSystem_WithCountsAndSortedRegions`: assert the id list equals the S1 order of the seven current systems, `a2600, gb, gba, gbc, n64, nes, snes`, and find `gb`/`gbc`/`a2600` by id for the count, region and display-name checks instead of indexing, so step 3 only edits the id list), `games/[id]/+page.svelte`, `SystemBadge.svelte` (optional `class` prop), `game-page.test.ts`. Tests: API returns systems in display-name order; vitest: the game information table (caption "Game information") has no `.table-responsive` ancestor, its value cells have `text-break`, and the System badge has `text-wrap`. Done when: `dotnet test tests/RnRomManager.Api.Tests` passes (Docker running), SPA lint/check/tests pass.
- [x] **2. ColecoVision handler and box art**: C2, C3, C6. New projects `src/RnRomManager.Systems.Coleco` and `tests/RnRomManager.Systems.Coleco.Tests` in `RnRomManager.slnx`, the Coleco csproj in the `Dockerfile` restore layer. Files: `ColecoVisionHandler.cs`, `Resources/boxart/*`, both csproj files, `RnRomManager.slnx`, `Dockerfile`, `ColecoVisionHandlerTests.cs` (test project references `RnRomManager.Systems.Atari` for the box-art comparison). Tests: `ColecoVision_Contract` (every C2 value); `ReadHeader`: `AA 55…` → plausible, magic `AA55`, classified `coleco`; `55 AA…` → plausible, `55AA`; `00 00…` → not plausible, `0000`; empty span → not plausible, magic empty, no exception; `NormalizeForHashing` returns the input unchanged; box art is a valid 512×512 PNG differing from Atari's. Done when: `dotnet test tests/RnRomManager.Systems.Coleco.Tests` passes, `dotnet build` passes with warnings as errors.
- [x] **3. Registration and existing tests**: C1, C5. Api project reference and `AppHost` line; `IngestSeeder.ColecoVisionDat` const, `[ColecoVisionDat] = []` in the dictionary `IngestSeeder.Seed` passes to `FixtureDatWriter.Write` (writer unchanged), `ingest/coleco/` created; `[IngestSeeder.ColecoVisionDat] = []` in the 16 files; counts (DATs 35 → 40, systems 7 → 8, startup ids/folders, `IngestSeederTests` DAT file count 35 → 40, S1 order now includes ColecoVision second); C5 (`app.css`, `systemColours.ts` + tests). Files: `AppHost.cs`, `RnRomManager.Api.csproj`, `IngestSeeder.cs`, the test files in § Changes' "broken by an 8th system" and the count files except seed counts, `app.css`, `systemColours.ts`, `systemColours.test.ts`, `Pagination.test.ts`. Tests: `systemColour('coleco')` is `{ name: 'coleco', foreground: '#fff' }`; `SystemBadge` for `coleco` has `text-bg-coleco`; `SystemStartupTests` sees `coleco` and its folders; DAT refresh fetches 40 files. Done when: `dotnet test` passes for the Coleco, Core, Data and Api test projects (Docker running), SPA lint/check/tests pass, `docker build .` succeeds.
- [x] **4. ColecoVision seed and integration tests**: C4. Add `new FixtureGame("Pixel Pursuit (USA)", "Pixel Pursuit (USA).col", rom) { ReleaseYear = 1983, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Action"] }`, `rom` = 8192 bytes from `new Random(1301).NextBytes` with bytes 0–1 set to `AA 55`, to the Basic set and the Coleco fixture DAT, written loose as `ingest/coleco/01-pixel-pursuit.rom` (proves `.rom` is accepted and the DAT name wins). Update every count it changes (`IngestSeederTests`, `SeedIngestTests`). `IngestPipelineTests`: `.col` in `ingest/nes/` → `UnsupportedType`; `.nes` in `ingest/coleco/` → `UnsupportedType`; 8191 bytes → `TooSmall`; 32769 → `TooLarge`; an unmatched 8 KiB `.bin` with `55 AA` → Review `NoDatMatch` with a file-name suggestion and header magic `55AA`, and resolved via review (Manual) it is stored as `….col`; an unmatched 8 KiB `.bin` starting `00 00` → `_failed/` with `InvalidHeader`; a `.zip` in `ingest/coleco/` holding a DAT game's `.col` → ingested. Files: `IngestSeeder.cs`, `IngestSeederTests.cs`, `SeedIngestTests.cs`, `IngestPipelineTests.cs`. Tests: `SeedIngestTests` asserts the ZIP under `roms/coleco/P/` with entry `Pixel Pursuit (USA).col` equal to the seeded bytes, `originalFileName` `01-pixel-pursuit.rom`, `system: coleco`, header magic `AA55`, year/genre metadata. Done when: `dotnet test` passes for Core and Api test projects (Docker running).
- [ ] **5. e2e**: `mvp.spec.ts`: update seed counts (`Ingested 12` → `Ingested 13`, the audit `toHaveCount(12)` → `13`, all three `10 games` → `11 games`, any other); the Library System filter lists options in S1 order ("All systems" first, then Atari 2600, ColecoVision, …); System filter → `ColecoVision`, URL `system=coleco`, `1 game`; on the SNES seed game's page (Mode Seven Rally) at 375 px width, the `.card` holding the "Game information" table has `scrollWidth <= clientWidth`. Files: `tests/e2e/specs/mvp.spec.ts`. Done when: `pwsh scripts/test-e2e.ps1` passes.
- [ ] **6. Docs**: README (ColecoVision in supported systems + Refresh DATs note); `features.md` (supported-systems sentence; S1 alphabetical system lists; G1 game info wraps; `ingest/coleco/` with `.col`/`.rom`/`.bin`, 8–32 KiB, magic check → `InvalidHeader` or Review; families list); `zip-format.md` (`coleco/` in the folder tree, `coleco` in the `system` id list, ColecoVision: `header` `{ magic }`); `deployment.md` (`ingest/coleco/`, upgrade step); README's quick-start ingest folder list (`data/ingest/coleco/`); `development.md` (project table, seed table with Pixel Pursuit, Basic counts); `adding-a-system.md` (project list; ColecoVision as the magic-bytes-only header example). Tests: none. Done when: every item above is in its doc and `pwsh scripts/test-all.ps1` passes.

## Decisions
- 2026-11-09 Sort scope → sort `GET /api/systems` by display name so every system dropdown is alphabetical (user)
- 2026-11-09 System id → `coleco` (user)
- 2026-11-09 Extensions → `.col`, `.rom`, `.bin`; resolved files stored as `.col` (user)
- 2026-11-09 Header → check `AA55`/`55AA` magic; unmatched without it → `InvalidHeader` (user)
- 2026-11-09 Sizes 8192–32768 from the libretro DAT 2026.10.07 (code)
- 2026-11-09 Game info table → drop `table-responsive`, wrap values with `text-break` (code)
- 2026-11-09 System badge wraps in the info table (`text-wrap`); e2e checks the SNES game at 375 px (reviewer)
- 2026-11-09 Project `RnRomManager.Systems.Coleco`, handler `ColecoVisionHandler` (code)

## Kickoff prompt
```text
/spec implement 2026-11-09-coleco-sort-game-table.md
```
Starts the spec, or resumes it at the first unticked step.
