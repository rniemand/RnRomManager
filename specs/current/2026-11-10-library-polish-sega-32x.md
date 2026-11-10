---
title: Library polish, Zip Contents, Ingestion tabs and 32X
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-10
---

# Library polish, Zip Contents, Ingestion tabs and 32X

## Summary
Quality-of-life pass for everyone browsing the library, plus one new system. Library cards look better (lighter in dark mode, subtle hover, box art filling the image area, coloured system badges) and the System / Region / Letter filters show how many games each option would give. The Game page's release cards collapse to their header, and a new **Zip Contents** tab browses the game's ZIP and downloads single entries. The Ingestion page splits Pending / Failed / Last runs into tabs under an always-visible Run section. Sega 32X (`s32x`) joins the Sega project, reusing the Mega Drive header reader.

## Out of scope
- Facet counts on a collection's page (it has no Region/Letter filters; its System filter stays as is).
- Counts on the "All systems" / "Any region" / "All" options.
- Editing ZIP contents, previewing entries in the browser, or downloading a folder as a sub-ZIP.
- Remembering which release cards were open (always collapsed on load).
- 32X CD / Mega-CD 32X, `.bin` 32X dumps, a dedicated `s32x` icon (controller fallback), automatic DAT refresh after upgrading.

## Behaviour

### Library cards (`GameResults`, grid view)
- **L1 Dark-mode background.** In the dark theme each card's background is `var(--bs-tertiary-bg)` (a step lighter than the body); the light theme is unchanged. Done with a `game-card` class and a `[data-bs-theme='dark'] .game-card { --bs-card-bg: var(--bs-tertiary-bg); }` rule in `app.css`.
- **L2 Hover.** On hover (and on `:focus-within`, so keyboard users get it too) the card lifts `translateY(-2px)` and gains `box-shadow: var(--bs-box-shadow-sm)`, over a 150 ms transition. Under `prefers-reduced-motion: reduce` only the shadow changes (no transform, no transition).
- **L3 Image fill.** Box art uses `object-fit-cover` (`w-100 h-100`) instead of `object-fit-contain`: it fills the whole 1:1 area with no visible border, keeping its proportions; non-square art is cropped at the edges. The image area keeps `bg-body-secondary` (shows only while loading / for the placeholder icon). The card's top corners stay rounded (`card-img-top` already clips; add `overflow-hidden` on the image wrapper if the cover image spills).
- **L4 System colours.** The grid card's top-left callout uses `text-bg-{systemColour(system).name}` instead of `text-bg-dark`, keeping its upper-cased id text and visually-hidden full name. The list view's System column uses `SystemBadge` (`system`, `name` = `systemName`) instead of the dark badge. Unknown ids fall back to `text-bg-secondary` (existing `systemColour` behaviour). The collection page shares `GameResults`, so it gets L1–L4 too.

### Library filter counts
- **F1 API.** New `GET /api/games/facets` (any signed-in user, like `GET /api/games`). Query: `system`, `letter`, `region`, `q`, `favourite`, validated exactly as `GET /api/games` (same `ParseQuery`, same `400` codes; `sort`, `dir`, `page`, `pageSize`, `collection`, `notInCollection` are not accepted — unknown query keys are ignored as elsewhere). Response `200`:
  ```json
  { "systems": [{ "id": "gb", "count": 12 }], "regions": [{ "region": "USA", "count": 9 }], "letters": [{ "letter": "A", "count": 3 }] }
  ```
  - `systems`: games matching every filter **except** `system`, grouped by system id; only ids with ≥ 1 game, ordered by id (ordinal).
  - `letters`: games matching every filter except `letter`, grouped by bucket (`#`, `A`–`Z`); only buckets with ≥ 1, ordered ordinally.
  - `regions`: games matching every filter except `region`; a game counts once for each distinct region among its releases (so the counts can sum to more than the total); only regions with ≥ 1, ordered ordinally.
  - Search uses the same word matching as `GET /api/games` (title, developers, publishers, genres).
