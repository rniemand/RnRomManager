# Development

## Architecture

```
Browser (Svelte SPA) ──HTTP/JSON──> ASP.NET Core API ──Dapper──> MariaDB
                                          ├── /app/ingest   input
                                          ├── /app/roms     library (one ZIP per game)
                                          ├── /app/logs
                                          ├── /app/cache    DATs, work folders
                                          └── HTTPS: raw.githubusercontent.com (DATs), thumbnails.libretro.com
```

One container: the API serves the built SPA from `wwwroot` and everything under `/api`. The design is in [specs/completed/initial-application.md](../specs/completed/initial-application.md); the build order in [specs/completed/initial-application-impl.md](../specs/completed/initial-application-impl.md).

| Project | Contents |
|---|---|
| `src/RnRomManager.Api` | hosting and the startup sequence (`StartupRunner`), minimal-API endpoints per area (`Endpoints/<Area>Endpoints.cs`), auth, security headers, the job runner and scheduler, the `--healthcheck` / `--reset-password` CLI modes |
| `src/RnRomManager.Core` | domain and services: ingestion, review, library edits, integrity, DAT parsing, naming, `info.json`, ZIP reader/writer, the `ISystemHandler` contract; repository interfaces |
| `src/RnRomManager.Data` | Dapper repositories (one per table) and the DbUp migrations (embedded `.sql`) |
| `src/RnRomManager.Systems.GameBoy` | the `gb`, `gbc` and `gba` handlers: header readers, lookup tables, default box art |
| `src/RnRomManager.Web` | the SvelteKit SPA (see below) |
| `tests/RnRomManager.TestData` | synthetic GB and GBA ROM builders, fixture DAT writer and the seed CLI (also a library for the test projects) |
| `tests/*.Tests` | NUnit + NSubstitute; database tests use Testcontainers (`[Category("Integration")]`) |
| `tests/e2e` | Playwright + axe against the real image |

- **Library ZIPs are the source of truth**; the database is a mirror rebuilt from them ([ZIP format](zip-format.md)).
- **One job at a time:** ingestion, integrity, DAT refresh and media retry run in the background under one in-process lock, and library edits and review resolutions take the same lock synchronously (`409 JobRunning` when busy). Progress is kept in memory (`GET /api/jobs/current`); jobs and their items are stored in the database. That's why only one app instance may use a database.
- **Time** comes from an injected `TimeProvider` everywhere, so tests use `FakeTimeProvider`.

## Getting started

Needs .NET 10 SDK, Node 24+, Docker (running) and PowerShell 7.

```powershell
pwsh scripts/setup-dev.ps1   # checks tools, restores, npm ci, starts the dev MariaDB, writes config
pwsh scripts/seed-ingest.ps1 # synthetic ROMs + fixture DATs (below)
pwsh scripts/start-dev.ps1   # API on :5080 (dotnet watch) + Vite on http://localhost:5173
```

- `setup-dev.ps1` (`-SkipDb` to leave the database alone) is safe to re-run. It keeps the dev DB password in the git-ignored `.env` (creating one if missing), starts `docker-compose.dev.yml` (MariaDB 11.4 on 3306), creates `.dev/{roms,ingest,logs,cache}`, and writes a git-ignored `src/RnRomManager.Api/appsettings.Development.json` with the connection string and absolute `.dev` paths. An existing settings file is kept; delete it to regenerate. It also installs the e2e project's packages and Playwright's Chromium (on Linux, run `npx playwright install-deps` once for the system libraries).
- `start-dev.ps1` runs both servers in one console; Ctrl+C stops both. `-ApiOnly` / `-WebOnly` run one. Vite proxies `/api` and `/health` to the API.
- The first API start on an empty database prints the generated `admin` password to the console once; signing in asks you to change it. Then Ingestion → Start ingests the seeded files.

## Synthetic ingestion data

