---
title: ZIP Compression, Missing Metadata Report and UI Polish
status: ready
created: 2026-11-19
---

# ZIP Compression, Missing Metadata Report and UI Polish

## Summary
Admins can set a per-system **ZIP compression** level (Store / Med / High) on the ROM Zip Config tab. Changing it starts a background job that repacks every game ZIP of that system. This is meant for systems like Nintendo DS, where the ZIP only bundles files, so ROM access stays fast. A new Admin-only **Missing Metadata** report lists games with no year, developer or publisher. It has a "next" shortcut. The emulator gets on-screen touch controls and a screenshot-to-Snap button. The SPA gets mobile and polish fixes: the navbar closes after navigation, tab lists scroll, the library shows 3 cards per row on phones, shortcuts get Run buttons, the delete modal body scrolls, the Review page gets a remaining-items counter, and smaller fixes.

## Out of scope
- Compression for systems whose ROMs are stored beside the ZIP (Switch, 3DS, disc systems, DS while beside ZIP): not editable (Z5).
- Per-game tracking of compression levels: a cancelled or failed repack leaves a mix of levels (all readable), resumed by **Repack again** (Z9), which rewrites every game again.
- Auto-resuming a repack after a restart.
- Recompressing ZIPs of other kinds (backups, log archives, play-disc ZIPs).
- Touch controls for an emulator that isn't EmulatorJS (E1).
- Screenshots for non-Admins (the image upload is Admin only).
- A game-page **Next** button for Missing Images (only Missing Metadata gets one, M7).

## Behaviour

### ZIP compression
- **Z1** New `enum ZipCompression { Store, Med, High }` in `Core/Library`. API strings are the enum names. The SPA shows "Store", "Medium" and "High".
- **Z2** `LibraryZipWriter.CompressionFor(entryName, sizeBytes, fastAbove, compression)` picks the level for every entry, including `info.json`, notes, images and saves:
  - `Store` → `CompressionLevel.NoCompression` for every entry.
  - `Med` → exactly today's rule: `Fastest` for a `roms/` or `saves/` entry larger than `fastAbove`, else `Optimal`.
  - `High` → `CompressionLevel.SmallestSize` for every entry.
  - The writer gets the level through its constructor: `LibraryZipWriter(InfoJsonReader reader, ZipCompression compression = ZipCompression.Med)`. The `info.json` entry (today hard-coded `Optimal`) follows the rule too. ZIPs are read the same whatever their level, so nothing on the read side changes.
- **Z3** The setting is stored in `app_settings` as `storage.zipCompression.<systemId>` = `Store`/`Med`/`High`. No migration. The key is at most 40 characters and fits `VARCHAR(64)`. No key = `Med`, today's behaviour, so nothing changes until an Admin acts. An unknown value is ignored with a warning and `Med` is used. The existing `RomStorageSettings` / `IRomStorageSettings` (already loaded at startup and after a backup restore, and reloaded by `ApiFactory.ResetAsync`) grows two members:
  - `ZipCompression Compression(string systemId)`
  - `Task SetCompressionAsync(string systemId, ZipCompression level, CancellationToken)`, which writes the DB first, then memory.
  - `LoadAsync` also loads the compression key of **every** registered handler, by name.
  A value restored from a backup that differs from the ZIPs on disk is harmless: the levels just differ until the next write or repack.