- **F2 Repository.** `IGameRepository.ListFacetsAsync(GameQuery query, CancellationToken)` → `GameFacets(IReadOnlyDictionary<string,int> Systems, IReadOnlyDictionary<string,int> Regions, IReadOnlyDictionary<string,int> Letters)`. The WHERE building in `ListAsync` moves into one private helper that takes the filter to leave out, so both methods share it (no duplicated filter SQL). Systems and letters are `GROUP BY` queries; regions select the matching games' `region_key`s and are counted in C# with the existing `Regions()` split (`shortcut:` comment: counts in memory, fine for tens of thousands of games; move to a `rom_regions` table if it gets slow).
- **F3 UI.** The Library page fetches facets in an effect keyed on `facetsApiUrl(filters)` (so paging and sorting never refetch them; own `AbortController`, like the games call; a failure is silent — options just show no counts, the games call reports errors). Option labels become `<name> (<count>)`, e.g. `Game Boy (12)`, `USA (9)`, `A (3)`; an option missing from the response shows `(0)` and stays selectable. The option **lists** don't change: systems from `/api/systems`, regions as derived today, all 27 letters. While facets load (or after a failure) labels have no count, so nothing flickers to `(0)`. The "All …" / "Any region" options have no count. The extra option for a URL region that isn't in the derived list gets a count too. Facets aren't refetched when a rating or favourite is changed from a card (only on a filter change), so with `favourite=true` the counts can be stale until the next filter change; accepted.

### Game page — Releases
- **R1 Collapsed by default.** Each release card shows only its `card-header` on load; the `card-body` (Revision, Flags, Size, CRC32, SHA1) is hidden (`hidden` attribute, so it stays in the DOM and copy buttons keep working when shown).
- **R2 Toggle.** A chevron button is the header's first element: `aria-expanded`, `aria-controls="release-{id}-details"`, `aria-label="Show details for {fileName}"` / `"Hide details for {fileName}"`; the chevron icon points right when collapsed and down when expanded (rotated with a 150 ms transition, none under reduced motion). Clicking empty space in the header also toggles; clicks on any button, link or the CopyButton inside the header do not (the handler ignores events whose target is inside `button, a`), nor does a click that ends a text selection (`getSelection()?.toString()` non-empty), so the file name can be selected and copied. The header shows `cursor: pointer`.
- **R3 Expand all.** One `btn-sm btn-outline-secondary` button above the cards: **Expand all** when any card is collapsed, otherwise **Collapse all**. Shown only when the game has ≥ 2 releases.
- **R4 State.** Open/closed is per page view: a `SvelteSet` of open release **file names** (unique within a game; ROM ids change on every ZIP rewrite), cleared when the game id changes. After a reload of the same game (e.g. a note saved, a release moved) releases whose file name still exists keep their state.

