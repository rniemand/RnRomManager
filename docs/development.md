# Development

## Architecture

```
Browser (Svelte SPA) ──HTTP/JSON──> ASP.NET Core API ──Dapper──> MariaDB
                                          ├── /app/ingest   input
                                          ├── /app/roms     library (one ZIP per game)
                                          ├── /app/logs
                                          ├── /app/cache    DATs, work folders
                                          ├── /app/backups  database backups
                                          ├── /app/collections  collection cover images
                                          └── HTTPS: raw.githubusercontent.com (DATs), thumbnails.libretro.com
```

One container: the API serves the built SPA from `wwwroot` and everything under `/api`. The design is in [specs/completed/initial-application.md](../specs/completed/initial-application.md); the build order in [specs/completed/initial-application-impl.md](../specs/completed/initial-application-impl.md).

| Project | Contents |
|---|---|
| `src/RnRomManager.Api` | hosting and the startup sequence (`StartupRunner`), minimal-API endpoints per area (`Endpoints/<Area>Endpoints.cs`), auth, security headers, the job runner and scheduler, the `--healthcheck` / `--reset-password` CLI modes |
| `src/RnRomManager.Core` | domain and services: ingestion, review, library edits, integrity, DAT parsing, naming, `info.json`, ZIP reader/writer, the `ISystemHandler` contract; repository interfaces |
| `src/RnRomManager.Data` | Dapper repositories (one per table) and the DbUp migrations (embedded `.sql`) |
| `src/RnRomManager.Systems.GameBoy` | the `gb`, `gbc` and `gba` handlers: header readers, lookup tables, default box art |
| `src/RnRomManager.Systems.Nintendo64` | the `n64` handler: byte-order conversion, header reader, default box art |
| `src/RnRomManager.Systems.Nes` | the `nes` handler: iNES / NES 2.0 header reader, default box art (headered `.nes` only) |
| `src/RnRomManager.Systems.Snes` | the `snes` handler: copier-header strip, LoROM / HiROM / ExHiROM header reader, default box art |
| `src/RnRomManager.Systems.Atari` | the `a2600` handler (no header), default box art; room for later Atari systems |
| `src/RnRomManager.Web` | the SvelteKit SPA (see below) |
| `tests/RnRomManager.TestData` | synthetic GB, GBA, N64, NES and SNES ROM builders, fixture DAT writer and the seed CLI (also a library for the test projects) |
| `tests/*.Tests` | NUnit + NSubstitute; database tests use Testcontainers (`[Category("Integration")]`) |
| `tests/e2e` | Playwright + axe against the real image |

- **Library ZIPs are the source of truth**; the database is a mirror rebuilt from them ([ZIP format](zip-format.md)).
- **One job at a time:** ingestion, integrity, DAT refresh and media retry run in the background under one in-process lock, and library edits and review resolutions take the same lock synchronously (`409 JobRunning` when busy). Progress is kept in memory (`GET /api/jobs/current`); jobs and their items are stored in the database. That's why only one app instance may use a database.
- **Time** comes from an injected `TimeProvider` everywhere, so tests use `FakeTimeProvider`.
- **Outbound HTTP** goes through named clients in `Api/Http/HttpClients.cs`. The DAT and thumbnail clients retry transient failures with a 20 s timeout per attempt. The image-URL client (`ImageUrlDownloader`) has no retries and no proxy, one 15 s timeout over headers and body, a bounded 8 MiB read, and a `ConnectCallback` that resolves the host and connects only when every address passes `Core/Media/PublicAddress.IsPublic` (so redirects and DNS rebinding are checked too). The check and the timeout are one `ImageUrlPolicy` singleton; integration tests replace it to allow a loopback Kestrel stub (`StubImageServer`) and shorten the timeout.

## Getting started

Needs .NET 10 SDK, Node 24+, Docker (running) and PowerShell 7.

```powershell
pwsh scripts/setup-dev.ps1   # checks tools, restores, npm ci, starts the dev MariaDB, writes config
pwsh scripts/seed-ingest.ps1 # synthetic ROMs + fixture DATs (below)
pwsh scripts/start-dev.ps1   # API on :5080 (dotnet watch) + Vite on http://localhost:5173
```