- **Z4** Every `new LibraryZipWriter(` site passes `settings.Compression(<game system id>)`: `IngestPipeline`, `GameMediaRetry`, `LibraryEditor` (+ `.External`), `StorageMigrationJob` (+ `.Embed`) and the new repack job. Implement greps `new LibraryZipWriter(` so no site is missed. Each class takes `IRomStorageSettings` as an optional constructor dependency; null (tests) means `Med`. So any edit, save, image upload or storage move of a game rewrites its ZIP at the system's current level. A beside-ZIP system's writes use its stored level too. That only touches `info.json`, images and notes, and is harmless.
- **Z5** A system is **compression-configurable** when its effective `EmbedRomsInZip` is true. If DS is moved beside its ZIP, its stored level is kept, but it can't be edited until DS is moved back into the ZIP.
- **Z6** `GET /api/admin/rom-storage` (B12 of the ROM Zip Storage spec) adds two fields per system: `zipCompression` (`Store`/`Med`/`High`) and `compressionConfigurable` (bool, Z5).
- **Z7** `PUT /api/admin/zip-compression/{systemId}` (Admin only), body `{ "level": "Store" | "Med" | "High" }`. The body is read as `JsonElement`, as in `PUT /api/admin/rom-storage/{systemId}`. Checks run in this order:
  - unknown system → `404` (`Problems.NotFound`);
  - missing body, or `level` not one of the three exact names → `400` `Problems.Validation` keyed `level`;
  - system not compression-configurable → `400` ProblemDetails `code` `NotConfigurable`.
  
  Then:
  - `level` equal to the current value → `204`, nothing saved, no job;
  - otherwise the repack job starts with that change through `JobRunner.StartAsync(trigger Manual, requestedBy caller)` → `202 { jobId }`. If the lock is busy → `409 JobRunning` and **nothing is saved**.
- **Z8** `POST /api/admin/zip-compression/{systemId}/repack` (Admin only) uses the same `404` / `NotConfigurable` checks. It starts the repack job with no change → `202 { jobId }`, or `409 JobRunning`. A system with no games still gets a job, which ends with 0 items.
- **Z9** **Repack job** `ZipRepackJob` (`JobType.ZipRepack`; the name fits `jobs.type VARCHAR(16)`; cancellable). It carries `(systemId, ZipCompression? level)` through a `ZipRepackJobFactory` delegate registered in `AppHost`, as `StorageMigrationJobFactory` is.
  1. Its first action, under the lock: if `level` is given and differs from the current value → `SetCompressionAsync`, then audit `ZipCompressionChanged` (new; joins `AuditActions.All`) with the system id, Details `Med → Store`, and the requesting Admin. If `level` equals the current value (a race), nothing is saved or audited, and the job still repacks every game, like Repack again. If the save fails, the job fails before any repack and the setting is unchanged.
  2. It lists every game of the system ordered by ZIP path (`IGameRepository.ListIdsBySystemAsync(systemId)` or an existing equivalent Implement finds) and creates one job item per game.
  3. Per game:
     - Re-open the ZIP. A missing ZIP, or one that can't be opened, or whose `info.json` can't be read → `Error` `IoError`, nothing written. A game that's pending a storage move (`RomStorage.IsPending`) → `Error` `StorageMigrationPending`.
     - Free space: the sum of the ZIP's uncompressed entry lengths + the current ZIP size + `SpaceMarginBytes` on the library volume (a Store repack can approach the uncompressed size), else `Error` `InsufficientSpace`.
     - Rewrite the ZIP in place through `WriteCheckedAsync` with unchanged contents (same `info.json`, every entry and save carried, no `NewEntries`) at the system's current level. The writer's existing verification (SHA-1 and size of every `roms/` entry) runs before publish. On failure → `Error` `VerifyFailed` or `IoError`, ZIP untouched.
     - After publish, mirror the game (`MirrorEntry`, so `zip_size_bytes` / `zip_mtime_utc` match). A mirror failure → `Error` `DbWriteFailed`. The ZIP is valid, and a repack-again fixes the row.
     - Success → item `Repacked`.
  4. Summary `{ repacked, error }`. Cancel and shutdown stop between games. A game is never half-written (atomic tmp + move). No per-game audit; the job items are the record.
- **Z10** `jobLabel('ZipRepack')` = "Repacking ZIPs". The footer indicator and the Jobs page pick it up. The item outcome `Repacked` joins `outcomes` and `outcomeClasses` in `lib/jobs.ts`, with class `text-bg-success` (as `Ingested` and `Repaired`).

