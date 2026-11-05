# Development

## Getting started

Needs .NET 10 SDK, Node 24+, Docker (running) and PowerShell 7.

```powershell
pwsh scripts/setup-dev.ps1   # checks tools, restores, npm ci, starts the dev MariaDB, writes config
pwsh scripts/seed-ingest.ps1 # synthetic ROMs + fixture DATs (below)
pwsh scripts/start-dev.ps1   # API on :5080 (dotnet watch) + Vite on http://localhost:5173
```

- `setup-dev.ps1` (`-SkipDb` to leave the database alone) is safe to re-run. It keeps the dev DB password in the git-ignored `.env` (creating one if missing), starts `docker-compose.dev.yml` (MariaDB 11.4 on 3306), creates `.dev/{roms,ingest,logs,cache}`, and writes a git-ignored `src/RnRomManager.Api/appsettings.Development.json` with the connection string and absolute `.dev` paths. An existing settings file is kept; delete it to regenerate. Playwright browsers are installed once the `tests/e2e` project exists.
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
| Basic | 5 games, 7 releases (3 GB games, 2 GBC games) | Ingested 7, Duplicate 1, Review 1, Failed 1; all others 0 |
| Full | 205 games, 207 releases | Ingested 207, Duplicate 1, Review 1, Failed 1; all others 0 |

Basic contains Orbit Blocks (World, Japan, World Rev 1), Quest, The (USA, Europe), Seven Zip Demo (World), Color Garden (World, dual-mode) and Color Racer (USA, GBC-only). Quest is in a ZIP and Seven Zip Demo in the 7z. Color Garden is deliberately dropped in `ingest/gb/`. A byte-identical Orbit Blocks copy tests deduplication; a plausible unmatched file tests review suggestions; another file has both invalid logo and header checksum. Full adds 200 generated games, split evenly between GB and GBC.

For a manual run (§15.4): seed, `start-dev.ps1`, sign in as an Admin, Ingestion → Start, and check the run's summary against the table. Set `Sources__ThumbnailBaseUrl` to an empty string for an offline test using default art. Generated library ZIPs are under `.dev/roms`; the unmatched file shows in the Review queue and stays in ingest, and duplicate/invalid files move to their side folders.

The `SeedIngestTests` API integration tests seed both sets, let startup load the fixture DATs, start ingestion through the authenticated endpoint, and verify counts, grouping, primary revision, article display, GBC classification, review suggestions and default art. Standard checks run with `pwsh scripts/test-all.ps1` (Docker required).

## Web app structure

The SPA in `src/RnRomManager.Web` is SvelteKit (static adapter, Svelte 5 runes) with Bootstrap 5.3. Import shared code through the `#lib/…` subpath import (TypeScript modules with a `.js` extension, e.g. `#lib/api/client.js`; components as `#lib/components/Icon.svelte`).

- `src/lib/api/client.ts` is the only way to call the API. It fetches `/api/auth/antiforgery` once and sends the `XSRF-TOKEN` cookie as `X-XSRF-TOKEN` on every POST/PATCH/DELETE. A `401` redirects to `/login?returnUrl=…` (pass `noRedirect` to handle it yourself), and `403 PasswordChangeRequired` redirects to `/change-password`. Other failures throw an `ApiError` carrying the ProblemDetails `code`, `title` and field `errors`.
- `src/lib/stores/`: `auth` (the signed-in user), `jobs` (polls `/api/jobs/current` every 1.5 s while a page watches and a job runs), `toasts` (`toasts.jobRunning(error)` shows the standard `409 JobRunning` toast).
- `src/lib/components/`: `Icon` (semantic names in `icons.ts`; never use icon classes directly), `Loading`, `EmptyState`, `ErrorAlert`, `ConfirmModal` (Bootstrap modal with focus trapping), `AdminOnly` (shows "Not allowed" to a `User`), `Navbar`, `ThemeToggle`, `ToastHost`.
- `src/lib/format.ts` formats times in the browser's local time zone and sizes in KiB/MiB. `src/lib/guard.ts` holds the routing rules the root layout applies on every navigation.
- Bootstrap's JavaScript (navbar collapse, dropdowns, modals) comes from its bundled build through `src/lib/bootstrap.ts`, so there's no separate Popper dependency.

Run `npm run lint`, `npm run check`, `npm test` and `npm run build` in `src/RnRomManager.Web` before pushing (`scripts/test-all.ps1` runs them too).
