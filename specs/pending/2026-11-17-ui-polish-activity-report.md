---
title: UI polish and Activity report
status: ready
created: 2026-11-17
---

# UI polish and Activity report

## Summary
A batch of UI fixes across Library, Game detail, Collections, Reports, ROM Tools, About, footer, Global Search and Admin, plus a new Admin-only **Activity** report (recent added ROMs, sign-ins, browser plays, saves, edits, deletions, jobs, last backup). Browser plays become a new `GamePlayed` audit event so they can be reported. For every signed-in user; Activity and Admin pages for Admins only.

## Out of scope
- New storage for plays beyond the audit log (no migration; plays, saves, edits, deletions and sign-ins in Activity cover the 30-day audit retention only).
- Auditing release-note edits or ratings.
- Redesigning pages beyond the listed items; no new dependencies (Bootstrap 5.3 + Bootstrap Icons only).

## Behaviour

### Library (`routes/+page.svelte`)
- **B1** The **Select** button moves into the search/filter row, at its right end, same `btn-sm` size as the filter dropdowns.
- **B2** A **Clear all** button (icon `clear` → Bootstrap Icons `x-circle`, + text) sits beside Select, always visible, disabled when the search box is empty and the URL has no `q`, `system`, `region`, `letter`, `favourite` or page > 1 (`hasFilters` plus page and the box). `resetFilters()` changes to clear the search box (cancelling its pending debounce), all filters and page while keeping `sort`/`dir` in the URL (today it also resets sort); the saved filter (`rnrm.library.filter`) is then re-saved by the normal save path, holding just the sort (removed when the sort is the default). Grid/list view (`readView`, localStorage key `libraryView`) is untouched; page size is the fixed `libraryPageSize` (48). It replaces the filter row's "Reset filters" button (today shown only when `showChipRow`); the empty state's "Clear filters" calls the same function.

### Game detail (`routes/games/[id]/+page.svelte`)
- **B3** Action buttons: one `d-flex flex-wrap gap-2` row of uniform `btn btn-sm btn-outline-secondary` buttons, each icon + text: Download (ZIP/ROM as today), View info.json, Google, Collections. Admin actions (Edit metadata, Retry media, Merge, Delete game) move into one **Manage** dropdown (`Dropdown` from `#lib/bootstrap.js`, `Icon name="manage"` → `gear`), Delete last after a divider in `text-danger`. Site users don't see Manage. Wrapping keeps an even gap on both axes.
- **B4** Each tab gets an icon before its label: Releases `releases`, Updates `updates`, Images `images`, Saves `saves`, Zip Contents `zip` (semantic names in `icons.ts`; Bootstrap Icons `collection`, `arrow-up-circle`, `images`, `floppy`, `file-zip`). Counts stay.
- **B5** "← Library" becomes a `btn btn-sm btn-outline-primary` with `Icon name="back"` (`arrow-left`) and text "Back to Library"; it still links to `/` (which restores the saved filter).

### Releases tab release card
- **B6** Header: chevron + release name on the left (`min-width: 0`, name `text-break`); on the right (`ms-auto`, `d-flex flex-wrap gap-2 align-items-center justify-content-end`) first the existing badges in today's order (Primary, Version, Disc, External, regions), then the buttons in today's order: Note, Play (or the PSP link), Download, Bundle (`bundleButton`). **Delete** leaves the header (B9). Nothing else is added or dropped.
- **B7** Expanded body: when `hasNote`, the note loads on first expand (`GET /api/games/{id}/roms/{romId}/note`). The cache is keyed by `rom.fileName` (ROM ids change on every ZIP rewrite, as `openReleases` already handles); after the game refetch that follows a note save/delete, the open card reloads its note with the new `romId`. The note renders through `renderMarkdown` in a bordered, non-interactive block labelled "Release note" (its links work as normal links), with a separate `btn-sm` **Edit note** (Admin) / **View note** (Site user) button beside the heading that opens `ReleaseNoteModal` in Edit or View mode. A load failure shows a small inline `ErrorAlert` with retry; a `404` (deleted meanwhile) or no note → nothing.
- **B8** The body's info `<dl>` becomes a key/value table per the table rules (T1–T4: `table table-sm table-striped table-hover mb-0` in `card` > `div.table-responsive`, `<th scope="row">` + `<td>`). The Header details modal is unchanged.
- **B9** The body's existing actions row (after the info table, before the files toggle; shown when `rom.header || auth.isAdmin` as today) keeps Header details on the left and right-aligns (`ms-auto`) Move release (existing) and **Delete release** (`btn-sm btn-outline-danger`, icon + text, `aria-label="Delete release <fileName>"` like Move release, Admin only, same confirm flow as today).