### ROM Zip Config tab (SPA)
- **Z11** New column **ZIP Compression** after Configured Behaviour.
  - Configurable rows show the level name and an **Edit** button (disabled while any job runs). The button opens `ConfirmModal` with a `<select>` (Store / Medium / High, the current value preselected; confirm is disabled while the current value is selected).
  - The modal text: every game ZIP of the system is rewritten one at a time; this takes a long time on a large library and is heavy on disk and CPU; no other job runs and no game can be edited until it ends; a cancelled run leaves mixed levels (still playable) and can be finished with Repack again; free space of about one game's ZIP is needed.
  - Confirm → `PUT`. `202` → `jobs.refresh()` and a success toast linking to the Jobs page. `204` → reload. `409` → `toasts.jobRunning`. Other errors → `ErrorAlert`.
  - Non-configurable rows show "—" with the hint "ROMs beside ZIP".
  - Each configurable row also has a small **Repack again** button (same disabled rule, `ConfirmModal` with the same text) → `POST …/repack`.
- **Z12** Below the table, a **ZIP compression levels** legend (a small table: Level, Typical size, Speed, Use for):
  - **Store**: ~100% of the ROM size (no compression); fastest to read and write, almost no CPU; for large ROMs that don't compress well (DS, ROMs where fewer files on disk is the goal).
  - **Medium** (default): about 40–90% of the ROM size, depending on the ROM; fast; today's behaviour.
  - **High**: a few percent smaller than Medium; writes several times slower, reads like Medium; for small cartridge ROMs where space matters most.
  
  A sentence under the table says the numbers are typical estimates, not guarantees.

### Missing Metadata report
- **M1** A game is **missing metadata** when `release_year IS NULL`, or `JSON_LENGTH(developers) = 0`, or `JSON_LENGTH(publishers) = 0`. The report covers the same game population as the Missing Images report (`ReportRepository.ListMissingImagesAsync`).
- **M2** `GET /api/reports/missing-metadata` (**Admin only**) takes:
  - `system` (validated like Missing Images);
  - `missing` = `any` (default) | `year` | `developer` | `publisher`;
  - `q` (title contains, trimmed, max 100 chars);
  - `sort` = `title` (default) | `system` | `year`;
  - `dir` = `asc` (default) | `desc`;
  - `page`, `pageSize` (as Multiple Releases).
  
  Invalid values → `400` `Problems.Validation` keyed by the parameter. It returns a `Page` of `{ id, displayTitle, system, systemName, releaseYear, developers, publishers }` (arrays as stored). Sorting by `year` puts nulls last in both directions. Ties are broken by display title, then id.
- **M3** `GET /api/reports/missing-metadata/next?afterGameId=` (Admin only) → `{ id, currentIsOnly }`. It returns the next missing-metadata game after `afterGameId` in the unfiltered default order (title asc, id), wrapping to the first, with the same contract as `missing-images/next` (`id` null when none; invalid id → `400` keyed `afterGameId`).
- **M4** A new tab **Missing Metadata** (icon `report-missing-metadata` → `bi bi-card-checklist`, added to `icons.ts`) in `routes/reports/+layout.svelte`, Admin only (hidden for Users, like Activity). The page is `/reports/missing-metadata`, wrapped in `AdminOnly`. It has:
  - a system filter, a missing-field filter, and a title search (debounced like the review search);
  - all filters, sort and page held in URL state, as Multiple Releases does;
  - columns **Game** (link to `/games/<id>?from=missing-metadata`), **System** (badge), **Year**, **Developer**, **Publisher**. Missing values show a `Missing` warning badge. Lists are joined with ", ";
  - sortable Game, System and Year headers (`sortBy`, as Multiple Releases);
  - pagination;
  - an empty state "Every game has its year, developer and publisher."