No real ROMs or downloaded DATs are needed. From any working directory, run `scripts/seed-ingest.ps1` by its path in the checkout. It defaults to the checkout's `.dev/ingest` and `.dev/cache`, with `-Set Basic`. Use `-Path` and `-CachePath` to choose other folders; relative overrides resolve from your working directory. `-Clean` empties only the selected ingest folder, including old `_failed/`, `_duplicates/` and review source files. It preserves the library and unrelated cache contents. Root/overlapping folders, the repository or its ancestors, and linked cleanup targets are rejected.

The CLI is also available directly:

```powershell
dotnet run --project tests/RnRomManager.TestData -- --ingest .dev/ingest --cache .dev/cache --set Basic
```

Both sets use deterministic builder seeds and fixture DAT version `fixture-1`. ZIP entries have a fixed timestamp; the 7z is the committed synthetic fixture. Input files are dated one minute before the injected clock so an immediate ingest does not skip them as TooRecent. Re-seeding overwrites the generated filenames and fixture DATs, while other ingest files remain unless `-Clean` is supplied. To switch from Full back to Basic, use `-Clean` to remove Full's extra source files first.

| Set | Library after first ingest | Outcomes on an empty library |
|---|---|---|
| Basic | 6 games, 8 releases (3 GB games, 2 GBC games, 1 GBA game) | Ingested 8, Duplicate 1, Review 1, Failed 1; all others 0 |
| Full | 206 games, 208 releases | Ingested 208, Duplicate 1, Review 1, Failed 1; all others 0 |

Basic contains Orbit Blocks (World, Japan, World Rev 1), Quest, The (USA, Europe), Seven Zip Demo (World), Color Garden (World, dual-mode) and Color Racer (USA, GBC-only) and Star Courier (USA, GBA, loose in `ingest/gba/`). Quest is in a ZIP and Seven Zip Demo in the 7z. Color Garden is deliberately dropped in `ingest/gb/`. A byte-identical Orbit Blocks copy tests deduplication; a plausible unmatched file tests review suggestions; another file has both invalid logo and header checksum. Full adds 200 generated games, split evenly between GB and GBC.

For a manual run (§15.4): seed, `start-dev.ps1`, sign in as an Admin, Ingestion → Start, and check the run's summary against the table. Set `Sources__ThumbnailBaseUrl` to an empty string for an offline test using default art. Generated library ZIPs are under `.dev/roms`; the unmatched file shows in the Review queue and stays in ingest, and duplicate/invalid files move to their side folders.

The `SeedIngestTests` API integration tests seed both sets, let startup load the fixture DATs, start ingestion through the authenticated endpoint, and verify counts, grouping, primary revision, article display, GBC classification, the GBA game and its header, review suggestions and default art. Standard checks run with `pwsh scripts/test-all.ps1` (Docker required).

## End-to-end tests

`pwsh scripts/test-e2e.ps1` (`-SkipBuild` reuses `niemandr/rnrommanager:e2e`, `-Headed` shows the browser) builds the image for your machine's architecture, seeds `Basic` into a temp folder, starts `tests/e2e/docker-compose.e2e.yml` (MariaDB on tmpfs + the app on `127.0.0.1:18080`, `E2E_PORT` to change it) with random per-run DB and admin passwords and media fetch disabled, waits for `/health/ready`, runs Playwright, and always tears the stack down. On failure it prints the app log; the HTML report and traces are in `tests/e2e/playwright-report` and `tests/e2e/test-results` (CI uploads both).

`tests/e2e/specs/mvp.spec.ts` walks one session through the MVP in order: sign-in + forced password change, ingesting the seed, library search/filters/views, game detail + ZIP/ROM downloads, resolving the review item, Admin edit/move/delete, and the Integrity/Admin pages. Every page visited is scanned with axe (`specs/axe.ts`) and fails on `serious` or `critical` WCAG A/AA violations.

## Web app structure

