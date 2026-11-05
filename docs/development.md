# Development

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

For a manual run, seed first, then start the API with its configured ingest/cache paths pointing at those roots (see [configuration](configuration.md)). Set `Sources__ThumbnailBaseUrl` to an empty string for an offline test using default art. Log in as an Admin, change the initial password if required, and POST `/api/jobs/ingest` with the session's antiforgery header. Poll `/api/jobs/{jobId}` and check the summary against the table. Generated library ZIPs are under `.dev/roms`; the unmatched file stays in ingest, and duplicate/invalid files move to their side folders. `start-dev.ps1` and the ingestion UI arrive in S48 and S44 respectively.

The `SeedIngestTests` API integration tests seed both sets, let startup load the fixture DATs, start ingestion through the authenticated endpoint, and verify counts, grouping, primary revision, article display, GBC classification, review suggestions and default art. Standard checks run with `pwsh scripts/test-all.ps1` (Docker required).

## Web app structure

The SPA in `src/RnRomManager.Web` is SvelteKit (static adapter, Svelte 5 runes) with Bootstrap 5.3. Import shared code through the `#lib/…` subpath import (TypeScript modules with a `.js` extension, e.g. `#lib/api/client.js`; components as `#lib/components/Icon.svelte`).

- `src/lib/api/client.ts` is the only way to call the API. It fetches `/api/auth/antiforgery` once and sends the `XSRF-TOKEN` cookie as `X-XSRF-TOKEN` on every POST/PATCH/DELETE. A `401` redirects to `/login?returnUrl=…` (pass `noRedirect` to handle it yourself), and `403 PasswordChangeRequired` redirects to `/change-password`. Other failures throw an `ApiError` carrying the ProblemDetails `code`, `title` and field `errors`.
- `src/lib/stores/`: `auth` (the signed-in user), `jobs` (polls `/api/jobs/current` every 1.5 s while a page watches and a job runs), `toasts` (`toasts.jobRunning(error)` shows the standard `409 JobRunning` toast).
- `src/lib/components/`: `Icon` (semantic names in `icons.ts`; never use icon classes directly), `Loading`, `EmptyState`, `ErrorAlert`, `ConfirmModal` (Bootstrap modal with focus trapping), `AdminOnly` (shows "Not allowed" to a `User`), `Navbar`, `ThemeToggle`, `ToastHost`.
- `src/lib/format.ts` formats times in the browser's local time zone and sizes in KiB/MiB. `src/lib/guard.ts` holds the routing rules the root layout applies on every navigation.
- Bootstrap's JavaScript (navbar collapse, dropdowns, modals) comes from its bundled build through `src/lib/bootstrap.ts`, so there's no separate Popper dependency.

Run `npm run lint`, `npm run check`, `npm test` and `npm run build` in `src/RnRomManager.Web` before pushing (`scripts/test-all.ps1` runs them too).