- **M5** New hotkey action `reports.missingMetadata.next` ("Next missing metadata", role `admin`, editable, no default key: `Defaults` is `[]`). It's added on both sides, which must stay in step: the server list `Core/Users/Hotkeys.cs` (so `GET /api/auth/me/hotkeys` lists it and saving a binding for it is accepted) and `lib/hotkeys.ts`. Both sides treat an empty `Defaults` as an unbound action: the SPA's `defaultBindings` and the server's default-binding code return `null`, not `undefined` or an index error. It works like `reports.missingImage.next`: on a game page it calls next with that game's id, otherwise with none; it navigates to `/games/<id>?from=missing-metadata`; when there are none, or the current game is the only one, it shows the matching toast.
- **M6** Counts in the report use `formatCount`.
- **M7** On the game page, when the URL has `from=missing-metadata` and the caller is Admin, a **Next missing metadata** button appears next to the page actions. It runs the M5 action.

### Emulator
- **E1** **Touch controls.** EmulatorJS 4.2.3 (vendored) ships a virtual gamepad. It's shown only when EmulatorJS has seen a touch, and its setting defaults to enabled only when it detects mobile. In `static/emulator/play.js`, when the device has touch (`matchMedia('(pointer: coarse)').matches || navigator.maxTouchPoints > 0`):
  - set `EJS_defaultOptions['virtual-gamepad'] = 'enabled'`;
  - on `EJS_onGameStart`, call `EJS_emulator.toggleVirtualGamepad(true)`.
  
  Implement first reproduces the missing gamepad with Chrome mobile emulation (touch on) and confirms why it's hidden. If the cause is something else (for example the iframe's size or the modal's CSS hiding it), it fixes that cause and records it in § Decisions. The gamepad layout is EmulatorJS's default per core. DS keeps `desmume_pointer_type: 'touch'` for the bottom screen. If `play-psp.html` doesn't use EmulatorJS, PSP is out of scope and that is noted in the docs.
- **E2** **Screenshot.** For Admins, the emulator modal toolbar gets a **Screenshot** button (camera icon), enabled once the game is running. Click → `postMessage({ type: 'rnrm-emulator-export', kind: 'screenshot' })`, following the existing export protocol. `play.js` calls `EJS_emulator.screenshot((blob, format) => …)` and replies with the bytes and format (an error reply on failure).
  - The modal then shows a **preview** (the image, with **Save as Snap** and **Discard**) inside the emulator modal, over the game area, not as a second Bootstrap modal.
  - `play.js` always asks for PNG: `EJS_emulator.screenshot(cb, undefined, 'png')`, so a user's `screenshotFormat` setting (which may be `webp`) is ignored.
  - Save → `POST /api/games/{id}/images` (`type=Snap`, the bytes as `screenshot.png`, through the existing upload helper). Success → toast "Screenshot saved to the game's images" and the preview closes. `409 JobRunning` → `toasts.jobRunning`. Other errors → error toast, preview kept so the user can retry.