### Game page — Zip Contents tab
- **Z1 Tab.** A third tab **Zip Contents** after Images, URL `?tab=zip` (GP1 rules: `replace`, no game refetch). The panel stays mounted like the others but loads its data **lazily** the first time it's shown. When the `game` object is replaced (any reload of the game), the entries are marked stale: refetched at once if the tab is showing, otherwise on its next show. After a refetch the selected folder is kept if it still exists, else the selection falls back to the root; expanded folders that still exist stay expanded (the selected one's ancestors are always expanded). A new game id resets selection and expansion to the root. No count in the tab label.
- **Z2 List API.** New `GET /api/games/{id}/zip-entries` (any signed-in user) → `200` with the ZIP's file name (last segment of the game's `zip_path`) and its file entries, ordered by `path` (ordinal):
  ```json
  { "zipName": "Orbit Blocks.zip", "entries": [{ "path": "roms/Orbit Blocks (World).gb", "sizeBytes": 32768, "compressedSizeBytes": 20111, "modifiedUtc": "2026-11-01T10:00:00Z" }] }
  ```
  `modifiedUtc` = `entry.LastWriteTime.UtcDateTime` (ZIP entries store DOS local time, 2-second precision; exact only while the server's time zone is unchanged — documented in `zip-format.md`). Folder entries (names ending `/`) are left out; folders are implied by paths. Unknown game → `404`; missing ZIP → `404` (logged, as `LibraryFiles.Fail`); unreadable ZIP → `500 IoError`. Read with the raw `ZipArchive` (no `info.json` parsing), via a new `LibraryFiles.ListEntries`. Sizes are the central directory's values.
- **Z3 Entry download API.** New `GET /api/games/{id}/zip-entries/download?path=<entry full name>` (any signed-in user): streams that entry as `application/octet-stream` with `Content-Disposition` filename = the last path segment (RFC 5987 `filename*` via `Results.Stream`'s `fileDownloadName`). `path` missing or empty → `400 ValidationFailed` with `errors.path` = `["Required."]`; no file entry with exactly that name (folder names included) → `404`; unknown game / missing ZIP → `404`; unreadable → `500 IoError`. The stream is capped at the entry's central-directory length (`LimitStream`, as release downloads are). The name is only ever looked up in the archive, never used as a file system path. Not audited (downloads never are).
- **Z4 Browser UI.** New `ZipBrowser.svelte`: a two-pane layout (stacked below `md`): left a folder tree, right the selected folder's contents.
  - The tree's root node is `zipName` (Z2); folders are nested list items sorted by name, each with a chevron **button** that only expands/collapses it (`aria-expanded`, `aria-label="Expand <name>"` / `"Collapse <name>"`) and a name **button** that selects and expands it (never collapses; `aria-current="true"` on the selected one). The root is selected and expanded on first load. No `<details>/<summary>`.
  - The selected folder's contents are listed in a standard table (`table table-sm table-striped table-hover`, caption visually hidden "Contents of <folder>"): sub-folders first (folder icon, name; clicking selects and expands it in the tree), then files with **Name**, **Size** (`FileSize`), **Compressed** (`FileSize`), **Modified** (`formatDateTime`) and a last column with a download **link** (`<a class="btn btn-sm btn-outline-primary" href="…/zip-entries/download?path=<encodeURIComponent(path)>" download aria-label="Download <name>">`). A plain link: an entry that vanished since the listing (ZIP rewritten meanwhile) is not caught before navigating — the browser gets the `404`; accepted, the tab refetches on the next game reload.
  - A breadcrumb above the table shows the selected folder's path; each segment selects that folder.
  - Folders and files are each sorted by name, ordinal (the API order), in the tree and the table.
  - Footer line: `<n> files · <total size> (<total compressed> compressed)` for the whole ZIP (`1 file` when one).
  - Loading → `Loading`; error → `ErrorAlert` with retry; an empty folder → `EmptyState` "This folder is empty."

### Ingestion page
- **I1 Tabs.** Under the always-visible **Run** section (unchanged, including the DATs-missing notice and job progress), a `nav-tabs` tablist with **Pending files (n)**, **Failed files (n)** and **Last runs**: `n` = number of files across all systems. The three existing sections move into tab panels unchanged (their `h2` headings become visually hidden, as on the Game page). Pending files is open by default.
- **I2 URL state.** `?tab=failed` / `?tab=runs` select those tabs; anything else (or none) is Pending. Switching uses `goto(…, { replace: true, reset: false })` and doesn't reload the data. Panels stay mounted (`hidden`), so selections survive tab switches.
- **I3 Behaviour unchanged.** Import selection, delete modals, Shift+click delete, run paging and reload after a job behave as today.

### Sega 32X
- **X1 Contract values.** `Id` = `s32x` (a CSS class can't start with a digit), `DisplayName` = `32X`, `ShortName` = `32X`, `Family` = `sega32x`, `RomExtensions` = `[".32x"]`, `MinRomSizeBytes` = `16384`, `MaxRomSizeBytes` = `4194304` (4 MiB), `DatName` = `Sega - 32X`; `NormalizeForHashing` is the default. Facts (libretro DAT 2026.10.07, checked 2026-11-10): all five DAT files and the thumbnails folder exist; 63 entries, all `.32x`, 24,576–4,194,304 bytes.
- **X2 Shared header reader.** The body of `MegaDriveHandler.ReadHeader` and its `Text` helper move, unchanged, into `internal static class MegaDriveHeader { RomHeader? Read(ReadOnlySpan<byte> rom, string systemId); }`. `MegaDriveHandler.ReadHeader` becomes `MegaDriveHeader.Read(rom, Id)`; the existing `MegaDriveHandlerTests` pass untouched. `Sega32xHandler.ReadHeader` is `MegaDriveHeader.Read(rom, "s32x")`: same fields and rules (system type starting with `SEGA`, e.g. `SEGA 32X`), classified `s32x` whatever the system type says; optional, never throws.
- **X3 Registration.** Registered after `MegaDriveHandler`. Registry order: `gb, gbc, gba, n64, nes, snes, a2600, coleco, gg, sms, md, s32x`. `/api/systems` (display-name order, `OrdinalIgnoreCase`): `s32x` (`32X`) first, then `a2600, coleco, gb, gba, gbc, gg, sms, md, n64, nes, snes`. `/api/admin/dats` lists `Sega - 32X` before `Sega - Game Gear`. A DAT refresh fetches 60 files (12 × 5). Ingestion refuses with `DatsMissing` until **Refresh DATs** (documented).
- **X4 Families and ingestion.** `.32x` in another system's folder → `UnsupportedType`; `.md`/`.bin`/`.gen` in `ingest/s32x/` → `UnsupportedType`. A DAT-matched dump is stored as `roms/s32x/<bucket>/<title>.zip`; an unmatched file (with or without header) → Review `NoDatMatch`, Manual/Flag resolves store `….32x`. Under 16 KiB → `TooSmall`, over 4 MiB → `TooLarge`. Releases with a header show the Header button.
- **X5 Colour and box art.** `--bs-s32x: #e8590c` (orange, clearly apart from NES's yellow `warning` and the reds/purples/blues in use) with `.text-bg-s32x { color: #000 !important; background-color: var(--bs-s32x) !important; }` (black on it is AA, ~5.9:1), same in both themes; `systemColours.s32x = { name: 's32x', foreground: '#000' }` (union gains `s32x`). `sega32x.svg` (drawn for this project, MIT) and its 512×512 PNG render, embedded like `megadrive.png`; boxart `README.md` lists it.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | `src/RnRomManager.Systems.Sega/MegaDriveHeader.cs` (new), `MegaDriveHandler.cs`, `Sega32xHandler.cs` (new), `Resources/boxart/sega32x.{svg,png}`, boxart `README.md` | X1, X2, X5 |
| Test data | `tests/RnRomManager.TestData/SyntheticMegaDriveRomBuilder.cs` | reused for 32X (`SystemType = "SEGA 32X"`, already a property or added as one) |
| Test data | `tests/RnRomManager.TestData/IngestSeeder.cs` | `Sega32xDat` const, fixture DAT entry, `ingest/s32x/`, one seed game |
| Api | `src/RnRomManager.Api/AppHost.cs` | X3 |
| Core | `src/RnRomManager.Core/Library/ILibraryRepositories.cs` (+ `GameFacets` record) | F2 |
| Data | `src/RnRomManager.Data/Repositories/LibraryRepositories.cs` | F2: shared filter helper, `ListFacetsAsync` |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | F1, Z2, Z3 |
| Api | `src/RnRomManager.Api/Endpoints/LibraryFiles.cs` | `ListEntries`, `OpenRawEntry` (Z2, Z3) |
| Web | `src/RnRomManager.Web/src/app.css` | L1, L2, X5 |
| Web | `lib/components/GameResults.svelte` | L1–L4 |
| Web | `lib/api/types.ts` | `GameFacets`, `ZipEntry` |
| Web | `routes/+page.svelte`, `lib/libraryQuery.ts` (a `facetsApiUrl(filters)` next to `gamesApiUrl`, and a pure `optionLabel(name, counts, key)` helper) | F3 |
| Web | `routes/games/[id]/+page.svelte` | R1–R4, Z1 |
| Web | `lib/components/ZipBrowser.svelte` (new), `lib/zipTree.ts` (new: pure `buildTree(entries)` → folder nodes with files and totals) | Z4 |
| Web | `routes/ingestion/+page.svelte` | I1–I3 |
| Web | `lib/systemColours.ts` (+ test) | X5 |
| Web | `lib/components/icons.ts` | `chevron` → `bi bi-chevron-right` (R2, Z4; rotated 90° when expanded), `folder` → `bi bi-folder` (Z4) |
| Tests (existing, broken by system 12) | the 15 test files (17 places) that list `[IngestSeeder.MegaDriveDat] = []` | add `[IngestSeeder.Sega32xDat] = []` |
| Tests (existing, counts/order) | `DatEndpointsTests` (55 → 60, DAT name order), `LibraryEndpointsTests` `/api/systems` list, `IngestEndpointsTests` system list, `SystemStartupTests`, `IngestSeederTests`, `SeedIngestTests`, `tests/e2e/specs/mvp.spec.ts` | |
| Tests (existing, UI) | `routes/games/game-page.test.ts` (G2 test reads `.card-body` text: expand first), `routes/ingestion/ingestion-page.test.ts`, `routes/library-page.test.ts`, `library-select.test.ts` (mock the facets call) | |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md` | last step |

No migrations or config keys. API: three new read-only endpoints (`/api/games/facets`, `/api/games/{id}/zip-entries`, `/api/games/{id}/zip-entries/download`); `GET /api/systems` and `GET /api/admin/dats` gain `s32x` (additive). Existing data is untouched.

## Risks
- **Facets cost** an extra query set per filter change (three filtered counts). Same WHERE as the games list, so same indexes; the region count reads one row per matching release. Acceptable at personal-library scale; `shortcut:` comment notes the upgrade path.
- **Entry download of an arbitrary name:** the path is only passed to `ZipArchive.GetEntry`, never to the file system, and library ZIPs are written by the app; no traversal risk. Size is capped at the declared length.
- **Hidden release details** change existing tests and the e2e flow that reads CRC/SHA1 (expand first); copy buttons stay in the DOM.
- **Upgrade blocks ingestion** until DATs are refreshed (X3), as for every earlier system; documented.
- **MD refactor (X2)** could change Mega Drive headers: guarded by the unchanged `MegaDriveHandlerTests` and the Turbo Comet seed assertion.
- **`object-fit: cover` crops** non-square box art (user's choice); the full image is still on the Images tab.

## Steps
- [x] **1. Shared Mega Drive header and 32X handler**: X1, X2, X5 (box art). Files: `MegaDriveHeader.cs`, `MegaDriveHandler.cs`, `Sega32xHandler.cs`, `Resources/boxart/sega32x.{svg,png}`, boxart `README.md`, `SyntheticMegaDriveRomBuilder.cs` (if it needs a `SystemType` property), `tests/RnRomManager.Systems.Sega.Tests/Sega32xHandlerTests.cs`. Tests: contract (every X1 value); `ReadHeader` with a 512 KiB builder ROM with `SEGA 32X` → plausible, classified `s32x`, fields as Mega Drive; no `SEGA` → null; shorter than `0x200` → null, no exception; `NormalizeForHashing` unchanged; box art valid 512×512 PNG differing from Mega Drive's; `MegaDriveHandlerTests` pass unchanged. Done when: `dotnet test tests/RnRomManager.Systems.Sega.Tests` passes and `dotnet build` passes with warnings as errors.
- [x] **2. 32X registration and existing tests**: X3, X5 (colour). `AppHost` one line; `IngestSeeder.Sega32xDat` const, `[Sega32xDat] = []` in `FixtureDatWriter.Write`, `ingest/s32x/` created; the empty entry in each of the 15 test files (17 places); counts and orders (DATs 55 → 60, systems 11 → 12, `/api/systems` list as X3, startup ids/folders, `IngestSeederTests` DAT file count); `app.css`, `systemColours.ts` + test. Tests: `systemColour('s32x')` as X5; `SystemBadge` for `s32x` has `text-bg-s32x`; `SystemStartupTests` sees `s32x` and its folder; DAT refresh fetches 60 files; `/api/admin/dats` order. Done when: `dotnet test` passes for the Sega, Core, Data and Api test projects (Docker running); SPA `npm run lint`, `npm run check`, `npm test` pass.
- [x] **3. 32X seed and integration tests**: X4. Basic set and fixture DAT: `new FixtureGame("Star Relay (USA)", "Star Relay (USA).32x", rom) { ReleaseYear = 1995, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Shooter"] }`, `rom` = `new SyntheticMegaDriveRomBuilder { Seed = 1701, Size = 524288, SystemType = "SEGA 32X" }.Build()`, written loose as `ingest/s32x/01-star-relay.32x`. Update every count this changes (`IngestSeederTests`, `SeedIngestTests`). `IngestPipelineTests`: `.32x` in `ingest/md/` → `UnsupportedType`; `.md` in `ingest/s32x/` → `UnsupportedType`; 16383 bytes → `TooSmall`, 4194305 → `TooLarge`; unmatched headered `.32x` → Review `NoDatMatch` with a header-title suggestion, Manual resolve stored as `….32x`. Files: `IngestSeeder.cs`, `IngestSeederTests.cs`, `SeedIngestTests.cs`, `IngestPipelineTests.cs`. Tests: `SeedIngestTests` asserts `roms/s32x/S/` with entry `Star Relay (USA).32x` equal to the seeded bytes, header `systemType` "SEGA 32X", `system`/year/genre metadata. Done when: `dotnet test` passes for Core and Api test projects (Docker running).
- [x] **4. Facets API**: F1, F2. Files: `ILibraryRepositories.cs`, `LibraryRepositories.cs`, `LibraryEndpoints.cs`, `tests/RnRomManager.Data.Tests/Database/GameQueryTests.cs`, `tests/RnRomManager.Api.Tests/Integration/LibraryEndpointsTests.cs`. Tests (Testcontainers): with games across two systems, several regions and letters — no filters → every count; `system=gb` → `systems` still lists both systems, `letters`/`regions` only gb's; `region=USA` → `regions` ignores it, others narrowed; `letter`, `q`, `favourite=true` each narrow the other lists; a multi-region game counts once per region; zero-count values are absent; `ListAsync` results unchanged (existing tests pass). Api: `200` shape; invalid `system`/`letter` → same `400` as `/api/games`; unauthenticated → `401`. Done when: `dotnet test` passes for Data and Api test projects.
- [x] **5. Library UI**: L1–L4, F3. Files: `app.css`, `GameResults.svelte`, `routes/+page.svelte`, `libraryQuery.ts` (+ test), `types.ts`, `library-page.test.ts`, `library-select.test.ts`. Tests (vitest): `facetsApiUrl` builds the query from filters (no sort/page); `optionLabel` → `Game Boy (12)`, missing key → `(0)`, counts null → plain name; page: options show counts after facets load, no counts while pending or after a facets error, refetch on a filter change, no facets call on a page or sort change; grid card has `game-card`, the callout has `text-bg-<colour>` (`text-bg-secondary` for an unknown id), image has `object-fit-cover`; list view System cell is a `SystemBadge`. Done when: SPA `npm run lint`, `npm run check`, `npm test` pass.
- [x] **6. Zip entries API**: Z2, Z3. Files: `LibraryFiles.cs`, `LibraryEndpoints.cs`, `tests/RnRomManager.Api.Tests/Integration/ZipEntryEndpointsTests.cs` (new). Tests: list for a seeded game → `zipName` = the ZIP's file name, `info.json`, `roms/…`, `images/…` entries with sizes matching the archive, no folder entries, ordinal order; unknown game → 404; ZIP deleted on disk → 404; corrupt ZIP → 500 `IoError`; download `info.json` and a ROM → bytes equal, `Content-Disposition` filename = last segment (non-ASCII name → `filename*`); missing `path` → 400 `Required`; `path=roms/` (folder) and `path=nope` → 404; `path=../x` → 404; unauthenticated → 401. Done when: `dotnet test tests/RnRomManager.Api.Tests` passes.
- [ ] **7. Collapsible releases**: R1–R4. Files: `routes/games/[id]/+page.svelte`, `lib/components/icons.ts`, `game-page.test.ts`. Tests: cards start collapsed (body `hidden`, chevron `aria-expanded="false"`); chevron toggles and updates label; clicking header space toggles, clicking Download/Note/Header does not; a click with a text selection does not; Expand all / Collapse all text and effect, hidden with one release; state survives a same-game reload where ROM ids change but file names don't, resets on a new game id; existing G2 test expands before reading the body. Done when: SPA lint, check and tests pass.
- [ ] **8. Zip Contents tab**: Z1, Z4. Files: `routes/games/[id]/+page.svelte`, `ZipBrowser.svelte`, `zipTree.ts` (+ `zipTree.test.ts`), `types.ts`, `game-page.test.ts`. Tests: `buildTree` nests folders from paths, root files, totals, sorting; `?tab=zip` opens the tab; entries fetched only when the tab is first shown; a game reload while showing refetches at once, while hidden refetches on next show; selection kept if the folder still exists, else root; chevron toggles without selecting, name selects and expands but a second click doesn't collapse; tree expand/select; root label is `zipName`; table shows sub-folders then files with both sizes and a download link (`<a download>`) to `…/zip-entries/download?path=<encoded>`; breadcrumb selects a parent; empty folder message; error + retry. Done when: SPA lint, check and tests pass.
- [ ] **9. Ingestion tabs**: I1–I3. Files: `routes/ingestion/+page.svelte`, `ingestion-page.test.ts`. Tests: Run section outside the tablist and visible on every tab; Pending default; labels with counts; `?tab=failed` / `?tab=runs` open those; clicking a tab calls `goto` with replace and no extra API call; selections survive switching; existing tests pass (selecting the tab where needed). Done when: SPA lint, check and tests pass.
- [ ] **10. e2e**: `mvp.spec.ts`: seed counts +1 (ingested, audit rows, `N games` texts); System filter option list gains `32X (1)` first and every option shows its count; System → `32X`, URL `system=s32x`, `1 game`; game detail expands a release before reading/copying CRC; Zip Contents tab lists `info.json` and the ROM under `roms`, downloads the ROM entry (bytes size matches); Ingestion tabs: Failed and Last runs tabs show their content, Run visible. axe passes on the new tab and tabs. Files: `tests/e2e/specs/mvp.spec.ts`. Done when: `pwsh scripts/test-e2e.ps1` passes.
- [ ] **11. Docs**: README (32X in supported systems, Refresh DATs note, `data/ingest/s32x/`); `features.md` (systems list; API entries for `GET /api/games/facets` (query, response, `400`s), `GET /api/games/{id}/zip-entries` (response, `404`/`500`) and `…/zip-entries/download` (`400 Required`, `404`, `500 IoError`); Library cards and filter counts; Releases collapse + Expand all; Zip Contents tab; Ingestion tabs; `ingest/s32x/` `.32x` 16 KiB–4 MiB, optional header; families list; size limits; Review first-extension list adds `.32x`); `zip-format.md` (`s32x/` folder, id, header fields = Mega Drive's; entry modified times are the writer's local time); `deployment.md` (`ingest/s32x/`, upgrade list); `development.md` (Sega project row, seed table with Star Relay, Basic counts, `ZipBrowser`/facets in Web app structure); `adding-a-system.md` (box-art list; 32X as a shared-reader example). Tests: none. Done when: every item above is in its doc and `pwsh scripts/test-all.ps1` passes.

## Decisions
- 2026-11-10 Filter counts → respect the other active filters (faceted), one new API call (user)
- 2026-11-10 Zero-count options → shown as `(0)`, still selectable (user)
- 2026-11-10 Card box art → `object-fit: cover` (crop, no distortion) (user)
- 2026-11-10 Zip entry download → every file entry, any signed-in user (user)
- 2026-11-10 32X → own system `s32x`, "32X", family `sega32x`, `.32x` only, 16 KiB–4 MiB, Mega Drive header reader shared (user)
- 2026-11-10 Release toggle → chevron button + header click; Expand all / Collapse all button (user)
- 2026-11-10 Ingestion tabs → URL state (`?tab=failed|runs`) and file counts in labels (user)
- 2026-11-10 System callouts colourised in both grid and list views via `systemColour`/`SystemBadge` (code: the badge colours used elsewhere)
- 2026-11-10 Zip Contents loads lazily on first open; no count in its tab label (code: avoids opening every ZIP on page load)

- 2026-11-10 Release open state keyed on file name, not ROM id (reviewer R1: ids change on every ZIP rewrite)
- 2026-11-10 Zip root label from `zipName` in the Z2 response, not a new `GameDetail` field (reviewer R2)
- 2026-11-10 Entry download is a plain `<a download>`; a vanished entry isn't pre-checked (reviewer R3)
- 2026-11-10 Zip tree uses separate chevron (toggle) and name (select + expand) buttons (reviewer R5)
- 2026-11-10 32X colour orange `#e8590c` with black text, apart from NES yellow (reviewer R10)
- 2026-11-10 Facets refetch only on a filter change, not after a rating/favourite edit (spec review: avoids a refetch per star click)
- 2026-11-10 New `chevron` and `folder` icons in `icons.ts` (second review: neither exists yet)
- 2026-11-10 Release header click ignores clicks that end a text selection (user)
- 2026-11-10 Zip tree keeps expanded folders across a refetch; tree and table sort ordinal (second review: unspecified)
- 2026-11-10 Missing entry `path` → the standard `400 ValidationFailed` with `errors.path` = `Required.` (implementation: no `Required` problem code exists; every other validation uses `ValidationFailed`)

## Kickoff prompt
```text
/spec implement 2026-11-10-library-polish-sega-32x.md
```
Starts the spec, or resumes it at the first unticked step.