- `setup-dev.ps1` (`-SkipDb` to leave the database alone) is safe to re-run. It keeps the dev DB password in the git-ignored `.env` (creating one if missing), starts `docker-compose.dev.yml` (MariaDB 11.4 on 3306), creates `.dev/{roms,ingest,logs,cache,backups,collections}`, and writes a git-ignored `src/RnRomManager.Api/appsettings.Development.json` with the connection string and absolute `.dev` paths. An existing settings file is kept (only missing `Paths` entries, such as `Backups` or `Collections`, are added); delete it to regenerate. It also installs the e2e project's packages and Playwright's Chromium (on Linux, run `npx playwright install-deps` once for the system libraries).
- `start-dev.ps1` runs both servers in one console; Ctrl+C stops both. `-ApiOnly` / `-WebOnly` run one. Vite proxies `/api` and `/health` to the API.
- The first API start on an empty database prints the generated `admin` password to the console once; signing in asks you to change it. Then Ingestion → Import all ingests the seeded files.

## Synthetic ingestion data

No real ROMs or downloaded DATs are needed. From any working directory, run `scripts/seed-ingest.ps1` by its path in the checkout. It defaults to the checkout's `.dev/ingest` and `.dev/cache`, with `-Set Basic`. Use `-Path` and `-CachePath` to choose other folders; relative overrides resolve from your working directory. `-Clean` empties only the selected ingest folder, including old `_failed/`, `_duplicates/` and review source files. It preserves the library and unrelated cache contents. Root/overlapping folders, the repository or its ancestors, and linked cleanup targets are rejected.

The CLI is also available directly:

```powershell
dotnet run --project tests/RnRomManager.TestData -- --ingest .dev/ingest --cache .dev/cache --set Basic
```

Both sets use deterministic builder seeds and fixture DAT version `fixture-1`. ZIP entries have a fixed timestamp; the 7z is the committed synthetic fixture. Input files are dated one minute before the injected clock so an immediate ingest does not skip them as TooRecent. Re-seeding overwrites the generated filenames and fixture DATs, while other ingest files remain unless `-Clean` is supplied. To switch from Full back to Basic, use `-Clean` to remove Full's extra source files first.

| Set | Library after first ingest | Outcomes on an empty library |
|---|---|---|
| Basic | 10 games, 12 releases (3 GB games, 2 GBC games, 1 GBA game, 1 N64 game, 1 NES game, 1 SNES game, 1 Atari 2600 game) | Ingested 12, Duplicate 1, Review 2, Failed 1; all others 0 |
| Full | 210 games, 212 releases | Ingested 212, Duplicate 1, Review 2, Failed 1; all others 0 |