### SPA polish
- **U1** **Navbar (mobile).** A click on any link inside `#main-nav` that isn't a dropdown toggle, including dropdown items, hides the collapse through Bootstrap's `Collapse` API, but only when it's shown (`.show`). `afterNavigate` also hides it, to cover navigation started elsewhere (hotkeys, Run buttons). Desktop is unaffected (the collapse is never `.show` there).
- **U2** **Delete release modal.** `ConfirmModal` gets a `scrollable` prop that adds `modal-dialog-scrollable`, so the header and footer stay visible and only `.modal-body` scrolls. `ReviewDeleteModal` passes it. Other modals are unchanged.
- **U3** **Review detail page** (`/review/[id]`). Under the title and actions row, a line shows the pending count from `navCounts`: "1,234 items to go" (`formatCount`; "1 item to go"; "No items left" at 0). It's hidden while the count is unknown. After Use, Use and Next, Dismiss or Delete, the page refreshes `navCounts` so the number drops. Implement checks whether it already does.
- **U4** **Header table numbers.** `formatValue` (`lib/format.ts`) gets an opt-in second parameter: `formatValue(value, { counts: true })` formats a `number` with `formatCount`. Strings, hex values and others are unchanged. Only the Review detail header table and `ReviewDeleteModal` pass it. Other callers (`routes/admin/settings`, the game-page header modal) keep plain numbers, so ports and years aren't shown with commas.
- **U5** **Review tab icons.** Each tab of the Review detail page tab list (`tabLabels`) gets its own icon from `icons.ts`, added there if missing, placed before the label (`Icon`, decorative).
- **U6** **Library grid.** The `GameResults` grids (both variants) replace `row-cols-1` with `row-cols-2` plus a custom class `game-grid-xs`. Its CSS goes in `app.css`: `@media (min-width: 360px) and (max-width: 575.98px) { .game-grid-xs > * { flex: 0 0 auto; width: 33.3333%; } }`. The result is 2 cards per row below 360px, 3 from 360px up to Bootstrap `sm`, and today's columns from `sm` up. The card text must not overflow at 320px and 375px; titles truncate as today, and small badges may wrap.
- **U7** **Keyboard Shortcuts modal.** The title becomes "Keyboard Shortcuts", as do the hidden caption, the footer button title and the docs. Each action row gets a **Run** button (`aria-label="Run <name>"`), shown on every device. It closes the modal and runs the action through the same code path as its key: the `hotkeys` store `run` for editable actions. For the non-editable `search`, it runs whatever opens global search today on its key; Implement exposes that function if it's private. Run is disabled while a key is being captured or there are unsaved binding changes, with the hint "Save or cancel your changes to run a shortcut", so no edit is silently lost. Actions the user's role can't use are already not listed and stay hidden.
- **U8** **Tab lists scroll.** In `app.css`, `.nav-tabs` gets `flex-wrap: nowrap; overflow-x: auto; overflow-y: hidden`, with thin scrollbars, and its `.nav-link` gets `white-space: nowrap`. This applies to every tab list in the app. A Svelte action `scrollActiveTab` (`lib/actions/scrollActiveTab.ts`) is applied to every `ul.nav-tabs` (Implement greps them all). On mount and whenever the active tab changes, it sets the list's `scrollLeft` so the `.active` tab is fully visible. It never calls `scrollIntoView`, so the page itself doesn't scroll.
- **U9** **Footer.** Below `sm` (576px) the "RnRomManager" name is hidden (`d-none d-sm-inline`). The version stays, and stays a link.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `Core/Library/ZipCompression.cs` (new), `Core/Library/LibraryZipWriter.cs` | Z1–Z2 |
| Core | `Core/Storage/RomStorageSettings.cs` | Z3 compression keys (load, get, set) |
| Core | every `new LibraryZipWriter(` caller: `IngestPipeline.cs`, `GameMediaRetry.cs`, `LibraryEditor.cs`, `LibraryEditor.External.cs`, `StorageMigrationJob.cs`, `StorageMigrationJob.Embed.cs` | Z4 |
| Core | `Core/Jobs/Job.cs` (`JobType.ZipRepack`), `Core/Storage/ZipRepackJob.cs` (new), `Core/Audit/AuditActions.cs` (`ZipCompressionChanged`), `Core/Library/ILibraryRepositories.cs` + `Data/Repositories/LibraryRepositories.cs` (game list by system, if no equivalent exists) | Z9 |
| Api | `AppHost.cs` (factory, DI), `Endpoints/AdminEndpoints.cs` | Z6–Z8 |
| Core/Data/Api | `Core/Reports/IReportRepository.cs`, `Data/Repositories/ReportRepository.cs`, `Endpoints/ReportEndpoints.cs` | M1–M3 |
| Web | `lib/jobs.ts`, `routes/admin/rom-storage/+page.svelte`, `lib/api/types.ts` | Z10–Z12 |
| Core/Web | `Core/Users/Hotkeys.cs`, `routes/reports/+layout.svelte`, `routes/reports/missing-metadata/+page.svelte` (new), `lib/hotkeys.ts`, `lib/components/icons.ts`, `lib/stores/hotkeys.svelte.ts`, `routes/games/[id]/+page.svelte` | M4–M7 |
| Web | `static/emulator/play.js`, `lib/components/EmulatorModal.svelte`, `lib/components/icons.ts` | E1–E2 |
| Web | `lib/components/Navbar.svelte`, `ConfirmModal.svelte`, `ReviewDeleteModal.svelte`, `routes/review/[id]/+page.svelte`, `lib/format.ts`, `GameResults.svelte`, `HotkeysModal.svelte`, `AppFooter.svelte`, `app.css`, `lib/actions/scrollActiveTab.ts` (new), every `ul.nav-tabs` (`RomToolsTabs.svelte`, `routes/reports/+layout.svelte`, `routes/admin/+layout.svelte`, review detail, others found) | U1–U9 |
| Docs | `docs/features.md` (ZIP compression column + legend + repack, Missing Metadata report + shortcut (including the `/api/auth/me/hotkeys` action list), emulator touch controls + screenshot, Run buttons, Review counter), `docs/zip-format.md` (entries may be stored or deflated at any level), `docs/development.md` (`RomStorageSettings` compression keys, `ZipRepackJob`, `scrollActiveTab`), `docs/deployment.md` (repack run time and free space) | doc rule |

