---
title: Reports, auditing and library QoL
status: ready # draft | ready | in-progress | completed
created: 2026-11-06
---

# Reports, auditing and library QoL

## Summary
A batch for people curating a large library, especially during the first import. It fixes the "page stops scrolling" bug and adds small quality-of-life changes to the Review, Game, Library, Ingestion pages and the navbar. It also adds a **Reporting** page (Library Size treemap, Missing Images, Duplicates with merge/dismiss) for every signed-in user, and an **Admin → Auditing** tab that records every file action the app takes, kept for 30 days.

## Out of scope
- A Library Size tab under Admin. It lives only on Reporting (user decision).
- Duplicate detection by hash, by DAT game name or across systems. `roms.sha1` is unique, so the same bytes can never sit in two games; only the base-title rule (D1) is used.
- Auditing reads, sign-ins, user/role changes, settings changes, integrity checks (they never touch files) and the log purge.
- Undo from the audit log.
- Auditing cleanup and rollback file changes: `CrashRecovery`'s startup deletes (`*.zip.tmp`, `cache/work`), `LibraryEditor.UndoTargetWriteAsync` and the `cache/work` deletes in `IngestJob`/`ReviewResolver`. They undo or clear temporary state, not library or ingest content.
- Fixing the Library page's existing `goto(…, { reset: false })` (`reset` isn't a SvelteKit option, so it pushes history). The new pages use the options given in RV4.
- Persisting the ingestion file selection across page loads.
- A JSON *editor*. The info.json view is read-only.
- Image search providers other than Google, and any automatic image fetching from the search.
- New third-party npm or NuGet dependencies. The treemap and JSON viewer are written here.

## Behaviour

