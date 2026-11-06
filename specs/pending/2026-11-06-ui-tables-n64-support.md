---
title: UI tables, file sizes, logs backlog and Nintendo 64 support
status: ready # draft | ready | in-progress | completed
created: 2026-11-06
---

# UI tables, file sizes, logs backlog and Nintendo 64 support

## Summary
A batch of SPA polish plus a fourth system. Every file size renders through one `FileSize` component, and every table uses one compact, striped, hoverable Bootstrap style with a dark header. The review delete modal names the file and uses that table style, the game page's image upload moves into a modal, and the Admin Logs tab shows the last 20 log entries as soon as it opens. Nintendo 64 (`n64`) is added as a new system through the existing `ISystemHandler` plug-in: `.z64`, `.v64` and `.n64` dumps are all converted to big-endian before hashing, so they match the No-Intro DAT and are stored as `.z64`.

## Out of scope
- Size units above MiB (GiB). No ROM or image the app handles is that large.
- A shared `<DataTable>` wrapper component. Tables keep their markup; the style is enforced by a test (T4).
- 64DD disks (`.ndd`), Aleck64 boards, the DAT's six `.bin` entries, save files and any CIC/boot-code checksum check.
- Reporting the original byte order of an N64 dump. The header is read after the conversion, so it can't know.
- A dedicated N64 icon. `system-n64` gets the controller fallback like the other systems.
- Any change to `ISystemHandler`, `RomHeader`, the DB schema or the existing log stream contract (L2).
- An auto DAT refresh after upgrading (as for GBA: documented manual step).

## Behaviour

### File sizes
- **F1 Component.** `src/RnRomManager.Web/src/lib/components/FileSize.svelte`, props `{ bytes: number | null | undefined }`. It renders `<span class="file-size font-monospace text-nowrap" title="{bytes} bytes">{formatSize(bytes)}</span>`, with the `title` byte count grouped by the browser locale (`1,234,567 bytes`). For null/undefined/invalid input it renders nothing (no span), as `formatSize` returns `''`. The format stays `formatSize`'s: `512 B`, `32.0 KiB`, `16.0 MiB` (one decimal, KiB/MiB).
- **F2 Everywhere.** Every file size shown in the SPA uses `FileSize`: the library game page's Releases table, the review list, the review detail page (item size and DAT search results), the ingestion page's pending files, the review delete modal, the image cards in `GameImages`, and the upload help text, which reads `PNG or JPEG, at most ` followed by `<FileSize bytes={maxUploadBytes} />` (rendering `8.0 MiB`). One place stays plain text because `<option>` can't hold markup, and keeps calling `formatSize`: the labels in the game page's release `<select>` (~line 211). The literal upload error `The image must be at most 8 MiB.` is unchanged.