No migration. New config keys: `storage.zipCompression.<systemId>` in `app_settings`. New API: `PUT /api/admin/zip-compression/{systemId}`, `POST /api/admin/zip-compression/{systemId}/repack`, `GET /api/reports/missing-metadata`, `GET /api/reports/missing-metadata/next`. `GET /api/admin/rom-storage` gains two fields (additive). Existing data: nothing changes until an Admin picks a level. Default `Med` is byte-for-byte today's level rule.

## Risks
- **Data loss during repack:** each ZIP is written to a tmp file, verified (SHA-1 and size of every ROM entry) and atomically moved, as for every library write. A failure leaves the original untouched (Z9).
- **Time and disk:** a repack rewrites every ZIP of a system and holds the job lock for its whole run (no ingest or edits). The modal warns about this, there's a per-game free-space check, and cancel plus Repack again are available.
- **High on large ROMs:** `SmallestSize` on 512 MiB DS ROMs is slow on every later write (saves, image uploads). The legend recommends High only for small ROMs. It isn't blocked.
- **Store makes ZIPs larger:** the library grows by roughly the compression savings. The free-space check is per game, not for the whole run. The modal says so.
- **Touch controls:** the root cause is unconfirmed (E1). Implement verifies with mobile emulation; the user confirms on a real phone in Finish.
- **Bootstrap `.nav-tabs` change is global:** every tab list scrolls instead of wrapping. The e2e and docs screenshots may shift.