### Global
- **G1 Scroll bug.** Cause: a Bootstrap `Modal` sets `body.modal-open`, inline `overflow: hidden` and `padding-right` on `<body>` and appends a `.modal-backdrop`. It only removes them at the end of `hide()`'s fade (`hidden.bs.modal`). `ConfirmModal` and `GlobalSearch` call `modal.dispose()` in `onDestroy`, and `dispose()` doesn't restore `<body>`. A modal unmounted while shown or still fading out therefore leaves the page unscrollable until refresh. That happens after merge, move, delete release and delete game on the game page (`GameAdminModal` sets `action = null` and calls `ondone`, which runs `goto` or `reload++` before the hide finishes; the page's load `$effect` sets `game = null`, unmounting every modal), after Dismiss/Delete on the review item page (`goto('/review')` right after closing), after a review action that 404s or hits `SourceMissing` (the item block that holds the modals unmounts), when the Users page's reload after create/reset fails (the `{#if error}` branch unmounts both modals), and when the root layout swaps the page out on a 401/403 redirect. All of these go through `ConfirmModal`, so the central fix covers them.
  Fix: a shared `releaseModal(element)` helper in `src/lib/modal.ts`, called by both components' `onDestroy` before `dispose()`. When `element.classList.contains('show') || document.querySelector('.modal-backdrop') || document.body.classList.contains('modal-open')`, and **no other** `.modal.show` element (other than `element`) remains in the document, it removes `modal-open` from `<body>`, clears `<body>`'s inline `overflow` and `padding-right`, and removes every `.modal-backdrop`. Another modal that is still open keeps its body state. Callers keep their current close-then-navigate flow. `releaseModal` must run before `dispose()`, because `dispose()` drops the backdrop reference. Only these two components create a `Modal` today; both import it as `'../bootstrap'` and import the helper the same way (`'../modal'`). Every SPA test that renders a modal mocks Bootstrap with a no-op `Modal` (route tests mock `'#lib/bootstrap.js'`, `ConfirmModal.test.ts`/`GlobalSearch.test.ts` mock `'../bootstrap'`), so `<body>` state is never set by Bootstrap in tests. `modal.test.ts` therefore tests `releaseModal` directly against a `<body>` it seeds by hand. The component tests mock `'../modal'` and only assert that `onDestroy` calls it before `dispose`.
- **G2 Ctrl K hint.** The `<kbd>Ctrl K</kbd>` in the navbar's Search button goes. The Ctrl+K shortcut itself still works.

### Library page
- **LP1 Size.** `GET /api/games` items get `primarySizeBytes` (`roms.size_bytes` of the game's `is_primary` release; with one release that is it). The grid card shows `<FileSize>` after `year · regions`; the list view gets a right-aligned **Size** column after Releases. A game with no primary release (shouldn't happen) shows nothing.

### Review list and review item
- **RV1 Shift+Delete.** On the review list's row delete button and on the item page's Delete button, a click with Shift held deletes the item at once (`DELETE /api/review/{id}`) with no modal, **unless the item came from an archive**. Deleting an archive item deletes the whole archive, so Shift+click on one still opens the modal and its warning. `GET /api/review` list items get `isArchive` (`archive_path` is not null; additive) so the list knows. Without Shift, the modal opens as today. The buttons get a tooltip (the `use:tooltip` action, the house rule) reading `Delete (Shift+click to skip confirmation)`. On success the list reloads the current page (or the previous one when the page became empty), and the item page navigates back to `/review` (keeping RV4's filter). A failure toasts the error (`JobRunning` uses `toasts.jobRunning`), and nothing else changes. The DELETE call moves from `ReviewDeleteModal` into `deleteReviewItem(id)` in `src/lib/review.ts`, used by the modal and both shortcuts. The success toast names the file. Today both pages' `deleted()` handler toasts the `originalFileName` of the `ReviewDetail` the modal passes back; that stays for the modal path and for the item page's shortcut (it has the detail). The list's shortcut has no `ReviewDetail`, so it toasts the list item's `fileName`.
- **RV2 Google It.** In the item page's header, before Dismiss/Delete, so every item gets it whatever its reason (AmbiguousGroup items have no Suggestions tab). It's a `Google it` link-button (`btn btn-outline-secondary btn-sm`, search icon, `target="_blank" rel="noopener noreferrer"`) opens `https://www.google.com/search?q=<encoded query>`. The query comes from `reviewSearchQuery(item, systemName)` in `src/lib/googleSearch.ts`:
  1. Name = the header `title` when it's non-empty and has at least 3 alphanumeric characters, else the original file name.
  2. Clean it: drop the extension, drop every `(…)` and `[…]` group, replace `_` and `.` with spaces, collapse spaces, trim.
  3. Query = `"<name>" <system display name> rom`. When the cleaned name is empty, query = `<crc32> <system display name> rom`.
  Example: `Orbt_Blcks (U) [!].gb`, Game Boy → `"Orbt Blcks" Game Boy rom`.
  The system display name comes from a new `systemName` field on `GET /api/review/{id}` (additive, from `ISystemRegistry`). Today neither detail DTO has one.
- **RV3 Filter API.** `GET /api/review` accepts optional `system` (a system id), `reason` (`NoDatMatch` or `AmbiguousGroup`) and `q` (1–100 chars, case-insensitive substring of `original_file_name` or of the header `title`). `header` is a JSON column (binary collation on MariaDB), so the title is matched as `JSON_VALUE(header, '$.title') COLLATE utf8mb4_unicode_ci LIKE …`, with the pattern escaped by `SqlLike.Contains`. A blank or whitespace `q` is treated as absent, as `/api/games` does. Unknown `system` (validated through `ISystemRegistry.ById`, message `Choose a registered system.` like `ParseQuery`) or `reason` values, and a trimmed `q` longer than 100 chars, give `400` through `Problems.Validation` (`code: ValidationFailed`, with a field error on the parameter). The `total` and paging reflect the filter. Status and paging are unchanged. The filter is a new optional last parameter of `IIngestReviewRepository.ListAsync` (default `null`), so existing callers such as `SeedIngestTests` keep compiling.
- **RV4 Filter UI.** The review list gets a filter bar above the table: a search input (`Search file name or title`, debounced 300 ms), a System select (`All systems` + `/api/systems`), a Reason select (`All reasons`, `No DAT match`, `Ambiguous group`) and a `Clear` button (enabled when any filter is set). The filter is kept in the URL query (`q`, `system`, `reason`, `page`) and in `localStorage` key `rnrm.review.filter` (the house `rnrm.` prefix, wrapped in try/catch like `pageSize.ts`). The page is new URL state: today it lives only in component state. The page derives its state from `page.url.searchParams` and writes it with `goto(url, { replaceState: true, keepFocus: true, noScroll: true })`. (The Library page's `goto(…, { reset: false })` is not the model: `reset` isn't a `goto` option.) It does not use shallow `replaceState`. Rules:
  - Loading `/review` with any of these params uses them and saves them.
  - Loading `/review` with none restores the saved filter (and page) through that `goto` with `replaceState`.
  - Changing a filter resets to page 1, updates the URL (same `goto`) and saves.
  - Changing page updates `page` in the URL and in storage.
  - `Clear` removes all four from the URL and `localStorage`.
  - The empty state reads `No review items match this filter.` when a filter is set, the current text otherwise.

### Game page
- **GP1 Tabs.** Below the header block, the Releases and Images sections become Bootstrap `nav-tabs` (`role="tablist"`, buttons with `role="tab"`, `aria-selected`, `aria-controls`) labelled `Releases (n)` and `Images (n)`. Releases is the default. `?tab=images` opens Images. Switching tabs updates `tab` with the same `goto(…, { replaceState: true, keepFocus: true, noScroll: true })` as RV4 (choosing Releases removes it). Switching must not reload the game: the load `$effect` keys on the id only. Both panels stay mounted and the inactive one is hidden with the `hidden` attribute, so `GameImages`' modals keep their DOM order (I3 from the previous spec) and nothing unmounts on a tab switch. Placement:
  - `GameAdminModal` opens from header buttons on either tab. It stays outside both panels, otherwise it would render inside a hidden ancestor.
  - It also stays before the Images panel in DOM order, because `game-admin.test.ts` takes the first `.modal`.
  - `GameImages` keeps its own `<h2>Images</h2>` region, which the e2e locates by role.
  - The existing Header `ConfirmModal` and the new `InfoJsonModal` (GP4) also stay outside both panels, and after `GameAdminModal`, for the same two reasons.
- **GP2 Google button.** Next to Download ZIP, a `Google` link-button (search icon, new tab, `noopener noreferrer`) opens `https://www.google.com/search?q=` + `gameSearchQuery(game)` = `"<display title>" <system display name>`. Visible to every signed-in user. The display name comes from a new `systemName` field on `GET /api/games/{id}` (additive, like RV2's).
- **GP3 info.json endpoint.** `GET /api/games/{id}/info-json` (any signed-in user, like the downloads) returns the game ZIP's `info.json` entry as stored, `application/json`. `OpenEntry` streams through a `LimitStream` that only throws mid-read (after the `200`), and `Fail` is private, so `LibraryFiles` gains `ReadEntry(ctx, game, entry, maxBytes, out byte[]? bytes)`: it opens the entry like `OpenEntry`, buffers it up to `maxBytes` (`LibraryZip.InfoJsonEntry`, `InfoJsonReader.MaxBytes`), and maps every failure through the private `Fail`, plus `LimitExceededException` → `IoError`. The endpoint returns the buffer with `Results.Bytes`. Errors, all through `Fail` as the ROM download does:
  - Unknown game → `404 NotFound`.
  - A missing ZIP or a missing entry → `404 NotFound`.
  - An unreadable ZIP → `500 IoError`.
  - An `info.json` over `InfoJsonReader.MaxBytes` → also `500 IoError`. Because the entry is buffered before responding, the response is never cut off after a `200` (the central directory's length can lie, so it isn't trusted).
  - An entry that isn't valid JSON is still returned as stored; GP4 handles it.
- **GP4 info.json modal.** A `View info.json` button (new `code: 'bi bi-code-slash'` icon in `icons.ts`, `btn-outline-secondary btn-sm`) next to the Google button opens a `ConfirmModal` (info mode, `size="lg"`, title `info.json`). The modal fetches GP3 when opened. It shows `Loading` while loading, `ErrorAlert` with Retry on failure, and a `CopyButton` for the raw text. A `JsonTree` component renders the parsed JSON:
  - Objects and arrays are `<details>`/`<summary>` elements. The summary shows the key and `{n}` or `[n]` when closed. Depth 0 and 1 start open; deeper ones start closed.
  - Expand all / Collapse all buttons.
  - Colours are Bootstrap text classes only: keys `text-primary-emphasis`, strings `text-success-emphasis`, numbers `text-warning-emphasis`, `true`/`false`/`null` `text-danger-emphasis`. All are monospace and readable in both themes (axe-checked).
  - Invalid JSON shows the raw text in a `<pre>` with a warning alert.
- **GP5 Merge suggestions.** When the Merge into… modal opens, it shows up to 5 **Suggested** games (same system, current game excluded) as a list of buttons above the typeahead. Clicking one selects it as the target, exactly as picking it in the typeahead does. That includes filling the typeahead's input with the title. `GameTypeahead` exports a `select(game)` function (the parent calls it through `bind:this`) that does what its own `choose()` does, because today only `choose()` sets `query`. Typing again clears the selection, as it does today. Suggestions come from `GET /api/games?system=<system>&q=<term>&sort=relevance&pageSize=6`, filtered client-side to exclude the current game. `term` = the display title with `(…)`/`[…]` groups removed, trimmed, and cut to its first 8 words (`GameQuery.MaxWords`). An empty `term` requests nothing (no `q` would list arbitrary games). If the first request returns no other game, one more try uses the first word of `term` with at least 3 characters; with no such word there is no second try. No suggestions → the list is omitted. A failed suggestion fetch is silent: the typeahead still works. `q` matches every word across title, developers, publishers and genres, so a suggestion can occasionally match on metadata, especially with the one-word fallback. That's accepted: the user still picks.

### Ingestion page
- **IN1 API.** `POST /api/jobs/ingest` accepts an optional JSON body `{ "files": [{ "system": "gb", "name": "x.zip" }, …] }`. No body, or `files` null → all pending files (today's behaviour). `files` present but empty, or any entry with a null/empty `system` or `name` → `400 ValidationFailed`. The handler's body parameter is nullable, so an empty POST still works. Unknown systems and repeated entries are ignored like an unknown file. A file in the list that isn't in the job's snapshot any more is ignored. Files in the snapshot but not in the list are left alone (not Skipped, not in `job_items`). The match is exact system id + top-level file name. 409 `JobRunning`/`DatsMissing` are unchanged. `IngestJob` gets an optional selection (set by the endpoint before `StartAsync`) that filters its snapshot. This is safe: `IngestJob` is transient and injected per request, and the scheduler never starts ingest. The progress total (`Progress.SetTotal`) counts the filtered snapshot. An archive is selected by its own top-level file name.
- **IN2 UI.** Each pending-file row gets a checkbox (checked by default, `aria-label="Select <name>"`). Each system table's header gets a select-all checkbox for that system (checked, unchecked, or indeterminate when mixed). A `Select all` / `Clear selection` pair sits above the tables. The selection survives client-side paging and is rebuilt (all checked) after every reload of the pending list. Buttons: `Import all` (today's `Start ingestion`, renamed; no body; `mvp.spec.ts`, `docs/features.md` and `README.md` are updated in the same step) and `Import selected (n)` (sends IN1's body). The second is disabled when nothing is selected or everything is (then `Import all` does the same).

### Reporting
- **RP1 Page.** A new top-nav link `Reports` (new `reports: 'bi bi-bar-chart'` icon; second entry of Navbar's `links`, after Library, `admin: false`) opens `/reports`, which redirects to `/reports/library-size`. `src/routes/reports/+layout.svelte` renders `nav nav-tabs` like the Admin layout: `Library Size`, `Missing Images`, `Duplicates`. All report endpoints are under `/api/reports` and need a signed-in user unless stated. The paged report endpoints (RP4, RP8) use the house `Paging.Validate` (`page` ≥ 1, `pageSize` 1–200, else `400`) and return `Paging.ToDto`'s `{ items, page, pageSize, total }`. Their optional `system` filter gives `400 ValidationFailed` (`Choose a registered system.`) when unknown. The System filter and page on Missing Images and Duplicates are component state only, not URL state.
- **RP2 Library Size API.**
  - `GET /api/reports/library-size` returns `{ totalBytes, gameCount, systems: [{ system, systemName, bytes, gameCount }] }`, sorted by `bytes` desc. Bytes are `SUM(games.zip_size_bytes)`, the on-disk ZIP sizes. Only systems with at least one game are listed.
  - `GET /api/reports/library-size/{system}` returns `{ system, systemName, bytes, games: [{ id, displayTitle, bytes, romCount }] }`, every game in the system sorted by `bytes` desc (ties by title). An unknown system gives `404 NotFound`; a registered system with no games gives `200` with `bytes: 0` and `games: []`.
- **RP3 Library Size UI.** The page has:
  - A total line: `<FileSize> in n games`.
  - A breadcrumb: `Library` › `<System>`.
  - A treemap: inline SVG, `role="img"` with an `aria-label` summary. Rectangles are laid out by a squarified algorithm (`src/lib/treemap.ts`, pure function `squarify(items, width, height)`) and sized by bytes. At the library level each system is one rectangle; at the system level each game is one. Colours come from a new exported map in `src/lib/systemColours.ts`, which `SystemBadge` also uses: per system, the Bootstrap colour name and label foreground, unchanged from today's badges: `gb` success/`#fff`, `gbc` info/`#000`, `gba` primary/`#fff`, `n64` danger/`#fff`, and a fallback secondary/`#fff` for an unknown id. A rectangle's `fill` is `var(--bs-<name>)`. Its label's `fill` is the foreground Bootstrap's `text-bg-<name>` uses for that colour (`#fff` or `#000`), stored next to the name in the map, so labels meet AA contrast like the badges. Rectangles get a 1 px `var(--bs-body-bg)` stroke so neighbours of the same system stay distinct. Each rectangle has a `<title>` tooltip `name — size` and a text label when it is at least 60×20 px. At the system level only the 300 largest games are drawn, plus one `n others` rectangle (not clickable).
  - A table under it, the accessible equivalent. At the library level: System, Games, Size, % of library. At the system level: Game (link), Releases, Size, % of system. Sorted by size desc.
  - Clicking a system rectangle or row drills into it (`?system=<id>`, so Back works). Clicking a game rectangle or row opens `/games/{id}`.
  - Empty library → `EmptyState`.
- **RP4 Missing Images API.** `GET /api/reports/missing-images?system=&page=&pageSize=` (pageSize default 50) returns `{ items: [{ id, displayTitle, system, systemName, reason }], page, pageSize, total }`, sorted by system then title. `reason` is `NoCover` (no image with `is_cover = 1`) or `Placeholder` (`has_default_boxart = 1`). A game is listed once; when both apply, `NoCover` wins. Through the app the two can't both apply: `has_default_boxart` is set only when the cover is the placeholder image. So the `NoCover`-wins rule only guards against DB drift. A placeholder that isn't the cover (the cover was set to a snap) means `has_default_boxart = 0`, and the game isn't listed.
- **RP5 Missing Images UI.** A System filter, then a table with Game (link to `/games/{id}?tab=images`), System (`SystemBadge`) and Reason (`No cover` / `Placeholder art`), with `Pagination`. Empty → `EmptyState` `Every game has a cover image.`
- **RP6 Duplicates rule (D1).** A duplicate group is two or more games in the same `system_id` that own releases with the same `roms.base_title` (the key ingestion groups releases by; compared with the column's collation). A game in several groups (because its releases have several base titles) appears in each. A group is hidden while a dismissal matches it (RP7).
- **RP7 Dismissals.** A new table `duplicate_dismissals(system_id VARCHAR(16), base_title VARCHAR(255) COLLATE utf8mb4_unicode_ci, games_key CHAR(64), dismissed_by INT NULL (no FK, like AU1), dismissed_at DATETIME(6))`, PK `(system_id, base_title, games_key)`. `games_key` is the lower-case SHA-256 hex of the group's game ids sorted ordinally and joined with `,`. Game ids are stored as lower-case `CHAR(36)` (`GuidFormat=Char36`), and dashes sit at fixed positions, so SQL's `ORDER BY` on them gives the same order as C#'s ordinal sort. The table is `ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci` (the schema test checks every table). `base_title` uses the same collation `roms.base_title` gets from its table default. A dismissal hides only that exact membership: when a game joins or leaves the group, the group shows again. The collation is case- and accent-insensitive, so a group's `baseTitle` is one of its variants (`MIN(base_title)`), and a dismissal matches whichever variant the group shows. Rows whose group no longer exists are harmless and stay.
- **RP8 Duplicates API.**
  - `GET /api/reports/duplicates?system=&page=&pageSize=` (pageSize default 25) returns `{ items: [{ system, systemName, baseTitle, gamesKey, games: [{ id, displayTitle, romCount, zipSizeBytes, createdAt, boxArtUrl }] }], page, pageSize, total }`. Groups are sorted by system then base title. Games within a group are sorted by `romCount` desc, then `createdAt` asc, then id, so `games[0]` is the suggested target.
  - Query shape: paging and `total` must exclude dismissed groups, so the key is computed in SQL. One query groups `roms` by `(system_id, base_title)` with `COUNT(DISTINCT game_id) >= 2`, selecting `MIN(base_title)` and `SHA2(GROUP_CONCAT(DISTINCT game_id ORDER BY game_id SEPARATOR ','), 256)`, anti-joins `duplicate_dismissals` on all three key columns, and pages. A second query loads the page's games, with `boxArtUrl` built the way the game list does (cover `MIN(images.id) WHERE is_cover = 1`, through `LibraryEndpoints.ImageUrl`, made `internal`). `DuplicateGroups.GamesKey` (C#) must produce the same key; a Data test checks parity against the SQL for a real group.
  - `POST /api/reports/duplicates/dismiss { system, baseTitle, gamesKey }` (rom-manager) stores the dismissal (`INSERT IGNORE`) and returns `204`; repeating it is a no-op returning `204`. Missing/empty fields, an unknown system, a `gamesKey` that isn't 64 lower-case hex characters, or a `baseTitle` over 255 chars → `400 ValidationFailed`. A key that matches no current group is stored anyway (harmless; the group may have just changed).
  - `POST /api/reports/duplicates/merge { targetGameId, sourceGameIds }` (rom-manager) calls `LibraryEditor.MergeAsync(source, target)` for each source in order. That's the same logic, lock and errors as Merge into… (`POST /api/games/{id}/merge`; `409 JobRunning` etc.), mapped through the same `LibraryEndpoints.ToProblem`, made `internal`. Validation: a target is required, there must be 1–20 sources, the target can't be among the sources, and there can be no repeats (`400`). Success → `200 { targetGameId }`. On the first failure it stops and returns that failure's problem, whatever its code (including a mid-loop `ValidationFailed` from `MergeAsync`, e.g. a source in another system), with an extra `merged` array of the source ids already merged. `Problems` gains an optional extensions argument on every factory `ToProblem` uses for this. A merge is not fully atomic. `MergeAsync` writes the target ZIP, updates the DB, then deletes the source ZIP even when the DB update failed. So a source whose merge returned `DbWriteFailed` counts as merged (its files moved) and is included in `merged` before the loop stops. A failure before or during the target ZIP write (`JobRunning`, `NotFound`, `ValidationFailed`, `TooManyImages`, `TargetZipUnreadable`, `InfoJsonNewerSchema`, `IoError`, `VerifyFailed`) leaves that source untouched and out of `merged`.
- **RP9 Duplicates UI.** A System filter and paging. One card per group, headed `<SystemBadge> <baseTitle>`, listing its games as radio rows: title link, releases, `FileSize`, added date. The first game is pre-selected and labelled `Suggested target`. For rom-managers, the buttons are `Merge into selected` (danger, opens a `ConfirmModal` naming the target and the n games merged into it) and `Dismiss`. Others see the list without radios or buttons. After a merge or dismiss the list reloads. A merge failure toasts the problem, and when `merged` is non-empty it also says how many merged. Empty → `EmptyState` `No duplicate games found.`

### Auditing
- **AU1 Table.** A new migration adds `audit_events`:
  - Columns: `id BIGINT AUTO_INCREMENT PK`, `occurred_at DATETIME(6)`, `action VARCHAR(32)`, `system_id VARCHAR(16) NULL`, `game_id CHAR(36) NULL`, `user_id INT NULL`, `username VARCHAR(64) NULL`, `job_id CHAR(36) NULL`, `path VARCHAR(1024) NULL`, `target_path VARCHAR(1024) NULL`, `details VARCHAR(1024) NULL`.
  - Indexes: `(occurred_at)`, `(action, occurred_at)`, `(system_id, occurred_at)`, `(user_id, occurred_at)`.
  - No foreign keys, so deleting a user or game keeps its history. `username` is a snapshot taken when the row is written.
  - `ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`, like every table.
  - Paths are relative to the library or ingest root they belong to, prefixed `roms/` or `ingest/`, and never absolute. An archive entry (whose working copy sits under `cache/work/`) is recorded as `ingest/<archive path> › <entry>` from its `ArchivePath` + `ArchiveEntry`. The code mostly holds absolute paths (`IngestFile.FullPath`, the review `source_path`/`archive_path` columns), while `games.zip_path` is already relative to roms. A small Core helper, `AuditPaths`, turns them into prefixed relative paths from `PathsOptions.Ingest`/`Roms`. A path outside both roots is recorded as its file name only, never absolute. `_failed`, `_duplicates` and `_removed` all sit under `ingest/<system>/`.
  - Text over its column length is truncated.
- **AU2 Writer.** `IAuditLog.Record(AuditEvent e)` in Core is **synchronous and non-blocking**: it stamps `occurred_at` from `TimeProvider` and enqueues the event on a bounded in-memory `Channel` (capacity 10,000, `BoundedChannelFullMode.Wait` with `TryWrite`, so a `false` return means full: the event is dropped with a `Warning` log; `DropWrite`/`DropOldest` give no signal). `IAuditLog` is a singleton registered in `CreateBuilder` next to the Core services. An `AuditWriter` hosted service in Api drains the channel and inserts the events through the Dapper `AuditRepository` with `CancellationToken.None`. So the synchronous pipeline helpers (`MoveToFailed`, `MoveToDuplicates`, `FailFile`, `IngestJob.ApplyFate`) can record without becoming async, and a cancelled request or job never loses a row that was recorded or throws after the file moved. A failed insert is logged at `Warning` and the event is dropped. `Record` never throws: its body is wrapped in try/catch that logs at `Warning`, so callers don't guard it. Events are recorded only after the file action succeeds. The file action is the file change itself (ZIP written, file moved or deleted), so a later DB-mirror failure (`DbWriteFailed`) still records the row: the files did change. A file action that fails or is never attempted records nothing. On shutdown the writer drains what is queued (up to 5 s). Hosted services stop in reverse registration order, so `AuditWriter` is registered in `Build` **before** `JobShutdownService` (after `StartupRunner` is inserted at index 0). That way it drains after the running job has been cancelled and has finished. All `StopAsync` calls share the host's single 30 s `ShutdownTimeout`, so a job that uses all of it leaves the drain little or no time; that is accepted as part of the best-effort log, not given its own timeout. Events still queued at a crash are lost, which is accepted (best-effort log, files are the source of truth). Tests use `AuditWriter.FlushAsync()` (completes when the queue is empty) before asserting.
- **AU3 Who.** Every recorded event carries an `AuditActor(int? UserId, string? Username, Guid? JobId)`:
  - Endpoints build it once per request from the claims (`ClaimTypes.NameIdentifier`, `ClaimTypes.Name`), via `AuditActor.From(ClaimsPrincipal)` in Api.
  - `JobRunner` builds it once per job: job id, `requestedBy` and that user's username, loaded once through `IUserRepository`. It is passed as a new `JobContext.Actor`, added as an optional last parameter (`AuditActor? Actor = null`; a null means `new(null, null, JobId)`), so the existing positional `new JobContext(id, progress)` calls in tests keep compiling. If the lookup throws or finds no user, the username is null, `UserId` is kept and a `Warning` is logged; the job still starts. Scheduled runs (`requestedBy` null) get `UserId`/`Username` null and show as `System`.
  - `LibraryEditor` methods, `ReviewResolver` methods, `GameMediaRetry.RetryAsync`, and the `IngestPipeline` methods that move or store files take an `AuditActor` parameter. In `IngestPipeline`/`IngestJob` precisely:
    - `ProcessAsync` (and the private `ProcessCoreAsync`, and `IngestJob`'s private `ProcessAsync`) gain an `AuditActor actor` after `jobId`. `jobId` stays because it also stamps `LastSeenJobId` on review rows.
    - `FailFile`, `MoveToFailed` and `IngestJob.ApplyFate` replace their `Guid jobId` with `AuditActor actor` (the id was only used for `.error.txt`).
    - `MoveToDuplicates` gains an `AuditActor actor`.
    - `StoreReleaseAsync` and the private `PackageAsync` gain `AuditActor actor, string? reviewAction` (null for ingest; the resolution action name from review, for the `review resolve (<action>)` detail). `LibraryEditor.PatchAsync` takes `AuditActor?`: `PATCH …/rating` passes `null`, meaning "don't record", and `PATCH /api/games/{id}` passes the request actor. All callers are updated: endpoints pass the request actor; `IngestJob`, `MediaRetryJob` and `DatRefreshJob` pass `context.Actor`; `ReviewResolver` passes its caller's actor (job id null) instead of making up a job id. Its methods take an `AuditActor` in place of today's `int userId`, and the user id for `SetResolutionAsync` (still `int`) comes from `actor.UserId!.Value`: review endpoints always have a signed-in user. `MoveToFailed` takes the actor instead of a `Guid jobId`. Its `.error.txt` records the job id when there is one, else `review by <username>`.
- **AU4 Actions.** One row per file action, each with exactly one writer:

  | `action` | Written by | path / target_path / details |
  |---|---|---|
  | `Ingested` | `IngestPipeline`'s private `PackageAsync`, after the ZIP write, when the outcome isn't `Repaired`. It is reached from `NewGameAsync`, `AddToGameAsync` and `DuplicateOrRepairAsync` (ingest) and from `StoreReleaseAsync` (review resolve), which pass the actor and `reviewAction` (null from ingest) down | source / game ZIP / `release <file name>`; `review resolve (<action>)` when from Review |
  | `ZipRepaired` | the same `PackageAsync` when the outcome is `Repaired` (rewrites an existing library ZIP) | source / game ZIP / — |
  | `ExtensionRenamed` | `IngestPipeline.RenameToClassifiedExtension` | old path / new path / — |
  | `MovedToFailed` | `IngestPipeline.MoveToFailed` (ingest, and review dismiss of a loose file; dismissing an archive entry moves nothing and writes no row) | source / failed path / reason code; `review dismiss` when from Review |
  | `MovedToDuplicates` | `IngestPipeline.MoveToDuplicates` | source / duplicates path / — |
  | `ArchiveDeleted` | `IngestJob` archive delete | archive / — / — |
  | `ReviewDeleted` | `ReviewResolver.DeleteSourceAsync` | source (or archive) / — / — |
  | `GameUpdated` | `PatchAsync` for metadata edits (`PATCH /api/games/{id}`); **not** written for rating/favourite (`PATCH …/rating`) | old ZIP / new ZIP / changed field names |
  | `ReleaseDeleted` | `DeleteReleaseAsync` (not the last release) | ZIP / `ingest/<system>/_removed/<copied ROM name>` (after clash renaming) / release file name |
  | `GameDeleted` | `DeleteGameAsync`, and `DeleteReleaseAsync` on the last release (it removes the game) | ZIP / `ingest/<system>/_removed/` (only the ROM entries are copied there; images and info.json are discarded) / — |
  | `ImageAdded` / `ImageDeleted` / `CoverSet` | image methods | ZIP / — / image file name |
  | `MediaFetched` | `GameMediaRetry.RetryAsync` only (called by `RetryMediaAsync` and `MediaRetryJob`), when it wrote the ZIP | ZIP / — / fetched image names |
  | `ReleaseMoved` | `MoveReleaseAsync` | source ZIP / target ZIP / release file name |
  | `GamesMerged` | `MergeAsync` | source ZIP / target ZIP / `n releases, m images` |
  | `DatsRefreshed` | `DatRefreshJob`, one row per run that reaches `Swap` without throwing (every successful run replaces `cache/dats`) | — / — / DAT names |

  `MoveAside` (the private helper behind the two moves) records nothing itself. Review resolve/dismiss therefore write one row (`Ingested`/`MovedToFailed` with the user as actor), not two. `system_id` and `game_id` are filled whenever the action has them.

  Edge cases:
  - **`Ingested` after a mirror failure.** The ZIP is written but the source is not deleted (`DbWriteFailed`). The row is still written, and the next run's `MovedToDuplicates` row records the leftover source.
  - **`GameUpdated` field names.** `PatchAsync` compares the patched fields with the stored game. When none changed it writes no row, even though the ZIP is rewritten. After a rename that hits `DbWriteFailed`, the old ZIP stays; the row still records old → new.
  - **`ReleaseDeleted` target.** `RemoveRomsAsync` returns its `copies` list so the editor knows the renamed copy path. When the entry was missing from the ZIP nothing is copied and `target_path` is null.
  - **`CoverSet` no-op.** `SetCoverAsync` on the image that is already the cover writes nothing, so no row.
  - **`MediaFetched`** is recorded inside `RetryAsync` right after the ZIP write, so it covers both `Updated` and `DbWriteFailed`; the names come from the written image entries.
  - **`ReviewDeleted`** is recorded only when the file existed before the delete (`File.Exists` first), because `File.Delete` on a missing file, and the `DirectoryNotFoundException` path, count as success today.
  - **Resolving an archive entry** deletes only the extracted copy (under `cache/work`, not audited). The archive's `ArchiveDeleted` comes from the next ingest run.
- **AU5 Retention.** Rows older than 30 days are deleted daily with the log purge (startup and 00:05 UTC), via `IAuditLog.PurgeAsync(olderThan)`. The 30 days is a constant (`AuditLog.Retention`), not a setting. The scheduler's `PurgeLogs` only catches IO errors and an unhandled exception would stop the host, so the audit purge gets its own catch-all that logs a `Warning` (e.g. DB down).
- **AU6 API.** `GET /api/admin/audit` (`AuthSetup.AdminOnly`) takes optional `action`, `system`, `user` (username, exact match under the column's `utf8mb4_unicode_ci`, so case- and accent-insensitive), `scheduled=true` (only rows with `user_id` null; `400` together with `user`), `q` (substring of path, target_path or details, 1–200 chars), `from` (inclusive) and `to` (exclusive) as ISO 8601 instants with an offset (`400` when unparseable or `from >= to`), `page` and `pageSize` (default 100, house `Paging.Validate`, max 200). An `action` not in `AuditActions` or an unregistered `system` → `400`; a blank `q` or `user` is treated as absent. It returns `Paging.ToDto`'s `{ items: [{ id, occurredAt, action, system, gameId, user, jobId, path, targetPath, details }], page, pageSize, total }`, newest first (ties by id desc). `GET /api/admin/audit/actions` returns the AU4 action names for the filter.
- **AU7 UI.** An `Auditing` tab (new `audit: 'bi bi-journal-text'` icon if the tabs use icons) between Logs and About in the Admin layout:
  - Filter row: Action select, System select, User text with a `Scheduled only` checkbox (disables the text), Search text, From/To date inputs, and `Clear`. The text inputs are debounced 300 ms; selects, checkbox and dates apply at once. All of them and `page` are kept in the URL query with the RV4 `goto` options. The date inputs are local days: From sends local midnight of that day and To sends local midnight of the day after, both as ISO instants, so the rows match the local times shown.
  - Table: Time (local, `text-nowrap`), Action, System (`SystemBadge`), User (`System` in italics when null), Game (link when `gameId` exists), Path (truncated, `title`), Target, Details.
  - `Pagination` with 100 rows per page. Default load shows the last 100 actions.
  - Empty → `EmptyState`.

### Permissions
- Reports GETs: any signed-in user. Duplicates dismiss/merge: `rom-manager` (`AuthSetup.LibraryEditor`). Audit: Admin (`AuthSetup.AdminOnly`). `GET /api/games/{id}/info-json`: any signed-in user. Ingest body: Admin (unchanged). Every new route gets a row in `EndpointRoleMatrixTests`.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Web | new `src/lib/modal.ts`; `lib/components/ConfirmModal.svelte`, `GlobalSearch.svelte` | G1 |
| Web | `lib/components/Navbar.svelte` | G2; `Reports` link (RP1) |
| Core/Data/Api | `Core/Library/GameListItem.cs` (new positional parameter before the optional `MatchedOn`), `Data/Repositories/LibraryRepositories.cs` (`ListAsync` select and the private `ListRow`), `Api/Endpoints/LibraryEndpoints.cs` | LP1 `primarySizeBytes` (additive) |
| Api/Web | `ReviewEndpoints.cs`, `LibraryEndpoints.cs` (detail DTOs), `lib/api/types.ts` (`ReviewDetail`, `GameDetail`) | `systemName` on both detail responses (RV2, GP2; additive) |
| Web | `routes/+page.svelte` | LP1 |
| Web | new `src/lib/review.ts`, `src/lib/googleSearch.ts`; `ReviewDeleteModal.svelte`, `routes/review/+page.svelte`, `routes/review/[id]/+page.svelte` | RV1, RV2, RV4 |
| Core/Data/Api | `IIngestReviewRepository.ListAsync` (+ filter record), its Dapper repo, `ReviewEndpoints.cs` | RV1 `isArchive` on list items, RV3 query params (both additive) |
| Api | `LibraryEndpoints.cs`, `LibraryFiles.cs` (`ReadEntry`) | GP3 route |
| Api | `LibraryEndpoints.cs` (`ToProblem`, `ImageUrl` → `internal`) | RP8 |
| Web | `lib/components/icons.ts` (`code`, `reports`, `audit`) | GP4, RP1, AU7 |
| Web | `routes/games/[id]/+page.svelte`, new `lib/components/JsonTree.svelte`, `InfoJsonModal.svelte`; `GameAdminModal.svelte`, `GameTypeahead.svelte` | GP1, GP2, GP4, GP5 |
| Core/Api | `Core/Ingest/IngestJob.cs`, `Api/Endpoints/IngestEndpoints.cs` | IN1 (optional body; additive) |
| Web | `routes/ingestion/+page.svelte` | IN2 |
| Data | new migration `0013_duplicate_dismissals.sql` | RP7 |
| Data | new migration `0014_audit_events.sql` | AU1 |
| Core/Data/Api | new `Core/Reports/` (`IReportRepository`, records, `DuplicateGroups.GamesKey`), `Data/Repositories/ReportRepository.cs`, new `Api/Endpoints/ReportEndpoints.cs` | RP2, RP4, RP6–RP8 |
| Web | new `routes/reports/{+layout,+page}.svelte`, `reports/library-size`, `reports/missing-images`, `reports/duplicates`; new `src/lib/treemap.ts`, `src/lib/systemColours.ts`; `SystemBadge.svelte` (reads the shared map) | RP1–RP9 |
| Core/Data | new `Core/Audit/` (`IAuditLog`, `AuditEvent`, `AuditActor`, `AuditActions`, `AuditPaths`, `AuditLog.Retention`), `Data/Repositories/AuditRepository.cs` | AU1, AU2, AU5 |
| Core | `Jobs/IJob.cs` (`JobContext.Actor`), `Jobs/JobRunner.cs`, `Ingest/IngestPipeline.cs`, `Ingest/IngestJob.cs`, `Ingest/ReviewResolver.cs`, `Library/LibraryEditor.cs`, `Library/GameMediaRetry.cs`, `MediaRetryJob`, `Dats/DatRefreshJob.cs` | AU3, AU4. These signatures gain an `AuditActor` (internal API; all callers updated) |
| Api | new `AuditWriter.cs`, new `Auth/AuditActorExtensions.cs`, `Scheduler.cs`, `AdminEndpoints.cs` (or new `AuditEndpoints.cs`), `LibraryEndpoints.cs`/`ReviewEndpoints.cs`/`ReportEndpoints.cs` (pass the actor), `Problems.cs` (extensions, RP8), `AppHost.cs` (DI) | AU2, AU3, AU5, AU6 |
| Web | `routes/admin/+layout.svelte`, new `routes/admin/audit/+page.svelte` | AU7 |
| Tests | `EndpointRoleMatrixTests.cs`; `SchemaMigrationTests.cs` (exact table/column/index/migration lists); tests named in each step; `tests/e2e/specs/mvp.spec.ts` | |
| Docs | `docs/features.md`, `docs/development.md`, `README.md` | every behaviour above |

Migrations 0013 and 0014 only add tables, so existing data is untouched. There are no new config keys. API changes are additive: new routes, new optional query params, an optional request body, and a new response field.

## Risks
- **G1 can't be reproduced reliably by hand, and the SPA tests can't run real Bootstrap.** Every SPA test that renders a modal mocks `Modal`. The guard is the `releaseModal` unit test against a hand-seeded `<body>`, plus component tests that `onDestroy` calls it. In the e2e, `axe.ts` disables transitions only until the next full page load, and `openGame()` reloads, so the release delete runs with the fade live: its body check is a real (if timing-dependent) regression check. The helper never touches `<body>` while another modal is shown, so stacked modals (none exist today) are safe.
- **Audit volume.** One row per file means a 5,000-file import writes 5,000 rows. Each insert is small, and rows go one at a time after each file action, which is fine at this scale. Retention caps the table at 30 days. The indexes cover the default newest-first page and every filter column paired with time.
- **Audit and file consistency.** A failed insert, a full queue or a crash with events still queued leaves a file action without a row. That's accepted and logged (AU2). The file is the source of truth and the audit log is best-effort; in exchange, recording never slows down or breaks a file action.
- **Shift+Delete** can't destroy an archive's other ROMs: archive items always confirm (RV1).
- **Changing `LibraryEditor` signatures** touches every caller and test. Done across steps 20–21 with the compiler as the checklist.
- **Merging a whole duplicate group** takes the job lock once per merge, not once for the group. A job can start between two merges, and the next merge then returns `409` with `merged` filled in. That is acceptable and reported. A `DbWriteFailed` merge has already moved files, so it counts as merged (RP8). The integrity check repairs the mirror.
- **Base-title false positives.** Two genuinely different games with the same base title (e.g. a remake) will be suggested. Dismiss handles that, and the dismissal sticks until the membership changes.
- **Treemap size.** Thousands of games in one system are capped at 300 rectangles plus "others". The table lists all of them.
- **Google links** send the ROM's name to Google. They are only opened by an explicit click and never automatically.

## Steps
- [ ] **1. Scroll bug**: G1. Files: new `src/lib/modal.ts`, `ConfirmModal.svelte`, `GlobalSearch.svelte`, new `src/lib/modal.test.ts`. Tests: vitest `modal.test.ts` — with `<body>` seeded by hand (`modal-open`, inline `overflow`/`padding-right`, a `.modal-backdrop`) and the element shown, `releaseModal` leaves `<body>` with none of them; with a second `.modal.show` element still in the document, `<body>` is left as is; a closed element with a clean `<body>` changes nothing. `ConfirmModal.test.ts` and `GlobalSearch.test.ts` mock `'../modal'` and assert that unmounting calls `releaseModal` before `dispose` (their existing `'../bootstrap'` mock stays). Done when: SPA lint/check/test pass.
- [ ] **2. Ctrl K hint and library sizes**: G2, LP1. Files: `Navbar.svelte`, `Navbar.test.ts`, `GameListItem.cs`, `LibraryRepositories.cs`, `LibraryEndpoints.cs`, `LibraryEndpointsTests.cs`, `routes/+page.svelte`, `routes/library-page.test.ts`. Tests: Navbar has no `Ctrl K` text (the shortcut lives in `GlobalSearch.svelte` and `GlobalSearch.test.ts` already covers it, so it's unchanged); integration — `primarySizeBytes` equals the primary release's size for a two-release game; library page shows the size in grid and list. Done when: `dotnet test tests/RnRomManager.Api.Tests` and SPA tests pass.
- [ ] **3. Review Shift+Delete and Google It**: RV1 (incl. `isArchive` on the list DTO), RV2 (incl. `systemName` on the review detail). Files: `ReviewEndpoints.cs`, `ReviewEndpointsTests.cs`, `lib/api/types.ts`, new `src/lib/review.ts`, `src/lib/googleSearch.ts` (+ `googleSearch.test.ts`), `ReviewDeleteModal.svelte`, `routes/review/+page.svelte`, `routes/review/[id]/+page.svelte`, `review-page.test.ts` (its `#lib/bootstrap.js` mock only has `Modal`, and `use:tooltip` constructs a `Tooltip`, so it mocks `#lib/tooltip.js` like `library-page.test.ts` does). Tests: integration — `isArchive` true for an archive item, false for a loose file; the detail has `systemName`; `reviewSearchQuery` — header title used, short/empty title falls back to file name, extension/brackets/underscores stripped (the RV2 example literally), CRC fallback; Shift+click on the list deletes a loose item without a modal and reloads; Shift+click on an archive item (list and item page) opens the modal; a plain click opens the modal; Shift+click on the item page deletes and navigates to `/review`; failure toasts; the list shortcut's success toast names the list item's `fileName` and the item page's names `originalFileName`; the Google link is in the header for both a NoDatMatch and an AmbiguousGroup item and has the encoded href, `target=_blank` and `rel`. Done when: `dotnet test tests/RnRomManager.Api.Tests` and SPA tests pass.
- [ ] **4. Review filter API**: RV3. Files: `IngestOutcome.cs` (`IIngestReviewRepository`), the review repository in Data, `ReviewEndpoints.cs`, `ReviewEndpointsTests.cs`. Tests: integration — filter by system, by reason, by `q` on file name and on header title (case-insensitive), combined filters, `total` reflects the filter, unknown system/reason → 400 `ValidationFailed` with a field error, `q` over 100 chars → 400, a blank `q` is ignored, a `q` containing `%` or `_` matches literally. Done when: `dotnet test tests/RnRomManager.Api.Tests` passes.
- [ ] **5. Review filter UI**: RV4. Files: `routes/review/+page.svelte`, `review-page.test.ts` (its `$app/state` mock gains a `url`, and `$app/navigation`'s `goto` is mocked). Tests: vitest — the controls send the right query; paging writes `page` to the URL; URL params win over storage and are saved; an empty URL restores storage via `replaceState`; changing a filter resets to page 1; Clear empties the URL and storage; the filtered empty-state text; a throwing `localStorage` doesn't break the page. Done when: SPA tests pass.
- [ ] **6. Game page tabs and Google button**: GP1, GP2 (incl. `systemName` on the game detail). Files: `LibraryEndpoints.cs`, `LibraryEndpointsTests.cs`, `lib/api/types.ts`, `routes/games/[id]/+page.svelte`, `src/lib/googleSearch.ts`, `game-page.test.ts`, `game-admin.test.ts` (both `$app/state` mocks gain a `url`, and `$app/navigation`'s `goto` is mocked; `game-images.test.ts` renders `GameImages` alone and is unaffected). Tests: integration — the game detail has `systemName`; vitest (in `game-page.test.ts`/`game-admin.test.ts`) — Releases is the default; `?tab=images` opens Images; clicking a tab updates the URL without refetching the game; tab ARIA attributes; both panels stay in the DOM; the admin modal opens from the Images tab and is visible; `game-admin.test.ts`'s first `.modal` is still the admin modal; `gameSearchQuery` and the link href. The e2e images flow (step 24) covers images inside the tab. Done when: Api and SPA tests pass.
- [ ] **7. info.json endpoint**: GP3. Files: `LibraryFiles.cs` (`ReadEntry`), `LibraryEndpoints.cs`, `LibraryEndpointsTests.cs`, `EndpointRoleMatrixTests.cs`. Tests: integration — 200 with a body equal to the ZIP's `info.json` bytes and `application/json`; unknown id → 404; ZIP missing on disk → 404; corrupt ZIP → 500 `IoError`; an `info.json` over `MaxBytes` (rewritten into the ZIP by the test) → 500 `IoError`; role matrix (anonymous 401, user 200). Done when: Api tests pass.
- [ ] **8. info.json modal**: GP4. Files: new `JsonTree.svelte`, `InfoJsonModal.svelte` (+ tests), `icons.ts` (`code`), `routes/games/[id]/+page.svelte`. Tests: vitest — the tree renders nested objects/arrays with counts; depth ≥ 2 starts closed; Expand/Collapse all; the colour classes on each type; invalid JSON shows raw text and a warning; the modal fetches on open, shows the error with Retry, and the copy button copies the raw text. Done when: SPA tests pass.
- [ ] **9. Merge suggestions**: GP5. Files: `GameAdminModal.svelte`, `GameTypeahead.svelte`, `game-admin.test.ts`. Tests: vitest — opening merge requests `q=<title without tags>&sort=relevance`; a 10-word title is cut to 8 words; the current game is excluded; at most 5 shown; clicking one fills the typeahead input with its title, selects the target, and the merge posts it; typing afterwards clears the selection; the first-word fallback; a title that is all tags requests nothing; no list when empty; a fetch error shows no list and the typeahead still works. Done when: SPA tests pass.
- [ ] **10. Ingest file selection API**: IN1. Files: `IngestJob.cs`, `IngestEndpoints.cs`, `IngestEndpointsTests.cs` (and a Core test for the snapshot filter if `IngestJob` has unit tests). Tests: integration — no body ingests everything; a body with one of two files ingests only it and leaves the other in place, untouched and absent from `job_items`; an unknown file, an unknown system and a repeated entry are ignored; `files: []` and an entry with an empty `name` → 400; the job's progress total is the selected count; selecting an archive by name ingests its entries. Done when: Api tests pass.
- [ ] **11. Ingest file selection UI**: IN2. Files: `routes/ingestion/+page.svelte`, `ingestion-page.test.ts`, `tests/e2e/specs/mvp.spec.ts`, `docs/features.md`, `README.md` (the `Start ingestion` button is now `Import all`). Tests: vitest — all rows checked by default; per-system header checkbox toggles and shows indeterminate; Select all / Clear selection; the selection survives paging; `Import selected (n)` posts exactly the checked files; it's disabled at none and at all; `Import all` posts no body; a reload re-checks everything. Done when: SPA tests pass.
- [ ] **12. Reporting shell and Missing Images**: RP1, RP4, RP5. Files: new `Core/Reports/IReportRepository.cs` + records, `Data/Repositories/ReportRepository.cs`, `Api/Endpoints/ReportEndpoints.cs`, `AppHost.cs`, `Navbar.svelte`, `icons.ts` (`reports`), new `routes/reports/+layout.svelte`, `+page.svelte`, `missing-images/+page.svelte`, tests: `ReportEndpointsTests.cs`, `EndpointRoleMatrixTests.cs`, `Navbar.test.ts`, new `routes/reports/reports-pages.test.ts`. Tests: integration — a game with no cover → `NoCover`, a placeholder cover → `Placeholder`, a DB-drift row matching both (seeded with raw SQL) is listed once as `NoCover`, a game with real box art is absent, system filter, unknown system → 400, paging and `pageSize` over 200 → 400; role matrix (user 200); vitest — the nav link is shown to a non-admin, `/reports` redirects, rows link to `?tab=images`, empty state. Done when: Api and SPA tests pass.
- [ ] **13. Library Size API**: RP2. Files: `IReportRepository`, `ReportRepository.cs`, `ReportEndpoints.cs`, `ReportEndpointsTests.cs`, `EndpointRoleMatrixTests.cs`. Tests: integration — totals equal the sum of `zip_size_bytes`, per-system ordering and counts, a system with no games is absent from `systems`, game listing ordered desc, unknown system → 404, a registered system with no games → 200 with empty `games`. Done when: Api tests pass.
- [ ] **14. Treemap and Library Size UI**: RP3. Files: new `src/lib/treemap.ts` (+ `treemap.test.ts`), new `src/lib/systemColours.ts`, `SystemBadge.svelte` (reads the map; badges unchanged), new `routes/reports/library-size/+page.svelte`, `reports-pages.test.ts`. Tests: `squarify` — the rectangles tile the area exactly (sum of areas = width×height within 0.5 px², none overlap, all inside the bounds), each area is proportional to its value, zero values are dropped, a single item fills the box; vitest — library-level rectangles and table rows, drilling sets `?system=`, a game click goes to `/games/{id}`, the 300 + `others` cap, the empty state. Done when: SPA tests pass.
- [ ] **15. Duplicates API**: RP6–RP8, migration 0013. Files: `0013_duplicate_dismissals.sql`, `Core/Reports/DuplicateGroups.cs` (`GamesKey`), `ReportRepository.cs`, `ReportEndpoints.cs`, `LibraryEndpoints.cs` (`ToProblem` and `ImageUrl` become `internal`), `Api/Problems.cs` (optional extensions), `ReportEndpointsTests.cs`, `EndpointRoleMatrixTests.cs`, `tests/RnRomManager.Data.Tests/Database/SchemaMigrationTests.cs` (the `Tables` array, the scripts-count test name and its `12L` → `13L`, columns, indexes), a Core unit test for `GamesKey`, a Data test for key parity. Seeding: ingest puts every release with one base title into one game (and later ones into `AmbiguousGroup` review), so duplicate groups are made by ingesting distinct games and then setting `roms.base_title` with raw SQL. Tests: `GamesKey` is independent of input order; the SQL key equals `GamesKey` for the same ids; integration — two games sharing a base title form a group with the suggested target first; different systems don't group; dismiss hides the group, adding a third game brings it back; dismiss twice → 204; dismiss validation 400s (missing field, unknown system, malformed key) and a stale key → 204; merge merges every source into the target (the sources are gone, the releases are on the target); validation 400s (`ValidationFailed`); a 404 source after one success returns the problem with `merged` holding the first id; a source in another system after one success returns `400 ValidationFailed` with `merged`; unknown `system` filter and `pageSize` over 200 → 400; a base title differing only in case groups together and is dismissed by either spelling; role matrix (user 403 for dismiss/merge, rom-manager 200/204). Done when: Api and Data tests pass.
- [ ] **16. Duplicates UI**: RP9. Files: new `routes/reports/duplicates/+page.svelte`, `reports-pages.test.ts`. Tests: vitest — groups render with the first radio pre-selected and labelled; a non-editor sees no radios or buttons; merge opens the confirm and posts the selected target and the other ids; dismiss posts the key; both reload the list; a partial-merge failure toast mentions the merged count. Done when: SPA tests pass.
- [ ] **17. Audit store, writer and retention**: AU1, AU2, AU5, migration 0014, the `AuditActor` record and `AuditPaths`. Files: `0014_audit_events.sql`, `Core/Audit/*` (`IAuditLog`, `AuditEvent`, `AuditActor`, `AuditActions`, `AuditPaths`, channel-backed `AuditLog`), `Data/Repositories/AuditRepository.cs`, new `Api/AuditWriter.cs` (hosted service, registered before `JobShutdownService`), `Api/Auth/AuditActorExtensions.cs` (`AuditActor.From(ClaimsPrincipal)`), `AppHost.cs`, `Scheduler.cs`, `tests/RnRomManager.Data.Tests/Database/SchemaMigrationTests.cs` (`Tables`, scripts count `13L` → `14L`), and tests in `RnRomManager.Data.Tests`, `RnRomManager.Core.Tests` and `RnRomManager.Api.Tests`. Tests:
  - Testcontainers: insert then read back every column; text over the column length is truncated; the purge deletes rows older than 30 days and keeps newer ones; the schema test lists `audit_events` with its columns and indexes.
  - Unit:
    - `Record` stamps the time from `FakeTimeProvider` and never blocks.
    - `Record` swallows and logs an internal failure (e.g. a throwing `TimeProvider`).
    - A full channel drops the event with a warning.
    - The writer logs and drops an event whose insert throws, and keeps going.
    - `FlushAsync` completes after the queue drains.
    - `AuditActor.From` reads the id and username.
    - `AuditPaths` gives `roms/…` and `ingest/…` for paths under each root, the file name only for a path outside both, and `ingest/<archive> › <entry>` for an archive entry.
    - The scheduler purge runs at startup and at 00:05 (`FakeTimeProvider`), and a throwing `PurgeAsync` is logged and doesn't stop the scheduler.

  Done when: Data, Core and Api tests pass.
- [ ] **18. Audit actor and ingest rows**: AU3 for jobs (`JobContext.Actor` as an optional last parameter, built by `JobRunner` with `IUserRepository`). AU4 rows `Ingested`, `ZipRepaired`, `ExtensionRenamed`, `MovedToFailed`, `MovedToDuplicates` and `ArchiveDeleted` from an ingest run. `IngestPipeline`/`IngestJob` signatures change as AU3 lists (`ProcessAsync` gains the actor, `FailFile`/`MoveToFailed`/`ApplyFate` swap `jobId` for it, `MoveToDuplicates`/`StoreReleaseAsync`/`PackageAsync` gain it), and `IngestPipeline` gains an `IAuditLog` constructor parameter. `ReviewResolver` keeps compiling by passing `new AuditActor(userId, null, null)` (and `reviewAction` for `StoreReleaseAsync`) until step 19 replaces it. Files: `IJob.cs`, `JobRunner.cs`, `AppHost.cs` (if the `JobRunner` registration changes), `IngestPipeline.cs`, `IngestJob.cs`, `ReviewResolver.cs` (those calls), and the tests that construct these or call `ProcessAsync` to seed: `JobRunnerTests`, `SchedulerTests`, `DatRefreshJobTests`, `IngestJobTests` (`JobRunner`, `IngestPipeline`, `IngestJob`), `IngestPipelineTests`, `IngestArchiveTests`, `ReviewResolverTests` (builds an `IngestPipeline`), `ImageEndpointsTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `ReviewEndpointsTests` and `IngestReviewTests`. Tests:
  - Unit: `JobRunner` builds the actor with the requester's username; with nulls for a scheduled run; and with a null username, the user id and a warning when the user lookup throws or finds nobody.
  - Integration, after `FlushAsync`:
    - An ingest run started by the admin writes `Ingested`, `MovedToFailed` and `MovedToDuplicates` rows with the job id, username and relative paths.
    - An archive entry's path is `ingest/<archive> › <entry>`, and the archive delete writes `ArchiveDeleted`.
    - A repaired ZIP writes `ZipRepaired`.
    - A renamed extension writes `ExtensionRenamed`.
    - A file failed by `FailFile` (e.g. too small) writes `MovedToFailed` with the reason code.
    - An ingest whose DB mirror fails (substitute mirror) still writes `Ingested`.
    - `.error.txt` still records the job id.

  Done when: Core and Api tests pass.
- [ ] **19. Audit review and DAT refresh**: AU3 for review (`ReviewResolver` methods take an `AuditActor` instead of `int userId`), AU4 rows from review (`Ingested` via `StoreReleaseAsync`, `MovedToFailed` on dismiss, `ReviewDeleted`) and `DatsRefreshed`. Files: `ReviewResolver.cs`, `ReviewEndpoints.cs`, `DatRefreshJob.cs`, `DatEndpoints.cs` (builds `DatRefreshJob` by hand), `ReviewResolverTests` (Core), `IngestReviewTests`, `ReviewEndpointsTests`, `DatEndpointsTests`, `DatRefreshJobTests`. Tests (integration, after `FlushAsync`):
  - Review resolve writes one `Ingested` row with the user, no job id and `review resolve (<action>)`.
  - Dismissing a loose file writes one `MovedToFailed` row, and its `.error.txt` reads `review by <username>`. Dismissing an archive entry writes no row.
  - Review delete writes `ReviewDeleted`; deleting an item whose file is already gone writes none.
  - A successful DAT refresh writes one `DatsRefreshed` row, and a failed one writes none.

  Done when: Core and Api tests pass.
- [ ] **20. Audit library edits**: AU3 for users, and AU4 rows `GameUpdated`, `ReleaseDeleted`, `GameDeleted`, `ReleaseMoved` and `GamesMerged`. `PatchAsync` takes an `AuditActor?` (null from the rating endpoint). `DeleteReleaseAsync`, `DeleteGameAsync`, `MoveReleaseAsync` and `MergeAsync` take an `AuditActor`. Every endpoint, including the RP8 group merge, passes the request actor. Files: `LibraryEditor.cs`, `LibraryEndpoints.cs`, `ReportEndpoints.cs`, and the edit, move, merge and delete tests. Tests (integration):
  - Each edit writes exactly its row, with the user, game and paths.
  - Deleting the last release writes `GameDeleted`, not `ReleaseDeleted`, and its target is the `_removed` folder.
  - `PATCH …/rating` writes no row.
  - A metadata `PATCH` that renames the ZIP writes `GameUpdated` with the old and new paths and the changed field names; a `PATCH` that changes nothing writes no row.
  - A release delete whose entry is missing from the ZIP writes `ReleaseDeleted` with a null target.
  - A merge whose DB update fails (substitute mirror) still writes `GamesMerged`, and the RP8 group merge lists that source in `merged`.
  - Existing tests compile and pass with the new parameter.

  Done when: Core and Api tests pass.
- [ ] **21. Audit images and media**: AU4 rows `ImageAdded`, `ImageDeleted`, `CoverSet` and `MediaFetched`. `AddImageAsync`, `DeleteImageAsync`, `SetCoverAsync`, `RetryMediaAsync` and `GameMediaRetry.RetryAsync` take an `AuditActor`, and `MediaRetryJob` passes `context.Actor`. Files: `LibraryEditor.cs`, `GameMediaRetry.cs`, `Core/Library/MediaRetryJob.cs`, `LibraryEndpoints.cs`, `LibraryImageEditTests`, `MediaRetryJobTests`, `ImageEndpointsTests`, `MediaRetryEndpointsTests`. Tests (integration):
  - Adding, deleting and setting the cover each write their row, with the image file name; setting the current cover again writes none.
  - A single-game media retry that wrote the ZIP writes exactly one `MediaFetched`; one that changed nothing writes none.
  - A `MediaRetryJob` run writes rows with its job id.

  Done when: Core and Api tests pass.
- [ ] **22. Audit API**: AU6. Files: `AdminEndpoints.cs` or a new `AuditEndpoints.cs`, `AuditRepository.cs` (the query), `AuditEndpointsTests.cs`, `EndpointRoleMatrixTests.cs`. Tests (integration):
  - Newest first, with a default page size of 100.
  - Each filter alone and combined.
  - `scheduled=true` matches null users, and together with `user` → 400.
  - `from` is inclusive and `to` exclusive.
  - An unparseable date or `from >= to` → 400 `ValidationFailed`.
  - An unknown `action` or `system` → 400.
  - `pageSize` over 200 → 400.
  - `/actions` lists the AU4 names.
  - Role matrix: user 403, admin 200.

  Done when: Api tests pass.
- [ ] **23. Audit UI**: AU7. Files: `routes/admin/+layout.svelte`, new `routes/admin/audit/+page.svelte`, a new `routes/admin/audit-page.test.ts` (like `logs-page.test.ts`), and a layout render test (`admin-pages.test.ts` never renders the layout today). Tests (vitest):
  - The Auditing tab is present, between Logs and About (the layout reads `page.url.pathname`, so this test's `$app/state` mock has a `url`).
  - The default request has no filters and a page size of 100.
  - Each control maps to its query param and to the URL.
  - The date inputs send local-midnight instants (To = the next day).
  - `Scheduled only` sends `scheduled=true` and disables User.
  - Clear.
  - `System` is shown for a null user.
  - The game link.
  - The empty state.

  Done when: SPA tests pass.
- [ ] **24. End-to-end**: in `tests/e2e/specs/mvp.spec.ts`:
  - Open Reports (each tab, axe-scanned) and drill into a system on Library Size.
  - Open a game's info.json modal, and switch to the Images tab (the existing images flow then runs inside it).
  - Keep the existing modal-based review delete as it is. Shift+click is covered by vitest, because the seed has only two review items and both are already used.
  - Open Admin → Auditing and see the ingest rows.
  - After closing a modal that navigates (delete a release), assert with a retrying `expect(page.locator('body')).not.toHaveClass(/modal-open/)` that `<body>` is released, and that the page scrolls. Transitions are live here (`openGame()` reloads, dropping `axe.ts`'s style tag), so this exercises the real fade race, though not deterministically.

  Done when: `pwsh scripts/test-e2e.ps1` passes.
- [ ] **25. Docs**:
  - `docs/features.md`: every behaviour above.
  - `docs/development.md`:
    - the `modal.ts` rule: modals must release `<body>` on destroy;
    - `JsonTree`, `treemap.ts`, `googleSearch.ts`, `review.ts` and the Reports routes;
    - the audit table, its retention and `AuditPaths`.
  - `README.md`: the feature list.

  Done when: the doc rule holds and `pwsh scripts/test-all.ps1` passes.

## Decisions
- 2026-11-06 One spec or several? → one spec, one PR (user)
- 2026-11-06 Where does Duplicates live? → a tab on the new Reporting page (user)
- 2026-11-06 Library Size in Admin and Reporting? → Reporting only (user)
- 2026-11-06 Who sees Reporting? → every signed-in user; actions keep their roles (user)
- 2026-11-06 Duplicate rule → same system + same release base title (user)
- 2026-11-06 Merge target in a group → user picks, most releases then oldest pre-selected (user)
- 2026-11-06 Library Size view → SVG squarified treemap + table, no new dependency (user)
- 2026-11-06 Missing image → no cover or placeholder cover, each labelled (user)
- 2026-11-06 Review filter → text + system + reason, server-side, kept in URL and localStorage (user)
- 2026-11-06 Audit detail → one row per file action; Admin only (user)
- 2026-11-06 Ingest selection → per-file checkboxes, all checked, not persisted (user)
- 2026-11-06 Scroll bug cause → modals disposed while shown leave `<body>` locked; fixed centrally in the modal components' destroy (code)
- 2026-11-06 Library size figure → on-disk ZIP size (`games.zip_size_bytes`); the Library page shows the primary ROM's size as asked (code)
- 2026-11-06 JSON viewer → native `<details>` tree, no dependency (code)
- 2026-11-06 Audit retention → 30 days, constant, purged with the daily log purge (code)
- 2026-11-06 Shift+click on an archive review item → still shows the modal and its warning (user)
- 2026-11-06 Audit rating/favourite clicks? → no; only metadata edits write `GameUpdated` (user)
- 2026-11-06 Audit writing → synchronous enqueue + background writer, so sync pipeline helpers and cancellation stay safe (reviewer)
- 2026-11-06 Audit actor → `AuditActor(userId, username, jobId)` built once per request (claims) or per job (`JobRunner`) (reviewer)
- 2026-11-06 Review resolve/dismiss audit → one row from the pipeline helper, with the user as actor (reviewer)
- 2026-11-06 Last-release delete → `GameDeleted` (reviewer)
- 2026-11-06 Audit "System" filter → separate `scheduled=true`, not a reserved username; dates as local-day instants (reviewer)
- 2026-11-06 Missing Images reason when both apply → `NoCover` (reviewer)
- 2026-11-06 e2e Shift+delete → vitest only; the e2e keeps the modal delete (reviewer)
- 2026-11-06 Google it on review items → in the item header, so AmbiguousGroup items (no Suggestions tab) get it too (user)
- 2026-11-06 Dismissing an archive-entry review item → no audit row; nothing on disk changes (user)
- 2026-11-06 File changed but DB mirror failed (`DbWriteFailed`) → the audit row is still written, and a group merge counts that source as merged (user)
- 2026-11-06 System display name for the Google queries → additive `systemName` on the review and game detail DTOs (code)
- 2026-11-06 URL state for the review filter and game tabs → the house `goto(…, { replaceState: true })` pattern from the Library page, not shallow `replaceState` (code)
- 2026-11-06 G1 test → unit-test `releaseModal` on a hand-seeded `<body>`, because every SPA test mocks Bootstrap's `Modal` and the e2e disables transitions (code)
- 2026-11-06 Audit steps → split into store, ingest, review/DAT, library edits, images/media, API, UI, keeping each step near the 300-line guide (review)
- 2026-11-06 `JobRunner` user lookup failure → null username, keep the id, log a warning, start the job anyway (code)
- 2026-11-06 Review `.error.txt` without a job → `review by <username>` instead of a made-up job id (code)
- 2026-11-06 Review 2 (code check against the codebase):
  - URL-state `goto` options are spelled out; the Library page's `{ reset: false }` isn't copied (not a SvelteKit option).
  - GP3 buffers the entry through a new `LibraryFiles.ReadEntry`, because `OpenEntry` can only fail mid-stream and `Fail` is private.
  - Paged report and audit endpoints use the house `Paging.Validate`/`ToDto` (max 200, `{ items, page, pageSize, total }`); unknown `system` filters are `400`; blank `q` is absent, as on `/api/games`.
  - The duplicates games key is computed in SQL so paging excludes dismissed groups; a parity test pins it to `DuplicateGroups.GamesKey`.
  - Every RP8 failure carries `merged`, including a mid-loop `ValidationFailed`.
  - `IngestPipeline`'s `ProcessAsync`/`FailFile`/`ApplyFate`/`MoveToDuplicates` carry the actor too; `StoreReleaseAsync` takes the review action name, not a flag.
  - Audit edge cases: no row for a no-op PATCH, a no-op cover set or a review delete of a missing file; `ReleaseDeleted` with a missing entry has a null target; cleanup/undo deletes aren't audited.
  - The audit purge gets its own catch-all; the writer's drain shares the host shutdown timeout (accepted).
  - `localStorage` key is `rnrm.review.filter` (house prefix).
  - The e2e body check retries, because transitions are live after `openGame()` reloads.

## Kickoff prompt
```text
/spec implement 2026-11-06-reports-audit-qol.md
```
Starts the spec, or resumes it at the first unticked step.