Basic contains Orbit Blocks (World, Japan, World Rev 1), Quest, The (USA, Europe), Seven Zip Demo (World), Color Garden (World, dual-mode) and Color Racer (USA, GBC-only) Star Courier (USA, GBA, loose in `ingest/gba/`) and Orbit Kart 64 (USA, N64, loose in `ingest/n64/` as a byte-swapped `.v64`, so its DAT match proves the byte-order conversion) and Pixel Pioneer (USA, NES, mapper 1 with battery, loose in `ingest/nes/` as `01-pixel-pioneer.nes`) and Mode Seven Rally (USA, SNES, LoROM with 8 KiB SRAM, loose in `ingest/snes/` as `01-mode-seven-rally.smc` with a copier header, so its DAT match proves the strip) and Laser Lanes (USA, Atari 2600, 4 KiB, loose in `ingest/a2600/` as `01-laser-lanes.bin`, so its match proves `.bin` is accepted and stored under the DAT's `.a26` name). Quest is in a ZIP and Seven Zip Demo in the 7z. Color Garden is deliberately dropped in `ingest/gb/`. A byte-identical Orbit Blocks copy tests deduplication; a plausible unmatched file tests review suggestions and a second unmatched file (`93-mystery.gb`, no suggestions) is there to delete from the queue; another file has both invalid logo and header checksum. Full adds 200 generated games, split evenly between GB and GBC.

For a manual run (§15.4): seed, `start-dev.ps1`, sign in as an Admin, Ingestion → Import all, and check the run's summary against the table. Set `Sources__ThumbnailBaseUrl` to an empty string for an offline test using default art. Generated library ZIPs are under `.dev/roms`; the two unmatched files show in the Review queue and stay in ingest, and duplicate/invalid files move to their side folders.

The `SeedIngestTests` API integration tests seed both sets, let startup load the fixture DATs, start ingestion through the authenticated endpoint, and verify counts, grouping, primary revision, article display, GBC classification, the GBA game and its header, the N64 game stored as `.z64` with its big-endian SHA1, the NES game with its iNES header fields, the SNES game stored as `.sfc` without its copier header and with its header fields, the Atari 2600 game stored as `.a26` with no header, review suggestions and default art. Standard checks run with `pwsh scripts/test-all.ps1` (Docker required).

## End-to-end tests

`pwsh scripts/test-e2e.ps1` (`-SkipBuild` reuses `niemandr/rnrommanager:e2e`, `-Headed` shows the browser) builds the image for your machine's architecture, seeds `Basic` into a temp folder, starts `tests/e2e/docker-compose.e2e.yml` (MariaDB on tmpfs + the app on `127.0.0.1:18080`, `E2E_PORT` to change it) with random per-run DB and admin passwords and media fetch disabled, waits for `/health/ready`, runs Playwright, and always tears the stack down. On failure it prints the app log; the HTML report and traces are in `tests/e2e/playwright-report` and `tests/e2e/test-results` (CI uploads both).

`tests/e2e/specs/mvp.spec.ts` walks one session through sign-in + forced password change, ingesting the seed, Reports (including system drill-down), library search/filters/views, game detail + ZIP/ROM downloads, resolving the review item, the info.json modal, Images tab, Admin edit/move/delete and the Integrity/Admin pages. It checks ingest audit rows and scrolling after modal-driven release deletion, adds, edits and deletes a release note, uses and rebinds a keyboard shortcut, and runs **Back up now** (the backups folder is the image's anonymous volume, so the e2e compose file needs no mount). Every page visited is scanned with axe (`specs/axe.ts`) and fails on `serious` or `critical` WCAG A/AA violations.

## Web app structure

The SPA in `src/RnRomManager.Web` is SvelteKit (static adapter, Svelte 5 runes) with Bootstrap 5.3. Import shared code through the `#lib/…` subpath import (TypeScript modules with a `.js` extension, e.g. `#lib/api/client.js`; components as `#lib/components/Icon.svelte`).

- `src/lib/api/client.ts` is the only way to call the API. It fetches `/api/auth/antiforgery` once and sends the `XSRF-TOKEN` cookie as `X-XSRF-TOKEN` on every POST/PATCH/DELETE. A `401` redirects to `/login?returnUrl=…` (pass `noRedirect` to handle it yourself), and `403 PasswordChangeRequired` redirects to `/change-password`. Other failures throw an `ApiError` carrying the ProblemDetails `code`, `title` and field `errors`.
- `src/lib/stores/`: `hotkeys` (the user's shortcut bindings, loaded once signed in with the password changed, and the `window` keydown handler the root layout installs), `auth` (the signed-in user), `jobs` (polls `/api/jobs/current` every 1.5 s while a page watches and a job runs), `toasts` (`toasts.jobRunning(error)` shows the standard `409 JobRunning` toast).
- `src/lib/components/`: `Icon` (semantic names in `icons.ts`; never use icon classes directly), `Loading`, `EmptyState`, `ErrorAlert`, `ConfirmModal` (Bootstrap modal with focus trapping; `size="lg"` for a wide form; `confirmDisabled` disables only the confirm button; without `onconfirm` it's an info modal with only Close), `Pagination` (Previous / Page x of y / Next, optional rows-per-page selector; sizes persist per table through `src/lib/pageSize.ts`), `SystemBadge` (system icon + name in a fixed colour per system), `AdminOnly` (shows "Not allowed" without the `admin` role), `Navbar`, `ThemeToggle`, `ToastHost`, `CopyButton` (`variant="icon"`, the default, is a bare icon link; `variant="button"` is a small outlined button reading Copy, used by the info.json dialog), `GameTypeahead` (debounced same-system game picker for move/merge/attach), `GameAdminModal` (the Game detail edit actions), `GameImages` (the Game detail gallery with the upload dialog, cover and delete), `FileSize` (a size with its byte count as tooltip), `ReviewDeleteModal` (shows a review item's details before deleting its file, shared by the queue and the item page), `IngestFileTable` (one system's pending or failed files on the Ingestion page: checkboxes, paging, optional Reason column, row delete buttons), `NotAllowed`, `RatingControl` (stars + favourite, optimistic, with a read-only mode; saves through `src/lib/rating.ts`) `GlobalSearch` (the Ctrl+K search modal, mounted in the root layout), `ReleaseNoteModal` (a release's note: View/Edit/Preview, byte counter, inline delete) and `HotkeysModal` (the keyboard shortcuts behind the navbar's (?) button, with key capture).
- Pages live in `src/routes/` (`/`, `games/[id]`, `ingestion`, `jobs/[id]`, `review`, `review/[id]`, `integrity`, `reports/*`, `admin/*`, including `admin/audit`). Form rules mirrored from the API live next to them in `src/lib` (`passwordPolicy.ts`, `gameEdit.ts`, `reviewForm.ts`, `libraryQuery.ts`, `ingestDelete.ts` (the Ingestion page's delete requests in batches of 500 and their toasts), and `hotkeys.ts`, which mirrors `Core/Users/Hotkeys.cs`: its tests repeat the C# cases, so change both together).
- `src/lib/markdown.ts` renders release notes: `marked` (GitHub-flavoured Markdown) then `DOMPurify`, with links forced to `target="_blank" rel="noopener noreferrer"`. User Markdown never reaches `{@html}` any other way.
- **Key handlers that must beat Bootstrap:** Svelte 5 delegates `onkeydown` to the app root, so `stopPropagation()` in it runs after listeners on ancestors such as `.modal` (Bootstrap's Escape). Attach those handlers to the element itself (`{@attach}`), as `HotkeysModal`'s key capture does.
- **Tables** (T1–T4): every `<table>` has `table table-sm table-striped table-hover` (plus `align-middle mb-0` in a list) inside `card` > `div.table-responsive`; every `<thead>` is exactly `class="table-dark"`, with `<th scope="col">` headers; key/value tables (two columns, no `<thead>`) use `<th scope="row">` + `<td>` rows. `src/lib/table-style.test.ts` fails, naming the file, when a table or header misses these classes; numbers and sizes right-aligned and `text-nowrap`, dates `text-nowrap`; long file names `text-truncate` (max-width 32rem) with a `title`; row actions right-aligned `btn-sm` icon buttons with an `aria-label`; an empty table shows `EmptyState`.
- **Modal teardown:** every Bootstrap modal must call `releaseModal(element)` from `src/lib/modal.ts` before `dispose()` on destroy. It clears body scroll locks, inline overflow/padding and backdrops when no other shown modal remains. `ConfirmModal` and `GlobalSearch` own this rule, so callers can close and immediately navigate/reload. Unit tests seed body state directly because SPA tests mock Bootstrap; the e2e checks real scrolling after deleting a release.
- **Modals** (M1): every modal is a `ConfirmModal` (or, for the global search, uses the same options): `backdrop: 'static'`, `keyboard: true`, so a background click never closes it but Escape, × and the footer button do.
- Styling is Bootstrap classes only. `src/app.css` holds the few overrides: dark-theme outline buttons use Bootstrap's text-emphasis colours, because the defaults fail WCAG AA contrast there (the e2e axe scans check both themes); the active navbar link's accent; and the blur behind the global search.
- `src/lib/format.ts` formats times in the browser's local time zone and sizes in KiB/MiB. Every file size on a page renders through `<FileSize bytes={…} />` (monospace, the exact byte count as its tooltip); only text that can't hold markup, such as an `<option>` label, calls `formatSize` directly. `src/lib/guard.ts` holds the routing rules the root layout applies on every navigation.
- Bootstrap's JavaScript (navbar collapse, dropdowns, modals, tooltips) comes from its bundled build through `src/lib/bootstrap.ts`, so there's no separate Popper dependency. Tooltips are created only through the `use:tooltip={text}` action in `src/lib/tooltip.ts`.
- The favicon is `static/favicon.svg` (Bootstrap Icons `controller`, MIT) with a 32×32 `static/favicon-32.png` fallback rendered from it.

Run `npm run lint`, `npm run check`, `npm test` and `npm run build` in `src/RnRomManager.Web` before pushing (`scripts/test-all.ps1` runs them too).

## Reports and curation helpers

Reports live under `routes/reports/`: Library Size (`?system=` drill-down), Missing Images and Duplicates, backed by `Core/Reports`, `ReportRepository` and `/api/reports`. `src/lib/treemap.ts` is a dependency-free squarified layout with proportional area and no overlaps; the system view caps graphics at 300 games plus others, while the table remains complete. `systemColours.ts` supplies both tiles and SystemBadge. The SVG has a labelled `group` role: `img` containing clickable links fails axe nested-interactive. Tiles are pointer shortcuts with aria-hidden and negative tabindex; the table supplies keyboard/screen-reader links.

`JsonTree` uses native details/summary, open at depths 0 and 1, with expand/collapse controls, a named root and padded summary touch targets. Its root takes an optional `actions` snippet (right end of the Expand/Collapse row) and `paneClass`/`paneStyle` for a wrapper around the tree only, which `InfoJsonModal` uses for the scrolling pane. `InfoJsonModal` calls `api.getText`, preserving invalid JSON as raw text. `/api/games/{id}/info-json` uses `LibraryFiles.ReadEntry` to buffer and limit bytes before sending a 200.

`googleSearch.ts` owns explicit-click Google query construction. `review.ts` shares the delete request and error handling between confirmation and Shift+click paths; archive entries always confirm. `reviewFilter.ts` parses, serializes and saves review filters under `rnrm.review.filter`. URL changes on Review, Game tabs and Auditing use `goto(url, { replace: true, reset: false })` (SvelteKit 3), preserving focus/scroll without adding history entries. Review's bare URL restores its saved filter; explicit URL values win. The game's load effect keys only on id and keeps both panels mounted.

## Backups, notes and hotkeys

- **Migrations** 0015 adds `app_settings` (`name` PK, `value`; runtime settings, missing row = default) and 0016 adds `users.hotkeys` (JSON overrides, `NULL` = defaults).
- **Backups:** `Core/Backups` holds `BackupService` (create with its own lock, temp file + rename, ` (2)` names, retention, the in-memory last result, settings and the change signal), `BackupRestorer` (the startup restore rules) and `BackupSettings`; `Data/MySqlBackupStore` dumps every base table inside `START TRANSACTION WITH CONSISTENT SNAPSHOT` and restores with `FOREIGN_KEY_CHECKS = 0` and 500-row inserts, converting values back by each column's type. `Api/BackupScheduler` waits on `ScheduleCalculator.NextDaily` with the injected `TimeProvider` and wakes when settings are saved. `StartupRunner` calls the restorer between the database wait and the migrations. The Api tests' stub settings turn scheduled backups off, so stub-database tests never connect to MariaDB.
- **Notes:** `LibraryZipWriter` copies `notes/<listed rom>.md` on every write and takes `RemovedEntries`; `LibraryEditor` carries notes on move and merge and saves/deletes them under the job lock through the normal write + mirror path (so `zip_size_bytes`/`zip_mtime_utc` stay in sync without touching `updatedAt`).
- **Hotkeys:** `Core/Users/Hotkeys.cs` holds the action table and rules; the SPA mirror is `src/lib/hotkeys.ts`. Adding an action means a row in both tables, a case in the store's `run`, and docs in [features](features.md#keyboard-shortcuts).

## File action auditing

Migration 0014 adds `audit_events`: auto-increment id, occurred_at, action, optional system/game/user/username/job, path, target_path and details. Time, action/time, system/time and user/time indexes support the newest-first queries; no foreign keys preserve history after deletion. Text is truncated to its column length on insert. `IAuditRepository.ListAsync` applies validated filters before count and paging. `/api/admin/audit` and its `/actions` endpoint require Admin; `/admin/audit` keeps filters and page in its URL and converts local date boundaries to UTC instants.

`IAuditLog.Record` stamps an event with injected TimeProvider and synchronously TryWrites a bounded 10,000-event channel. It never blocks or throws; full queues or internal failures warn and drop. `AuditWriter` inserts in the background, logging/dropping failed inserts and continuing. It is registered before JobShutdownService so reverse shutdown order finishes jobs before draining the log (best effort within the host timeout). Tests await `AuditWriter.FlushAsync` before reading rows. `AuditLog.Retention` is 30 days; Scheduler purges at startup and with the 00:05 UTC log purge, independently catching audit failures.

Build `AuditActor` once per request from claims or once per job from the requester lookup (a failed/missing lookup warns, keeps the user id and starts with a null username); pass it through file-changing services. Record immediately after a successful file change, including when the subsequent DB mirror fails. Keep each action's single writer; no-op metadata/cover changes, ratings, integrity and temporary cleanup are excluded. Review resolves/dismissals use the pipeline's row, not an extra review row. `AuditPaths` turns absolute paths under configured roots into `roms/` or `ingest/`; archive sources use archive + entry instead of cache/work, and outside-root paths use filename only. Stored relative ZIP paths use `AuditPaths.Library`.