## Steps
- [ ] **1. Compression levels in the writer and settings**: Z1–Z4. Files: `ZipCompression.cs`, `LibraryZipWriter.cs`, `RomStorageSettings.cs`, the Z4 writer callers, `AppHost.cs` (DI only). Tests: `tests/RnRomManager.Core.Tests/Library/LibraryZipCompressionTests.cs` (existing; add: `CompressionFor` per level, including `info.json`; a Store ZIP's entries have `CompressedLength == Length`; Med unchanged from today), `RomStorageSettingsTests` (compression keys load for all systems, invalid → Med with no throw, `SetCompressionAsync` writes the DB then memory, DB failure leaves memory), `ZipCompressionWriteTests` (new, Api.Tests Integration: with a system set to Store, a game edit, e.g. a notes or metadata update through the API, leaves a ZIP whose entries are stored). Done when: those classes pass and `new LibraryZipWriter(` has no caller left without a level, except the tests and the default.
- [ ] **2. Repack job**: Z9 (with the setting change), `ZipCompressionChanged`. Files: `Job.cs`, `ZipRepackJob.cs`, `AuditActions.cs`, `ILibraryRepositories.cs` / `LibraryRepositories.cs` (if needed), `AppHost.cs` (factory). Tests: `ZipRepackJobTests` (new, Core.Tests: setting saved and audited before repack; same value saves nothing; save failure fails the job with no repack; every game rewritten at the new level with byte-identical entries; DB size/mtime mirrored; pending game → `StorageMigrationPending`; missing or unreadable ZIP → `IoError`; a Store repack whose uncompressed size exceeds free space → `InsufficientSpace`; a same-value race still repacks every game; a verify failure leaves the ZIP; cancel between games; summary counts), `GameRepository*Tests` for the new query if added (Integration). Done when: those classes pass.
- [ ] **3. Compression endpoints**: Z6–Z8. Files: `AdminEndpoints.cs`. Tests: `RomStorageEndpointsTests` (GET has `zipCompression` and `compressionConfigurable`), `ZipCompressionEndpointsTests` (new, Integration: User → 403; PUT 404, 400 for missing body or bad `level`, `NotConfigurable` for a beside-ZIP system, 204 for the same value, 202 starts the job and persists the key plus audit, 409 when busy saves nothing; repack 202, 404, `NotConfigurable`, 409), `EndpointRoleMatrixTests` (new routes Admin only). Done when: those classes pass.
- [ ] **4. Missing Metadata API**: M1–M3. Files: `IReportRepository.cs`, `ReportRepository.cs`, `ReportEndpoints.cs`. Tests: `MissingMetadataQueryTests` (new, Data.Tests Integration: each missing rule; complete games excluded; `missing` filter; system and `q` filters; sort by title/system/year both directions with nulls last; paging; next wraps, `currentIsOnly`, none), `ReportEndpointsTests` (Admin only; 400 per bad parameter; shape), `EndpointRoleMatrixTests`. Done when: those classes pass.
- [ ] **5. ROM Zip Config: compression column, legend, repack**: Z10–Z12. Files: `routes/admin/rom-storage/+page.svelte`, `lib/jobs.ts`, `lib/api/types.ts`. Tests: `rom-storage-page.test.ts` (column values; Edit and Repack only on configurable rows; "—" otherwise; modal select preselected, confirm disabled while unchanged; PUT and POST bodies; 202 toast plus refresh; 409 toast; disabled while a job runs; legend rendered), `jobs.test.ts` (`ZipRepack` label; `Repacked` outcome and class). Done when: those tests, lint and check pass.
- [ ] **6. Missing Metadata page, shortcut and game-page Next**: M4–M7. Files: `Core/Users/Hotkeys.cs`, `routes/reports/+layout.svelte`, `routes/reports/missing-metadata/+page.svelte`, `lib/hotkeys.ts`, `lib/stores/hotkeys.svelte.ts`, `lib/components/icons.ts`, `routes/games/[id]/+page.svelte`, `lib/api/types.ts`. Tests: `HotkeysTests` (Core.Tests: new action listed, empty defaults give no binding, saving a binding for it is accepted), `reports-pages.test.ts` (tab visible to Admins only; columns; Missing badges; filters, sort and page in the URL; empty state; links carry `from`), `hotkeys.test.ts` (new action; `defaultBindings` gives `null` for it), `hotkeys.svelte.test.ts` (next navigation and toasts), `game-page.test.ts` (Next button only with `from=missing-metadata` for Admins). Done when: those tests, lint and check pass.
- [ ] **7. Shell: navbar, footer, tabs, shortcuts modal**: U1, U7, U8, U9. Files: `Navbar.svelte`, `AppFooter.svelte`, `app.css`, `lib/actions/scrollActiveTab.ts`, every `ul.nav-tabs` file, `HotkeysModal.svelte`, `lib/stores/hotkeys.svelte.ts` (export `run` if not already). Tests: `Navbar.test.ts` (a link click hides an open collapse; a dropdown toggle doesn't), `AppFooter.test.ts` (name has the `d-none d-sm-inline` classes, version still shown), `scrollActiveTab.test.ts` (new: sets `scrollLeft` for an off-screen active tab; never calls `scrollIntoView`), `HotkeysModal.test.ts` (title "Keyboard Shortcuts"; Run closes the modal and runs the action, including `search`; Run is disabled while capturing or with unsaved changes; the role filter is unchanged). Done when: those tests, lint and check pass.
- [ ] **8. Review, delete modal, library grid**: U2–U6. Files: `ConfirmModal.svelte`, `ReviewDeleteModal.svelte`, `routes/review/[id]/+page.svelte`, `lib/format.ts`, `icons.ts`, `GameResults.svelte`. Tests: `ConfirmModal.test.ts` (`scrollable` adds the class), `review-page.test.ts` / the review detail test (counter text for 0, 1 and many; it refreshes after Use and Next; tab icons), `format.test.ts` (`formatValue` with and without `counts`, numbers vs strings), `library-page.test.ts` (grid classes). Files also: `app.css` (`game-grid-xs`). Done when: those tests, lint and check pass, and a manual check in the browser pane at 320px, 375px and 576px shows 2, 3 and today's columns with no horizontal overflow.
- [ ] **9. Emulator touch controls and screenshot**: E1–E2. Files: `static/emulator/play.js`, `EmulatorModal.svelte`, `icons.ts`. Tests: `EmulatorModal.test.ts` (Screenshot only for Admins; export message; preview Save uploads as Snap; Discard; 409 and error toasts), `emulatorParams.test.ts` or a new `play.test.ts` (touch detection sets `virtual-gamepad`), plus a manual check with Chrome mobile emulation recorded in § Decisions. Done when: those tests, lint and check pass, and the virtual gamepad shows in mobile emulation.
- [ ] **10. Docs and e2e**: Changes' Docs row. E2E in `tests/e2e/specs/mvp.spec.ts`: Admin opens ROM Zip Config, sees the Compression column and legend, sets GBA (in-ZIP) to Store through the modal, the job finishes on `/jobs` with `Repacked` items (poll `GET /api/jobs/current` until 204), the GBA ROM downloads byte-exact, then sets it back to Med; Admin opens Reports → Missing Metadata and sees a seeded game with a Missing badge; at a mobile viewport the navbar closes after a link click. Done when: docs updated and the e2e passes in Finish.

## Decisions
- 2026-11-19 One spec or split → one spec, ZIP compression first (user)
- 2026-11-19 Which systems get a compression level → only systems whose ROMs are currently in the ZIP (user)
- 2026-11-19 Level mapping → Store = NoCompression; Med = today's rule (Optimal, Fastest over 128 MiB); High = SmallestSize; no key = Med (user)
- 2026-11-19 Resume after a cancel or failure → Repack again button, no per-game tracking, no migration (user)
- 2026-11-19 Screenshot flow → preview, then Save as Snap; Admin only because the upload is Admin only (user, code)
- 2026-11-19 Missing Metadata report access → Admin only (user)
- 2026-11-19 Extras → Next missing metadata shortcut plus game-page Next button; scroll the active tab into view (user)
- 2026-11-19 Compression setting lives in `RomStorageSettings` (reuses its startup, restore and test-reset loads) rather than a new class (code)
- 2026-11-19 Setting saved by the job under the job lock, as for ROM storage, so a busy lock saves nothing (code)
- 2026-11-19 Library grid → 2 per row below 360px, 3 from 360px to `sm` (default)
- 2026-11-19 Review counter and tab icons go on the Review detail page, where Use and Next lives (code)
- 2026-11-19 Review: own hotkey list kept in step on server (`Hotkeys.cs`); free space counts uncompressed size; `formatValue` commas opt-in for Review only; screenshots always PNG; Run disabled with unsaved binding edits (reviewer, code)

## Kickoff prompt
```text
/spec implement 2026-11-19-zip-compression-ui-polish.md
```
Starts the spec, or resumes it at the first unticked step.