The SPA in `src/RnRomManager.Web` is SvelteKit (static adapter, Svelte 5 runes) with Bootstrap 5.3. Import shared code through the `#lib/…` subpath import (TypeScript modules with a `.js` extension, e.g. `#lib/api/client.js`; components as `#lib/components/Icon.svelte`).

- `src/lib/api/client.ts` is the only way to call the API. It fetches `/api/auth/antiforgery` once and sends the `XSRF-TOKEN` cookie as `X-XSRF-TOKEN` on every POST/PATCH/DELETE. A `401` redirects to `/login?returnUrl=…` (pass `noRedirect` to handle it yourself), and `403 PasswordChangeRequired` redirects to `/change-password`. Other failures throw an `ApiError` carrying the ProblemDetails `code`, `title` and field `errors`.
- `src/lib/stores/`: `auth` (the signed-in user), `jobs` (polls `/api/jobs/current` every 1.5 s while a page watches and a job runs), `toasts` (`toasts.jobRunning(error)` shows the standard `409 JobRunning` toast).
- `src/lib/components/`: `Icon` (semantic names in `icons.ts`; never use icon classes directly), `Loading`, `EmptyState`, `ErrorAlert`, `ConfirmModal` (Bootstrap modal with focus trapping; `size="lg"` for a wide form; without `onconfirm` it's an info modal with only Close), `Pagination` (Previous / Page x of y / Next, optional rows-per-page selector; sizes persist per table through `src/lib/pageSize.ts`), `SystemBadge` (system icon + name in a fixed colour per system), `AdminOnly` (shows "Not allowed" to a `User`), `Navbar`, `ThemeToggle`, `ToastHost`, `CopyButton`, `GameTypeahead` (debounced same-system game picker for move/merge/attach), `GameAdminModal` (the Game detail admin actions), `RatingControl` (stars + favourite, optimistic, with a read-only mode; saves through `src/lib/rating.ts`) and `GlobalSearch` (the Ctrl+K search modal, mounted in the root layout).
- Pages live in `src/routes/` (`/`, `games/[id]`, `ingestion`, `jobs/[id]`, `review`, `review/[id]`, `integrity`, `admin/*`). Form rules mirrored from the API live next to them in `src/lib` (`passwordPolicy.ts`, `gameEdit.ts`, `reviewForm.ts`, `libraryQuery.ts`).
- **Tables** (T1): `table table-sm table-hover align-middle mb-0` inside `card` > `div.table-responsive`; header row `small text-uppercase text-body-secondary`; numbers and sizes right-aligned and `text-nowrap`, dates `text-nowrap`; long file names `text-truncate` (max-width 32rem) with a `title`; row actions right-aligned `btn-sm` icon buttons with an `aria-label`; an empty table shows `EmptyState`.
- **Modals** (M1): every modal is a `ConfirmModal` (or, for the global search, uses the same options): `backdrop: 'static'`, `keyboard: true`, so a background click never closes it but Escape, × and the footer button do.
- Styling is Bootstrap classes only. `src/app.css` holds the few overrides: dark-theme outline buttons use Bootstrap's text-emphasis colours, because the defaults fail WCAG AA contrast there (the e2e axe scans check both themes); the active navbar link's accent; and the blur behind the global search.
- `src/lib/format.ts` formats times in the browser's local time zone and sizes in KiB/MiB. `src/lib/guard.ts` holds the routing rules the root layout applies on every navigation.
- Bootstrap's JavaScript (navbar collapse, dropdowns, modals, tooltips) comes from its bundled build through `src/lib/bootstrap.ts`, so there's no separate Popper dependency. Tooltips are created only through the `use:tooltip={text}` action in `src/lib/tooltip.ts`.
- The favicon is `static/favicon.svg` (Bootstrap Icons `controller`, MIT) with a 32×32 `static/favicon-32.png` fallback rendered from it.

Run `npm run lint`, `npm run check`, `npm test` and `npm run build` in `src/RnRomManager.Web` before pushing (`scripts/test-all.ps1` runs them too).