### Images, Saves, Zip Contents tabs
- **B10** Images: each tile lifts on hover/focus-within (`shadow` + slight `translateY`, `prefers-reduced-motion` disables the move). Each image gets a **Download** icon button (`aria-label="Download <fileName>"`, like its Delete button), first in an actions row shown to every user (the Admin's Use as cover and Delete follow it) — an `<a download>` to `GET /api/games/{id}/images/{imageId}` (add `?download=true`, a `bool?` like the audit list's `scheduled`; minimal APIs reject `1` → `Content-Disposition: attachment` with `<game title>-<imageId>.<ext>`, sanitized by `FileNameSanitizer.Sanitize`). The image's size badge (`<FileSize>`) sits top-right over the thumbnail like `GameResults`' card badge; dimensions stay under the image.
- **B11** Saves: `GameSaves` follows the table rules (T1–T4), wrapped in a card with a card-header holding the heading and Upload button like other tabbed lists, row actions right-aligned icon buttons with `aria-label`, `EmptyState` when none.
- **B12** Zip Contents: folder panel `col-md-3`, detail `col-md-9`. Folder rows leave Size, Compressed and Modified cells empty (no `—`).

### Collections (`SystemList`, `DiscoveryGroups`, `GameResults`)
- **B13** Selector names truncate with an ellipsis on one line (`text-truncate`, `min-width: 0` on the flex child) and carry `title` with the full name.
- **B14** Every selector-list logo renders in a fixed frame: `border rounded bg-body-tertiary` with fixed size and `object-fit: contain`, for shared logos, starter art and the text banner alike.
- **B15** Count badges use `badge rounded-pill text-bg-primary` in light theme and `bg-primary-subtle text-primary-emphasis` in dark (one rule in `app.css` keyed on `[data-bs-theme=dark]`), checked by axe in both themes.
- **B16** A search box tops each list (`input type="search"`, label visually hidden "Search publishers/genres/systems"). Publishers/Genres: the API query parameter is `q` (the SPA URL parameter is `find`, below; the repository parameter is `q`): `GET /api/discovery/{kind}?q=` filters groups whose label contains `q` (trimmed; blank or whitespace = absent; over 100 chars → `400`; ordinal ignore-case), applied before paging; it doesn't change grouping or keys. Systems filter client-side by display name or id. The URL parameter is `find` (`q` is already taken by User collections on `/collections`); `discoverySearch` and `systemsSearch` carry it when a group is selected or paged, `tabSearch` drops it on tab change, and the client omits an empty value. Typing is debounced 300 ms and resets `groupPage` to 1; the selected group stays selected even when filtered out. No match → `EmptyState`.
- **B17** In the collection game grid/list card, the `RatingControl` stays on one line (`text-nowrap`, the card's title/meta stack above it; stars shrink to `fs-6` at narrow card widths rather than wrap).

### Reports (`routes/reports`)
- **B18** Report tabs get icons: Library Size `report-size` (`pie-chart`), Missing Images `report-missing-images` (`image-alt`), Duplicates `report-duplicates` (`files`), Activity `activity` (`activity`).
- **B19** Library Size: each system row's System cell has a right-aligned `btn-sm` icon link "Open <system> in Library" (`Icon name="library"`) to `/?system=<id>`.
- **B20** **Activity** tab (`/reports/activity`), wrapped in `AdminOnly`, its tab hidden for Site users. Sections (each a card with a table per T1–T4, `EmptyState` when empty, rows link to the game/job where one exists):
  1. Last 20 added ROMs: `roms.date_added` desc — time, game title (link), release name, system badge, size.
  2. Last signed-in user (`users.last_login_at` max) shown as a summary line, then the last 20 `SignedIn`/`SignInFailed`/`LockedOut` events — time, user, outcome.
  3. Last 20 browser plays (`GamePlayed`) — time, user, game, release.
  4. Last 20 saves (`SaveAdded`/`SaveUpdated`) — time, user, game, action.
  5. Last 20 edits (`GameUpdated`, `ReleaseMoved`, `GamesMerged`, `CoverSet`, `ImageAdded`, `ImageDeleted`) — time, user, game, action.
  6. Last 20 deletions (`GameDeleted`, `ReleaseDeleted`) — time, user, game title from `details`/path (game may be gone, so no link when the game no longer exists), action.
  7. Recent jobs: last 10 from the existing `GET /api/jobs` — type, status, started, duration, link to `/jobs/{id}`.
  8. Last backup: from the existing `GET /api/admin/backups`: the in-memory last result (time + outcome) when present; after a restart (null) the newest backup file's name and time, labelled "since restart: no backup run"; no files → `EmptyState`.
  A note under the heading says audit-based sections cover the last 30 days. The three calls (`/api/reports/activity`, `/api/jobs?pageSize=10`, `/api/admin/backups`) load in parallel; each source's sections show `Loading` until it answers, and a failing source shows an `ErrorAlert` with retry in its own sections while the others render.
- **B21** `GET /api/reports/activity` (Admin; Site user `403`) returns, all times UTC ISO-8601 with `Z` (as `/api/admin/audit`'s `occurredAt`), each list ≤ 20, newest first (ties by id desc):
  - `lastSignIn`: `{ username, at }` or `null`.
  - `addedRoms`: `{ romId, gameId, gameTitle, fileName, systemId, sizeBytes, addedAt }`.
  - `signIns`, `plays`, `saves`, `edits`, `deletions`: audit rows `{ id, occurredAt, action, user, system, gameId, gameTitle, path, details }` (the `/api/admin/audit` item names), where `gameTitle` comes from `LEFT JOIN games` and is `null` when the game is gone. For a missing title the client shows `details` (deletions record the game's name there) or else the file name from `path`; the server builds no fallback.

### Browser plays
- **B22** `POST /api/games/{id}/roms/{romId}/played` (any signed-in user, antiforgery as all POSTs) records `GamePlayed` (system, game, actor, `details` = release name) and returns `204`; unknown game or a release not in it → `404`. No job lock. The SPA calls it fire-and-forget (errors ignored) from one place for the modal: `EmulatorModal` when it opens, so every modal play counts (game page, Library and collection quick play, Global Search, play-with-save from `GameSaves`). Every PSP `target="_blank"` play link (game page release card and `GameResults`' `playAction`) records on click through the same helper in `lib/emulator.ts`, and so does Global Search's PSP path (B27).

### ROM Tools
- **B23** Ingestion: Pending files, Failed files and Last runs become an accordion (`AccordionItem`, below) under the Run section. **Import all** and **Import selected (n)** move into the Pending files panel's top bar. Pending files starts open; the others start closed; panels open independently. `?tab=failed|runs` still opens that panel (and keeps Pending open), so old links work; toggling no longer rewrites the URL. Headers keep the counts ("Pending files (n)").
- **B24** Integrity: Issues and History become accordion panels, Issues open by default, independent.
- **AccordionItem** (`lib/components/AccordionItem.svelte`): Bootstrap accordion markup (`accordion-item`, `accordion-header` with `<h2>`, `accordion-button` with `aria-expanded`/`aria-controls`, `accordion-collapse collapse show`), open state held in Svelte (`bind:open`), no Bootstrap Collapse JS; collapsed content stays mounted (selection state survives). The panel's existing heading becomes the accordion header's `<h2>` (the visually hidden `<h2>`s in the Ingestion panels and Integrity's `issues-heading`/`history-heading` go, their ids move to the header so `aria-labelledby` still resolves); no duplicate headings.

### About modal and footer
- **B25** About: the licences table sits in a `max-height: 40vh; overflow-y: auto` wrapper with a sticky `thead`; a search box above filters rows by name or licence (case-insensitive contains), showing "No licences match" via `EmptyState`. The modal body itself no longer scrolls for it.
- **B26** Footer: the shortcuts button shows `Icon name="help"` + visible text "Shortcuts"; remove its `aria-label` (the visible text names it); tooltip "Keyboard shortcuts".

### Global search
- **B27** `GlobalSearch` already gets `playRomId`, `playRomFileName`, `playDisc` and `browserCore` from `/api/games`; no server change. A result with a `playRomId` shows a Play icon at the row's right end: `aria-hidden="true"`, not focusable (a pointer shortcut like the treemap tiles), its click calls `stopPropagation()` so the row's `choose()` doesn't also run. Keyboard: **Shift+Enter** in the search input plays the active result (plain Enter still opens it; Shift+Enter on a collection or a game without `playRomId` does nothing); the playable option gets `aria-describedby` pointing at a visually hidden "Shift+Enter to play". Playing closes the search and navigates to `/games/{id}?play=<playRomId>`; the game page, once loaded, opens the Play modal for that release and removes `play` from the URL with `replace`. For PSP (`opensInNewTab`) it opens the play tab (`window.open` to `playUrl`, `noopener`) and navigates to the game page without `?play=`. `?play=` naming a release that isn't playable or isn't in the game is ignored and removed.

### Admin
- **B28** DATs: "Automatic refresh" and "DAT files" become independent accordion panels, both open by default.
- **B29** Each DAT row gets a Download icon button (`aria-label="Download <datName>"`) to `GET /api/admin/dats/{datName}/download` (Admin): the server finds the live manifest entry with that exact `datName` (main DAT or titledb file, same filter as the list), opens it under the cache root through the checked path code (`ExternalRomPath`), and streams it as an attachment with its file name. Unknown name or a file that doesn't classify as Found → `404`. The client never sends a path.
- **B30** Auditing: 5 columns, no horizontal scroll: Time (`text-nowrap`), Action (with the system badge below it), User, Game (title link from `gameTitle`; the short id when the game is gone; `—` when none), Change (path → target and details stacked, `text-break`, wrapping). `GET /api/admin/audit` items gain `gameTitle` (`LEFT JOIN games`); the page's local `AuditItem` interface gains it too. Pagination is shown above and below the table. The `.table-responsive` wrapper stays (rule T1) but nothing overflows at ≥ 992 px.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `Core/Audit/AuditActions.cs` | `GamePlayed` added to the constants and `All` list |
| Core | `Core/Audit/IAuditLog.cs` (declares `IAuditRepository`) | `ListRecentAsync(IReadOnlyList<string> actions, int limit)` returning rows with `GameTitle`; `AuditItem` (`Core/Audit/AuditQuery.cs`) gains `GameTitle` |
| Core | `Core/Reports` (`IReportRepository`) | `ListRecentlyAddedRomsAsync(int limit)` |
| Core | `Core/Users` (`IUserRepository`) | `GetLastSignInAsync()` (max `last_login_at`) |
| Core | `Core/Discovery/IDiscoveryRepository.cs` | `ListGroupsAsync(kind, q, page, pageSize)` |
| Data | `Data/Repositories/AuditRepository.cs`, `ReportRepository.cs`, `UserRepository.cs`, `DiscoveryRepository.cs` | queries above; the group list filters the cached index by label |
| Api | `Endpoints/LibraryEndpoints.cs` | `POST /games/{id}/roms/{romId}/played`; image route `?download=true` |
| Api | `Endpoints/ReportEndpoints.cs` | `GET /reports/activity` (Admin) |
| Api | `Endpoints/DatEndpoints.cs` | `GET /admin/dats/{datName}/download` (Admin) |
| Api | `Endpoints/AuditEndpoints.cs`, `DiscoveryEndpoints.cs` | `gameTitle` in items; `q` on group list |
| Tests | `tests/RnRomManager.Api.Tests/Integration/EndpointRoleMatrixTests.cs` | entries for the three new routes (played: User; activity, DAT download: Admin) |
| Web | `lib/api/types.ts` | Activity response types (the audit item type is local to `routes/admin/audit/+page.svelte`) |
| Web | `lib/components/AccordionItem.svelte` (new), `icons.ts`, `GameImages`, `GameSaves`, `ZipBrowser`, `SystemList`, `DiscoveryGroups`, `GroupLogo`, `GameResults`, `AboutModal`, `AppFooter`, `GlobalSearch`, `EmulatorModal`, `ReleaseNoteModal`, `lib/discovery.ts`, `lib/emulator.ts`, `app.css` | as Behaviour |
| Web | `routes/+page.svelte`, `routes/games/[id]/+page.svelte`, `routes/reports/+layout.svelte`, `routes/reports/library-size`, `routes/reports/activity` (new), `routes/ingestion`, `routes/integrity`, `routes/admin/dats`, `routes/admin/audit` | as Behaviour |
| e2e | `tests/e2e/specs/mvp.spec.ts` | selectors that break: ingestion tabs → accordion buttons (B23); "Reset filters" → "Clear all", now disabled instead of hidden (B2); "← Library" → "Back to Library" (B5); Edit metadata / Delete game behind Manage (B3); release Delete → "Delete release <fileName>" in the expanded body (B9); audit `cell` matchers on the Action cell, which now also holds the system badge (B30); "Keyboard shortcuts" button → "Shortcuts" (B26). New: Activity tab visit + axe; DAT download; Play from Global Search |
| Docs | `docs/features.md`, `docs/development.md` (component list, AccordionItem, Activity, audit `GamePlayed`), `README.md` screenshots if regenerated | doc rule |

No migrations, no config keys. API changes are additive (new routes, new optional `q`, new response fields).

## Risks
- **Audit volume:** one `GamePlayed` row per play start; negligible next to ingest rows, purged at 30 days.
- **e2e selectors** break for B2, B3, B5, B9, B23, B26 and B30 (Changes); each is updated in the step that changes it.
- **DAT download path safety:** the name is only a lookup key into the checked manifest; no client path reaches the file system.
- **Discovery search vs "app never normalizes names":** `q` is a display filter on labels only; grouping and keys are untouched (Decisions).
- **Hidden Activity tab:** the route itself is guarded by `AdminOnly` and the API by `AdminOnly`, so a Site user typing the URL sees "Not allowed".

## Steps
- [ ] **1. GamePlayed audit + played endpoint**: B22 server side. Files: `Core/Audit/AuditActions.cs`, `Api/Endpoints/LibraryEndpoints.cs`, `EndpointRoleMatrixTests.cs` (User entry). Tests: `GamePlayedEndpointTests` (new, integration: 204 + row for Site user and Admin, 404 unknown game / foreign release, 401 anonymous), `EndpointRoleMatrixTests`. Done when: endpoint records exactly one `GamePlayed` row with system, game, user, release name; matrix covers the route; tests pass.
- [ ] **2a. Audit gameTitle**: B30 API part. `AuditItem` (positional record in `Core/Audit/AuditQuery.cs`, mapped by Dapper's constructor, so every query selecting it must select the title) gains `GameTitle`; `ListAsync` `LEFT JOIN games`. Files: `AuditQuery.cs`, `AuditRepository.cs`, `AuditEndpoints.cs`. Tests: `AuditRepositoryTests`, `AuditEndpointsTests` (title present, null for deleted game). Done when: `/api/admin/audit` items carry `gameTitle`; tests pass.
- [ ] **2b. Activity queries**: B21 data. Files: `Core/Audit/IAuditLog.cs` + `AuditRepository` (`ListRecentAsync`, reusing 2a's `AuditItem`), `IReportRepository`/`ReportRepository` (`ListRecentlyAddedRomsAsync`), `IUserRepository`/`UserRepository` (`GetLastSignInAsync`). Tests: `AuditRepositoryTests` (action filter, limit, newest first, null title for a gone game), `RecentlyAddedRomsTests` (new, integration, `tests/RnRomManager.Data.Tests/Database`), `UserRepositoryTests` (integration; null when nobody signed in). Done when: each query returns newest first capped at its limit; tests pass.
- [ ] **2c. Activity API**: B21 endpoint. Files: `ReportEndpoints.cs`, `EndpointRoleMatrixTests.cs` (Admin entry). Tests: `ActivityReportEndpointTests` (new: Admin 200 with the B21 field names and ordering, limit 20, deleted game → null `gameTitle`, Site user 403), `EndpointRoleMatrixTests`. Done when: all lists newest first and capped at 20; tests pass.
- [ ] **3a. DAT download**: B29 API. Files: `DatEndpoints.cs`, `EndpointRoleMatrixTests.cs` (Admin entry). Tests: `DatEndpointsTests` (bytes + attachment name for a main DAT and a titledb file, unknown name 404, Site 403), `EndpointRoleMatrixTests`. Done when: tests pass.
- [ ] **3b. Image download**: B10 `?download=true`. Files: `LibraryEndpoints.cs`. Tests: `ImageEndpointsTests` (attachment header + sanitized name; without the flag unchanged). Done when: tests pass.
- [ ] **3c. Discovery search**: B16 API. Files: `IDiscoveryRepository.cs`, `DiscoveryRepository.cs`, `DiscoveryEndpoints.cs`. Tests: `DiscoveryRepositoryTests` (filter, paging after filter, blank = absent), `DiscoveryEndpointsTests` (400 over 100 chars). Done when: tests pass.
- [ ] **4. AccordionItem + ROM Tools**: B23, B24. Files: `AccordionItem.svelte` (+ test), `routes/ingestion/+page.svelte`, `routes/integrity/+page.svelte`, `tests/e2e/specs/mvp.spec.ts` (ingestion selectors). Tests: `AccordionItem.test.ts`, `ingestion-page.test.ts` (Import buttons inside Pending, `?tab=runs` opens runs, Pending open by default), `integrity-page.test.ts`. Done when: lint/check/vitest pass.
- [ ] **5. Admin DATs + Auditing**: B28, B29 (UI), B30. Files: `routes/admin/dats/+page.svelte`, `routes/admin/audit/+page.svelte` (its local `AuditItem` gains `gameTitle`), `tests/e2e/specs/mvp.spec.ts` (DAT download; audit Action-cell matchers). Tests: `dats-page.test.ts` (download links, both panels open), `audit-page.test.ts` (5 columns, game title link, short id fallback, pagination top+bottom), `table-style.test.ts`. Done when: tests pass.
- [ ] **6. Library + Library Size + report icons**: B1, B2, B18 (existing tabs), B19. Files: `routes/+page.svelte`, `routes/reports/+layout.svelte`, `routes/reports/library-size/+page.svelte`, `icons.ts`, `tests/e2e/specs/mvp.spec.ts` (Reset filters → Clear all). Tests: `library-page.test.ts` (Clear all disabled/enabled, clears search+filters+saved filter, keeps sort/view), `library-select.test.ts`, `reports-pages.test.ts` (link `/?system=`). Done when: tests pass.
- [ ] **7. Activity page**: B20, B18 (Activity tab, hidden for Site). Files: `routes/reports/activity/+page.svelte` (no `+page.ts`; the other reports have none), `lib/api/types.ts` (Activity types), `routes/reports/+layout.svelte`, `tests/e2e/specs/mvp.spec.ts` (visit + axe). Tests: `activity-page.test.ts` (new: sections render, empty states, one failing source shows its error while the others render, Site user tab hidden + Not allowed). Done when: tests pass.
- [ ] **8. Game page header, buttons, tabs, back**: B3, B4, B5. Files: `routes/games/[id]/+page.svelte`, `icons.ts`, `tests/e2e/specs/mvp.spec.ts` ("Back to Library"; open Manage before Edit metadata / Delete game). Tests: `game-page.test.ts`, `game-admin.test.ts` (Manage menu items for Admin, absent for Site), `game-collections.test.ts`. Done when: tests pass.
- [ ] **9. Release card**: B6–B9. Files: `routes/games/[id]/+page.svelte` (the card stays in the page; extracting it would move ~250 lines and blow the step size), `ReleaseNoteModal.svelte` (new optional `initialMode: 'edit'` prop: an Admin's Edit note keeps Edit mode after the note loads; without it, today's behaviour: View once an existing note loads, Edit for an Admin's new note), `tests/e2e/specs/mvp.spec.ts` (expand the release, then "Delete release <fileName>"). Tests: `release-note.test.ts` (lazy load once on expand keyed by file name, reload with the new id after save, Edit/View button opens the modal in the right mode by role, error alert), `game-page.test.ts` (delete in body, header order), `table-style.test.ts`. Done when: tests pass.
- [ ] **10. Images, Saves, Zip tabs**: B10–B12. Files: `GameImages.svelte`, `GameSaves.svelte`, `ZipBrowser.svelte`. Tests: `game-images.test.ts` (download link + label, size badge), `GameSaves.test.ts`, `ZipBrowser.test.ts` (no `—` on folder rows, col-md-3), `table-style.test.ts`. Done when: tests pass.
- [ ] **11a. Collections list styling**: B13–B15, B17. Files: `SystemList.svelte`, `DiscoveryGroups.svelte`, `GroupLogo.svelte`, `GameResults.svelte`, `app.css`. Tests: `SystemList.test.ts`, `DiscoveryGroups.test.ts` (truncation + `title`), `GroupLogo.test.ts` (fixed frame for every source), `GameResults.test.ts` (rating `text-nowrap`). Done when: tests pass.
- [ ] **11b. Collections search**: B16 SPA. Files: `SystemList.svelte`, `DiscoveryGroups.svelte`, `lib/discovery.ts` (`find` URL state; sends `q` to the API), `routes/collections/+page.svelte`. Tests: `DiscoveryGroups.test.ts` (search sends `q`, resets page, keeps selection, no match → empty state), `SystemList.test.ts` (client filter by name or id), `discovery.test.ts` (URL state: `find` kept on select/page, dropped on tab change, empty omitted), `collections-page.test.ts`. Done when: tests pass.
- [ ] **12a. Play recording**: B22 SPA call. Files: `lib/emulator.ts` (record helper), `EmulatorModal.svelte`, `GameResults.svelte` (PSP link click), `routes/games/[id]/+page.svelte` (PSP link click). Tests: `emulator.test.ts` (helper POSTs, swallows errors), `EmulatorModal.test.ts` (POST played on open, error ignored), `GameResults.test.ts` (PSP link records), `game-page.test.ts` (PSP link records). Done when: tests pass.
- [ ] **12b. Global Search play**: B27. Files: `GlobalSearch.svelte`, `routes/games/[id]/+page.svelte` (`?play=`), `tests/e2e/specs/mvp.spec.ts` (play from search). Tests: `GlobalSearch.test.ts` (Shift+Enter plays, Enter opens, Shift+Enter on a non-playable option does nothing, icon click doesn't open, PSP opens tab and records), `game-page.test.ts` (`?play=` opens modal and is removed; non-playable ignored). Done when: tests pass.
- [ ] **12c. About and footer**: B25, B26. Files: `AboutModal.svelte`, `AppFooter.svelte`, `tests/e2e/specs/mvp.spec.ts` ("Keyboard shortcuts" button → "Shortcuts"). Tests: `AboutModal.test.ts` (filter, no-match empty state), `AppFooter.test.ts` (accessible name "Shortcuts", tooltip). Done when: tests pass.
- [ ] **13. Docs**: `docs/features.md` (Library Clear all, game page, Activity, DAT download, Collections search, Global Search play, accordions), `docs/development.md` (AccordionItem, Activity, `GamePlayed`, components list). Done when: every Behaviour rule is documented.

## Decisions
- 2026-11-17 Where do browser plays come from? → New `GamePlayed` audit event, no migration; 30-day window (user)
- 2026-11-17 Who sees Activity? → Admin only (user)
- 2026-11-17 Extra Activity sections → recent jobs, last backup, last 20 sign-ins, recently deleted (user)
- 2026-11-17 Game page buttons → uniform outline row + Admin "Manage" dropdown (user)
- 2026-11-17 Clear all scope → search + filters + page + saved filter; keeps sort/view/page size; replaces "Reset filters" (user)
- 2026-11-17 Audit table → 5 wrapping columns with game title, pagination top and bottom (user)
- 2026-11-17 Accordions → independent panels, default panel open; Ingestion `?tab=` still honoured (user)
- 2026-11-17 Footer text → "Shortcuts" (code: user left it open)
- 2026-11-17 Dark-mode count colour → `bg-primary-subtle text-primary-emphasis` (code: user left it open)
- 2026-11-17 Discovery search → server-side label contains (ordinal ignore-case) since groups are paged server-side; it is a display filter, not normalization (code)
- 2026-11-17 Global Search Play → aria-hidden Play icon for the mouse + Shift+Enter for the keyboard (results are listbox options, so a focusable button would fail axe) (user)
- 2026-11-17 Which plays count → every modal open (incl. play-with-save and collection quick play) and every PSP tab link (reviewer)
- 2026-11-17 Release note in the card → non-interactive rendered block + separate Edit/View note button (links in Markdown would nest interactive content) (reviewer)
- 2026-11-17 Collections search URL parameter → `find`, since `q` is taken by User collections (reviewer)
- 2026-11-17 "Import panel" default → the Pending files panel, which now holds the Import buttons (code)
- 2026-11-17 B2 "Reset filters" location and page size → it sits in the filter row (not the chip row); `libraryPageSize` is a constant 48, not storage; Clear all is enabled by a search-box draft too and cancels its debounce (code)
- 2026-11-17 B10 image download flag → `?download=true`, since a `bool?` query parameter rejects `1`; label `Download <fileName>` like the tile's Delete, in an actions row every user sees (code)
- 2026-11-17 B7 note 404 in the card → nothing shown, as `ReleaseNoteModal` treats it as deleted (code)
- 2026-11-17 B9 Delete release name and placement → `aria-label="Delete release <fileName>"` (visible text in the name, like Move release); joins the existing actions row, Header details left, Move/Delete right (reviewer)
- 2026-11-17 B20 partial failures → three parallel calls, each source's sections show their own Loading/ErrorAlert (reviewer)
- 2026-11-17 B21 field names → `system` (not `systemId`) and `Z` UTC times, matching `/api/admin/audit` (code)
- 2026-11-17 Accordion headings → the panel's existing heading becomes the accordion `<h2>`, no duplicates (reviewer)
- 2026-11-17 Audit item type → local to `routes/admin/audit/+page.svelte`, not `lib/api/types.ts` (code)
- 2026-11-17 e2e selectors → B2, B3, B5, B9, B26 and B30 also break `mvp.spec.ts`; each step that changes them updates it (code)
- 2026-11-17 Step order → audit `gameTitle` (old 3a) moves before the Activity queries as 2a, since `AuditItem` is a positional record Dapper maps by constructor; Activity API split into 2b queries + 2c endpoint (code)
- 2026-11-17 Step size → 11 split into 11a styling + 11b search, 12 into 12a play recording, 12b Global Search play, 12c About/footer; step 9 keeps the release card in the page (reviewer)
- 2026-11-17 `ReleaseNoteModal` mode → optional `initialMode: 'edit'`; without it today's behaviour (View for an existing note, Edit for an Admin's new one), not "default view" (code)

## Kickoff prompt
```text
/spec implement 2026-11-17-ui-polish-activity-report.md
```
Starts the spec, or resumes it at the first unticked step.