### Tables
- **T1 Classes.** Every `<table>` in the SPA has `table table-sm table-striped table-hover` (keeping `align-middle`, `mb-0` and the like where they are today).
- **T2 Header.** Every `<thead>` has `class="table-dark"` and nothing else (the Releases table's `small text-uppercase text-body-secondary` goes, since secondary text is unreadable on a dark row). Column headers keep `<th scope="col">`.
- **T3 Key/value tables** (two columns, no `<thead>`: the game page's header modal, the review delete modal): each row is `<th scope="row">` + `<td>`, the `th` keeping `fw-normal text-body-secondary` where it has it today.
- **T4 Guard.** A vitest test reads every `.svelte` file under `src/` as raw text (`import.meta.glob('/src/**/*.svelte', { query: '?raw', import: 'default', eager: true })`) and fails, naming the file, when a `<table` tag lacks any T1 class or a `<thead` tag lacks `table-dark`.

### Review delete modal
- **R1 Title.** `Delete File: {originalFileName}` once the item is loaded, `Delete File` while loading or on error. The confirm button stays `Delete file`.
- **R2 Table.** The `<dl>` becomes a T1/T3 key/value table with the same rows in the same order (file name, system, reason, size, hashes, source path, archive and entry when present, header fields, added), with a visually hidden `<caption>` "Details of {originalFileName}".

### Game page images
- **I1 Upload modal.** For users who can edit the library, the Images section shows an `Upload image` button (`btn btn-outline-primary btn-sm`, upload icon) instead of the inline form. It opens a `ConfirmModal` titled `Upload image`, `confirmLabel` `Upload`, holding the same fields (file input labelled `Upload an image`, Type select, help text).
- **I2 Behaviour.** Validation is unchanged (file required, PNG/JPEG, ≤ 8 MiB, server errors on the fields). A failed upload keeps the modal open: field errors show on their field, and failures that toast today (`DuplicateImage`, `TooManyImages`, `JobRunning`, other API errors) still toast, with the modal left open. A successful upload closes the modal, resets the fields (file cleared, Type back to Box art) and updates the image list as today. Opening the modal again clears old errors. While uploading, the confirm button is busy.
- **I3 Modal order.** `GameImages`' modals stay below `GameAdminModal` in the page (game-admin.test.ts takes the first `.modal`).

### Admin logs
- **L4 Recent endpoint.** `GET /api/admin/logs/recent` (Admin, as the other `/api/admin/logs*` routes) returns a JSON array of the last 20 buffered `LogEntry` values, oldest first (fewer when the buffer holds fewer, `[]` when empty). It is backed by a new `LogBuffer.Recent(int count)` taken under the buffer's lock. Non-admins get the existing 401/403.
- **L5 Logs tab load.** On mount the Logs tab fetches L4, shows those entries (subject to the level filter), then opens the live stream as today. Fetched and streamed entries are merged by `id`: an id already held is skipped, and the list stays sorted by id (append when newer than the last, sorted insert otherwise). So the stream's backlog (up to 500, as today) fills in the older history behind the 20 shown at once, without duplicates, and a Retry refetch after streaming merges the same way. The 2,000-line cap keeps the newest ids. If the fetch fails, an `ErrorAlert` with Retry (refetch) shows above the view and the stream still opens. The view shows `No log events yet.` when nothing has been fetched or streamed, and `No log events at {minLevel} or above.` when entries exist but the filter hides them all. L3 (pause, filter, 2,000-line cap, reconnect states) is unchanged.

### Nintendo 64
- **N1 Registration.** `Nintendo64Handler`, in a new `src/RnRomManager.Systems.Nintendo64` project, is registered after GBA. `/api/systems`, the System filter and startup folder creation list `gb`, `gbc`, `gba`, `n64`.
- **N2 Contract values.** `Id` = `n64`, `DisplayName` = `Nintendo 64`, `Family` = `n64` (its own), `RomExtensions` = `[".z64", ".v64", ".n64"]` (`.z64` first, so renames and resolved review items are stored as `.z64`), `MinRomSizeBytes` = `0x1000` (4096: the header plus boot code), `MaxRomSizeBytes` = 64 MiB (`64L * 1024 * 1024`, the largest cart), `DatName` = `Nintendo - Nintendo 64` (the five libretro DAT files and the libretro-thumbnails folder exist; checked 2026-11-06).
- **N3 Byte order** (`Nintendo64ByteOrder.ToBigEndian`, used by `NormalizeForHashing`). Decided by where `0x80` sits in the first word (the PI config word always starts with `0x80` in big-endian, though its other bytes vary between carts), never by the extension; checked in this order:
  | Rule | Order | Conversion | Example |
  |---|---|---|---|
  | byte 0 = `0x80` | big-endian (`.z64`) | none (the input is returned as is) | `80 37 12 40 AA BB CC DD` |
  | byte 1 = `0x80` | byte-swapped (`.v64`) | swap each 16-bit pair | `37 80 40 12 BB AA DD CC` |
  | byte 3 = `0x80` | little-endian (`.n64`) | reverse each 32-bit word | `40 12 37 80 DD CC BB AA` |
  The three examples are the same ROM; tests pin them literally. Anything else, input shorter than 4 bytes, or a length that the conversion can't cover (odd for byte-swapped, not a multiple of 4 for little-endian) → the input unchanged; the header is then implausible (N4). Conversion writes a new array and never mutates the input. Stored bytes, `info.json` hashes and integrity deep hashes are therefore always big-endian, matching the DAT's `.z64` entries; its `.v64` entries never match and are harmless (DAT ROMs are indexed by SHA1, metadata by CRC32, and the metadat files key the `.z64` CRCs; checked 2026-11-06).
- **N4 Header reader** (`Nintendo64HeaderReader.Read`, on big-endian bytes, n64brew "ROM Header"). Never throws. Returns `null` under `0x40` bytes. Otherwise `ClassifiedSystemId` = `n64`, `Title` = `0x20–0x33` (20 bytes) read with the GB `ReadTitle` rules (ASCII up to the first NUL, non-printable → `?`, trimmed, null when empty), `IsPlausible` = `headerValid`. `Fields`:
  | Field | Source |
  |---|---|
  | `title` | as `Title` |
  | `gameCode` | `0x3B–0x3E` as ASCII when all four bytes are `A–Z`/`0–9`, else null |
  | `mediaType` | 1st game-code char: `N` Cartridge, `C` Cartridge (expandable), `D` 64DD disk, `E` 64DD expansion, `Z` Aleck64; other → `Unknown (X)`; null when `gameCode` is null |
  | `destination` | 4th game-code char: `A` Asia, `B` Brazil, `C` China, `D` Germany, `E` USA, `F` France, `H` Netherlands, `I` Italy, `J` Japan, `K` Korea, `N` Canada, `P` Europe, `S` Spain, `U` Australia, `W` Scandinavia, `X`/`Y` Europe; other → `Unknown (X)`; null when `gameCode` is null |
  | `version` | `0x3F` as an int |
  | `crc1`, `crc2` | `0x10`, `0x14` as big-endian `0xHHHHHHHH` (upper-case hex) |
  | `bootAddress` | `0x08` as `0xHHHHHHHH` |
  | `headerValid` | byte `0x00` is `0x80` |
  | `romSizeBytes` | the length |
- **N5 Ingestion.** The existing pipeline, with one wording fix: the `InvalidHeader` detail in `IngestPipeline` (~line 137) becomes `no DAT match, and the header isn't valid for this system` (today it names the GB logo and checksum, wrong for N64). A matched dump in any order → `roms/n64/<Letter>/<DAT name>.z64` inside the release ZIP. Unmatched + plausible → Review with the header title as suggestion; resolving names it `….z64` (existing `RomExtensions[0]` rule). Unmatched + implausible → `InvalidHeader`. Under 4096 bytes → `TooSmall`, over 64 MiB → `TooLarge`. A file with another family's extension in `ingest/n64/`, or an N64 file in another family's folder → `UnsupportedType`. The same dump in two orders is a `Duplicate` (same normalised SHA1).
- **N8 Badge.** `SystemBadge` gets `n64: 'text-bg-danger'`, a fixed colour like the other systems.
- **N6 Default box art.** `n64.svg` (drawn for this project, MIT) and its 512×512 `n64.png` render, embedded in the new project like the GameBoy ones.
- **N7 Upgrade.** Ingestion refuses with `DatsMissing` until an Admin runs **Admin → DATs → Refresh DATs** (existing behaviour, documented).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Web | new `src/lib/components/FileSize.svelte` (+ test) | F1 |
| Web | `src/lib/components/GameImages.svelte`, `ReviewDeleteModal.svelte`, `src/routes/games/[id]/+page.svelte`, `routes/ingestion/+page.svelte`, `routes/review/+page.svelte`, `routes/review/[id]/+page.svelte` | F2 sizes; T1–T3 tables; R1–R2; I1–I3 |
| Web | `routes/+page.svelte`, `routes/admin/{about,dats,settings,users}/+page.svelte`, `routes/integrity/+page.svelte`, `routes/jobs/[id]/+page.svelte` | T1–T2 classes only |
| Web | new `src/lib/table-style.test.ts` | T4 guard |
| Web | `routes/admin/logs/+page.svelte`, `src/lib/api/types.ts` (none if `LogEntry` fits) | L5 |
| Core | `src/RnRomManager.Core/Ingest/IngestPipeline.cs` | N5 `InvalidHeader` wording |
| Web | `src/lib/components/SystemBadge.svelte` | N8 |
| Api | `src/RnRomManager.Api/Logging/LogBuffer.cs`, `Endpoints/AdminEndpoints.cs` | L4. New route, read-only, additive to the API contract |
| Systems | new `src/RnRomManager.Systems.Nintendo64/` (`Nintendo64Handler.cs`, `Nintendo64HeaderReader.cs`, `Nintendo64ByteOrder.cs`, `Resources/boxart/n64.svg`, `n64.png`, `README.md`, csproj embedding `boxart/*.png`) | N2–N4, N6. The csproj mirrors `RnRomManager.Systems.GameBoy.csproj`; a 5-line private resource loader (the GameBoy `EmbeddedResources` helper is `internal` to its project) |
| Wiring | `RnRomManager.slnx`, `src/RnRomManager.Api/RnRomManager.Api.csproj`, `Dockerfile` (csproj `COPY` line ~21), `src/RnRomManager.Api/AppHost.cs` | N1: project + test project in the solution, Api reference, Docker restore layer, `AddSystemHandler<Nintendo64Handler>()` after GBA |
| Tests (new) | `tests/RnRomManager.Systems.Nintendo64.Tests/` (csproj mirroring the GameBoy tests project), `tests/RnRomManager.TestData/SyntheticN64RomBuilder.cs` | reader, byte order, handler, builder |
| Tests (existing, broken by a 4th system) | every fixture writing `[IngestSeeder.GbaDat]`: `DatEndpointsTests`, `ImageEndpointsTests`, `IngestArchiveTests`, `IngestEndpointsTests`, `IngestPipelineTests`, `IngestReviewTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `ReviewEndpointsTests` | add `[IngestSeeder.N64Dat] = []`, else `DatsMissing` |
| Tests (existing) | `DatEndpointsTests` (stub serves 5 N64 DATs, requests 15 → 20, `/api/admin/dats` lists `Gb, Gba, Gbc, N64`), `LibraryEndpointsTests` / `IngestEndpointsTests` (systems 3 → 4, pending systems + `n64`), `SystemStartupTests` (ids + `n64` folders), `IngestSeederTests` (DAT files 15 → 20; seeded files +1 in step 9), `SeedIngestTests`, `tests/e2e/specs/mvp.spec.ts` | counts |
| Tests (existing) | `src/routes/games/game-images.test.ts`, `game-page.test.ts`, `routes/review/review-page.test.ts`, `routes/admin/logs-page.test.ts`, `Api.Tests/.../EndpointRoleMatrixTests.cs`, `AdminEndpointsTests.cs`, e2e images flow (~line 236) | upload now opens a modal first; delete modal title/table; logs fetch; role row for L4 |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md` | N64 (systems, folders, header fields, byte-order note, upgrade note, project + seed tables); `adding-a-system.md` uses N64 as the `NormalizeForHashing` example; `features.md` Logs tab (L4/L5), upload modal; `development.md` T1–T4 and `FileSize` conventions |

No migrations or config keys. `system_id` is a `VARCHAR`; existing data is untouched. API: one new admin GET route.

## Risks
- **Ingestion blocked after upgrade** (N7) until DATs are refreshed. Documented in `docs/deployment.md` and the README.
- **Memory.** A 64 MiB dump is read, converted into a new array and copied by the pipeline's `.ToArray()`: about 3 × 64 MiB transient per file, one file at a time. Within the existing design; the archive total stays 256 MiB.
- **Wrong byte-order rule** would make every non-`.z64` dump miss the DAT. Covered by the literal N3 vectors (written by hand, not by the builder) and by seeding the matched N64 game as a `.v64` file so the full pipeline proves the match.
- **Weak N64 plausibility.** `headerValid` only checks the `0x80` byte, so about 3 in 256 junk files with an N64 extension go to Review instead of `InvalidHeader`. Accepted: Review is harmless (the user deletes them) and a stricter rule risks rejecting real carts.
- **A 4th system breaks existing tests** (`DatsMissing`, counts). Fixed in the same step as the registration (step 8); seed counts in step 9.
- **T4 guard false positives** on a `<table` string inside a comment or text: none today; the guard's failure names the file.
- **Logs merge** relies on ids increasing, which `LogBuffer` guarantees across restarts (L1); merging by id makes the fetch/stream overlap and Retry safe.
- No real ROMs are committed; tests use synthetic ROMs only.

## Steps
- [ ] **1. FileSize component**: F1 and F2 across every file named in § Changes for F2. Files: `FileSize.svelte`, `FileSize.test.ts`, `GameImages.svelte`, `ReviewDeleteModal.svelte`, `routes/games/[id]/+page.svelte`, `routes/ingestion/+page.svelte`, `routes/review/+page.svelte`, `routes/review/[id]/+page.svelte`, plus any page test whose text assertion the span changes. Tests: vitest — renders `32.0 KiB` for 32768 with the `file-size` class and `title` equal to `${(32768).toLocaleString()} bytes` (locale-independent); `512 B`; `16.0 MiB`; null renders nothing; one page test (review list) finds the size inside `.file-size`. Done when: `npm run lint`, `npm run check`, `npm test` pass and no `.svelte` file calls `formatSize` outside `FileSize.svelte` and the `<option>` label.
- [ ] **2. Table styling**: T1–T3 on every table, T4 guard. Files: every `.svelte` with a `<table` (listed in § Changes), new `src/lib/table-style.test.ts`. Tests: the T4 guard (and a unit case proving it flags a table missing `table-striped`, using an inline string); existing page tests still pass. Done when: SPA lint/check/test pass.
- [ ] **3. Review delete modal**: R1–R2. Files: `ReviewDeleteModal.svelte`, `routes/review/review-page.test.ts`. Tests: vitest — title `Delete File: <name>` after load, `Delete File` while loading; the details render as a table with a row per field (archive rows only for archive items); delete still works. Done when: SPA tests pass.
- [ ] **4. Upload image modal**: I1–I3. Files: `GameImages.svelte`, `routes/games/game-images.test.ts`, `tests/e2e/specs/mvp.spec.ts` (images flow: click `Upload image`, then fill the dialog). Tests: vitest — no inline form; button hidden without edit rights; button opens the modal; empty submit shows the file error and keeps the modal open; >8 MiB error; success closes the modal, resets fields and updates the list; reopening clears errors; a toasted failure (`DuplicateImage`) leaves the modal open. Done when: SPA tests pass and `pwsh scripts/test-e2e.ps1` passes.
- [ ] **5. Recent logs endpoint**: L4. Files: `LogBuffer.cs`, `AdminEndpoints.cs`, `tests/RnRomManager.Api.Tests/...` LogBuffer unit tests, `AdminEndpointsTests.cs`, `EndpointRoleMatrixTests.cs`. Tests: `Recent(20)` returns the last 20 oldest-first after 30 adds, all of them after 5 adds, `[]` when empty; integration — Admin gets 200 with the JSON array; role matrix row (anonymous 401, user 403, admin 200). Done when: `dotnet test tests/RnRomManager.Api.Tests` passes (Docker running).
- [ ] **6. Logs tab initial load**: L5. Files: `routes/admin/logs/+page.svelte`, `routes/admin/logs-page.test.ts`. Tests: vitest with the fake `EventSource` and a mocked `api.get` — fetched entries render before any stream event; streamed backlog with older ids is inserted before them in id order, duplicates skipped, newer ones appended; a Retry refetch after streaming merges without duplicates; the filter-empty text; fetch failure shows ErrorAlert with Retry and the stream still opens; existing L3 tests still pass. Done when: SPA tests pass.
- [ ] **7. N64 byte order and header reader**: N3, N4 and `SyntheticN64RomBuilder` (`sealed record` like `SyntheticGbaRomBuilder`: `Seed = 0`, `SizeBytes = 1024 * 1024`, `Title = null`, `GameCode = null`, `Version = 0`, `Crc1 = 0`, `Crc2 = 0`, `BootAddress = 0x80000400`, `CorruptMagic = false`, `ByteOrder = Z64 | V64 | N64` (default `Z64`); body is `Random(Seed)` bytes, header written over it, then converted to the requested order last by the builder's own swap code and its own `N64ByteOrder` enum: TestData references only Core, never the Systems project). New projects `src/RnRomManager.Systems.Nintendo64` and `tests/RnRomManager.Systems.Nintendo64.Tests`, added to `RnRomManager.slnx`. Files: `Nintendo64ByteOrder.cs`, `Nintendo64HeaderReader.cs`, both csproj files, `RnRomManager.slnx`, `tests/RnRomManager.TestData/SyntheticN64RomBuilder.cs`, `Nintendo64ByteOrderTests.cs`, `Nintendo64HeaderReaderTests.cs`, `SyntheticN64RomBuilderTests.cs`. Tests: byte order — the three literal N3 example vectors convert to the z64 one; a first word other than `80 37 12 40` (e.g. `80 27 07 40` and its swaps) is detected too; z64 returned unchanged; v64 and n64 convert to the z64 bytes of the same builder input; unknown magic, < 4 bytes, odd-length v64 and non-multiple-of-4 n64 return the input unchanged; input never mutated. Reader — null under 0x40, not null at 0x40; title rules; gameCode valid/invalid; mediaType and destination for known, unknown and null; version; crc1/crc2/bootAddress hex format; `headerValid` and `IsPlausible` true/false; `romSizeBytes`; random garbage never throws; a junk buffer starting `80 00 00 00` reads as `IsPlausible` true (the accepted weak check). Builder — deterministic; reads back every field. Done when: `dotnet test tests/RnRomManager.Systems.Nintendo64.Tests` passes and `dotnet build` passes with warnings as errors.
- [ ] **8. N64 handler and registration**: N1, N2, N6, `NormalizeForHashing` → `Nintendo64ByteOrder.ToBigEndian`; the N5 `InvalidHeader` wording (and any test asserting the old text); N8 badge colour; Api reference, `Dockerfile` COPY line, `AppHost` registration; `IngestSeeder.N64Dat = "Nintendo - Nintendo 64"` and an empty N64 fixture DAT; `[IngestSeeder.N64Dat] = []` in the eleven fixtures; count updates (DATs 15 → 20, systems 3 → 4, startup ids/folders, pending systems). Files: `Nintendo64Handler.cs`, `Resources/boxart/*`, csproj files, `Dockerfile`, `AppHost.cs`, `IngestSeeder.cs`, the eleven fixtures, `DatEndpointsTests.cs`, `SystemStartupTests.cs`, `IngestSeederTests.cs`, new `Nintendo64HandlerTests.cs`. Tests: `Nintendo64_Contract` (every N2 value); box art is a 512×512 PNG differing from the GameBoy ones; `ReadHeader` classifies a builder ROM as `n64`; `NormalizeForHashing` converts a v64 builder ROM to its z64 bytes. Done when: `dotnet test` passes for `tests/RnRomManager.Systems.Nintendo64.Tests`, `tests/RnRomManager.Core.Tests` and `tests/RnRomManager.Api.Tests` (Docker running), and `docker build .` succeeds.
- [ ] **9. N64 seed, integration and e2e**: add `new FixtureGame("Orbit Kart 64 (USA)", "Orbit Kart 64 (USA).z64", new SyntheticN64RomBuilder { Seed = 6401, Title = "ORBIT KART 64", GameCode = "NOKE" }.Build()) { ReleaseYear = 1998, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Racing"] }` to the Basic set and the N64 fixture DAT, written loose **byte-swapped** as `ingest/n64/01-orbit-kart.v64` (the same builder with `ByteOrder = V64`); the seeder creates `ingest/n64/`. Update every Basic/Full count it changes: `IngestSeederTests` (files +1), `SeedIngestTests` (releases +1, games +1), `mvp.spec.ts` (`Ingested 8` → `Ingested 9` ~line 99, `6 games` → `7 games` ~lines 111, 167), `docs/development.md` seed table (in step 10). Add N5 tests to `IngestPipelineTests`: an `.n64` file in `ingest/gb/` fails `UnsupportedType`; the same ROM as `.z64` and `.n64` → one Ingested, one Duplicate. Files: `IngestSeeder.cs`, `IngestSeederTests.cs`, `SeedIngestTests.cs`, `IngestPipelineTests.cs`, `mvp.spec.ts`. Tests: `SeedIngestTests` asserts Orbit Kart is stored under `roms/n64/O/` as `Orbit Kart 64 (USA).z64` with `system: n64`, `header.gameCode` `NOKE` and the z64 SHA1; e2e selects System → `Nintendo 64`, expects `system=n64` in the URL and `1 game`. Done when: those projects pass and `pwsh scripts/test-e2e.ps1` passes.
- [ ] **10. Docs**: every doc in § Changes' Docs row. Tests: none. Done when: README lists Nintendo 64 and the Refresh DATs upgrade note; `features.md` covers `ingest/n64/`, the three accepted byte orders stored as `.z64`, the upload modal and the Logs tab's instant last-20 load; `zip-format.md` lists the N64 header fields (N4); `deployment.md` has `ingest/n64/` and the Refresh DATs upgrade step; `development.md` has the project table, seed table (Orbit Kart as `.v64`), the T1–T4 table rules and the `FileSize` rule; `adding-a-system.md` uses N64 as the `NormalizeForHashing` example; and `pwsh scripts/test-all.ps1` passes.

## Decisions
- 2026-11-06 How does the Logs tab get initial data? → New `GET /api/admin/logs/recent` (last 20) fetched on load, then the live stream (user; merge rule in the reviewer decision below)
- 2026-11-06 Table header style → `thead.table-dark`, readable in both themes (user)
- 2026-11-06 N64 formats → accept `.z64`/`.v64`/`.n64`, normalise to big-endian, store `.z64` (user)
- 2026-11-06 Where does N64 live? → a new `RnRomManager.Systems.Nintendo64` project, per `docs/adding-a-system.md` (code)
- 2026-11-06 Delete modal "table" → the existing `<dl>` becomes a key/value table (code: the modal has no table today)
- 2026-11-06 Enforce the table style → a vitest source guard, not a wrapper component (code)
- 2026-11-06 Sizes inside `<option>` → stay `formatSize` text, since it can't hold markup; the upload error stays its literal string (code)
- 2026-11-06 Logs: keep the stream's older backlog → merge by id instead of dropping it, so the tab shows 20 at once and the full history after connect (reviewer)
- 2026-11-06 N64 byte order detection → by the position of `0x80` in the first word, since some carts don't start `80 37 12 40` (reviewer)
- 2026-11-06 Byte-order tests → literal vectors; the test builder swaps on its own (reviewer)
- 2026-11-06 Enter-to-submit in the upload modal → dropped (file input and select can't submit implicitly) (reviewer)
- 2026-11-06 `InvalidHeader` detail → generic wording (reviewer)
- 2026-11-06 N64 badge → `text-bg-danger` (reviewer)
- 2026-11-06 N64 test coverage → synthetic unit tests, seed + integration + one e2e check, as for GBA (code: GBA precedent)

## Kickoff prompt
```text
/spec implement 2026-11-06-ui-tables-n64-support.md
```
Starts the spec, or resumes it at the first unticked step.
