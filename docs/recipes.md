# Change recipes

Short playbooks for the changes this repo gets most often: where each change must reach, and the existing example to copy. Use them with [codemap.md](codemap.md), which has file paths and doc heading line numbers. Full detail is in [development.md](development.md) (read by section; see the codemap's Doc sections). The rules in [CONTRIBUTING.md](../CONTRIBUTING.md) still apply: every behaviour gets tests, and docs change in the same PR.

## Finding code cheaply
1. Grep `docs/codemap.md` (don't read it whole) for the thing: route, page, job, handler, file, test or doc section.
2. Grep for the symbol, then read only the line range you need.
3. Read docs by section (the codemap gives heading line numbers). Don't read `features.md` or `development.md` whole.
4. Use an `explore-haiku` subagent only for sweeps the codemap can't answer, such as "every caller of X".
5. At the end of every change, run `pwsh scripts/codemap.ps1` and commit `docs/codemap.md` with it. CI (`codemap` job) and `scripts/test-all.ps1` run `pwsh scripts/codemap.ps1 -Check` and fail on a stale map. A merge conflict in it is resolved by regenerating.

## API endpoint
- Add it to `src/RnRomManager.Api/Endpoints/<Area>Endpoints.cs`, on the area's `MapGroup`. Admin-only routes use `.RequireAuthorization(AuthSetup.AdminOnly)`, on the group or on the route.
- Errors are ProblemDetails from `Problems`: `Validation(ctx, errors)` (400, keyed by field), `NotFound`, `Forbidden`, or `Create(ctx, status, code, title)` for a coded error. A busy job lock → `DatEndpoints.JobRunning(ctx)` (409 `JobRunning`).
- A JSON body that needs exact type checks is read as `JsonElement`, as in `PUT /api/admin/rom-storage/{systemId}`.
- Tests: `tests/RnRomManager.Api.Tests/Integration/<Area>EndpointsTests.cs` (Testcontainers, so Docker must be running), plus a row in `EndpointRoleMatrixTests` for every new route.
- SPA: add the response type to `lib/api/types.ts` and call it through `lib/api/client.ts`.

## Persisted setting
- Store it in `app_settings` through `ISettingsRepository`. No migration needed; keys are at most 64 characters. `GetAsync` takes exact names; there is no prefix query.
- Copy `Core/Storage/RomStorageSettings.cs`: a singleton loaded once into memory, with DB-then-memory writes. Load it in `StartupRunner` after `database.Migrate()` and again in `BackupService.RestoreAsync` after its migrate. `ApiFactory.ResetAsync` (Api tests) reloads it between tests.
- An invalid stored value logs a warning and falls back to the default; it never throws.

## Background job
- Add a value to `JobType` in `Core/Jobs/Job.cs`. The name must be 16 characters or fewer (`jobs.type` is `VARCHAR(16)`).
- Implement `IJob` (`Type`, `Mode`, `Preflight()`, `Cancellable`, `RunAsync(JobContext, ct)`). Report progress through `JobProgress` (`SetTotal`, `SetCurrentItem`, `ItemDone`), and write one job item per unit of work.
- Start it with `JobRunner.StartAsync(...)`. A null result means the global lock is busy: return 409 `JobRunning`. A job that needs arguments gets a factory delegate registered in `AppHost`, like `StorageMigrationJobFactory`.
- While a job runs, every library edit gets `409 JobRunning`. A setting the job changes is saved by the job itself under the lock, not by the endpoint (see `StorageMigrationJob`).
- SPA: add `jobLabel(type)` and the new item outcomes (`outcomes`, `outcomeClasses`) in `lib/jobs.ts`. The footer indicator and the `/jobs` pages then pick it up.
- Tests: `<Job>Tests` in Core.Tests (temp dirs, NSubstitute), `jobs.test.ts`.

## Writing a game ZIP
- Always go through `LibraryZipWriter`: `WriteAsync` when every ROM is in the ZIP, `WriteCheckedAsync` when there are external files. It writes a tmp file, verifies it and moves it into place atomically. Never write library ZIPs directly.
- After publishing, mirror the DB row (`DbSync` / `MirrorEntry`) so `zip_size_bytes` and `zip_mtime_utc` match, or Integrity flags the game.
- Respect `RomStorage.IsPending` (409 `StorageMigrationPending`) and `handler.EmbedRomsInZip` (ROMs inside the ZIP, or beside it). Format reference: [zip-format.md](zip-format.md).

## Audit action
- Add a constant to `Core/Audit/AuditActions.cs` and include it in `AuditActions.All`, which the audit filter validates against.

## Report
- Query: a method on `Core/Reports/IReportRepository.cs`, implemented in `Data/Repositories/ReportRepository.cs` (Dapper). Copy `ListMultipleReleasesAsync` for a sortable, paged list and `ListMissingImagesAsync` / `NextMissingImageAsync` for a list plus a "next" lookup.
- Endpoint: `Endpoints/ReportEndpoints.cs` (group `/api/reports`; Admin-only reports add `AdminOnly`, like Activity).
- Page: `routes/reports/<name>/+page.svelte`, with filters, sort and page held in URL state (copy `multiple-releases`). Add a tab in `routes/reports/+layout.svelte` (with its icon in `lib/components/icons.ts`) and wrap an Admin-only page in `AdminOnly`.
- Tests: `tests/RnRomManager.Data.Tests/Database/<Name>QueryTests.cs`, `ReportEndpointsTests`, `routes/reports/reports-pages.test.ts`.

## Admin tab
- Add the tab to the tabs array in `routes/admin/+layout.svelte`, with the page in `routes/admin/<name>/+page.svelte`. Tests: `routes/admin/<name>-page.test.ts` and `admin-pages.test.ts`.

## Hotkey
- There are two action lists, and they must stay in step: `src/RnRomManager.Core/Users/Hotkeys.cs` (served by `/api/auth/me/hotkeys`, which validates saves) and `lib/hotkeys.ts` (`actions`: `id`, `name`, `defaults`, `editable`, `role`).
- Behaviour goes in `run()` in `lib/stores/hotkeys.svelte.ts`. The `HotkeysModal` lists the actions automatically.
- Tests: `HotkeysTests` (Core), `hotkeys.test.ts`, `stores/hotkeys.svelte.test.ts`.

## SPA conventions
- Numbers: `formatCount` from `lib/format.ts` ("1,234"). Sizes: the `FileSize` component.
- Icons: `<Icon name=...>`, with the names mapped to Bootstrap Icons classes in `lib/components/icons.ts`. Font Awesome is used for brands only.
- Confirmations: `ConfirmModal`. Toasts: the `toasts` store (`toasts.jobRunning(err)` handles 409 `JobRunning`). Errors: `ErrorAlert`. Admin gating: `AdminOnly`.
- Watching a job from a page: call `jobs.watch()` and `jobs.onEnded` on mount; disable action buttons while `jobs.current` is set.
- Tests sit next to their source as `*.test.ts` (rune stores: `*.svelte.test.ts`). Run them with `npx vitest run <files>`, plus `npm run lint` and `npm run check`, all in `src/RnRomManager.Web`.

## Emulator
- `static/emulator/play.js` configures EmulatorJS (vendored 4.2.3 under `static/emulator/vendor` and, for azahar only, 4.3.0-pre under `static/emulator/vendor/ejs-4.3`; do not edit vendor files). It runs in an iframe inside `EmulatorModal.svelte`, and the two talk through `postMessage` (`rnrm-emulator-*` messages). Per-system cores: `lib/systemEmulators.json`.

## New system
- Follow [adding-a-system.md](adding-a-system.md). It also needs the README supported-systems table (checked by `SupportedSystemsTableTests`).

## Running tests
- .NET, targeted: `dotnet test tests/<Project> --filter "FullyQualifiedName~<Class>"`. Append `&Category!=Integration` when Docker isn't running.
- Full suites, before a PR only: `pwsh scripts/test-all.ps1` and `pwsh scripts/test-e2e.ps1`.
