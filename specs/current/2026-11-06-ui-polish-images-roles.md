---
title: UI polish, image management and roles
status: in-progress
created: 2026-11-06
---

# UI polish, image management and roles

## Summary
A batch of UI and feature work requested together: compact, consistently styled tables with pagination; a hard delete for review items; a reworked Review item page; Admin Users/Logs changes (create-user modal, live log stream); a ROM download dropdown, image management and a nicer Releases table on the Game page; one modal-dismiss rule; and global search over developers, publishers and genres. Image management and library edits move to a new `rom-manager` role, which replaces the single Admin/User role with a set of roles.

## Out of scope
- Roles beyond `admin` and `rom-manager`, or per-role admin UI beyond checkboxes on the Users page.
- Image conversion, resizing, cropping or thumbnails (uploads are stored as-is).
- Image types other than `BoxArt` and `Snap` for uploads (`TitleScreen`/`Other` stay read-only).
- Server-side pagination for the Ingestion "Pending files" list (it is client-side) and a page-size selector anywhere except the Review queue.
- FULLTEXT search indexes; restyling pages not named here (Library, Integrity, Jobs, Login, Change password).

## Behaviour

### Roles
- **R1** Users have a set of roles from `admin` and `rom-manager` (possibly empty). Stored in a new `user_roles` table; `users.role` is dropped.
- **R2** `admin` grants everything the old `Admin` role granted (every `AdminOnly` endpoint/page). `rom-manager` grants library edits: `PATCH /api/games/{id}`, `DELETE /api/games/{id}`, `DELETE /api/games/{id}/roms/{romId}`, `POST /api/games/{id}/roms/{romId}/move`, `POST /api/games/{id}/merge`, `POST /api/games/{id}/media/retry`, and the image endpoints (I5–I7). Roles don't imply each other: an `admin` without `rom-manager` cannot edit the library. `POST /api/jobs/media-retry` (a job) stays `admin`.
- **R3** Migration: `Admin` → `admin` + `rom-manager`; `User` → no roles. Every user's `security_stamp` is rotated, so all existing sessions end (old cookies carry the old role claim) and users sign in again.
- **R4** The seeded first user and the `--reset-password` CLI's "no active admin left" promotion give `admin` + `rom-manager`. The last-active-admin protection (cannot disable or remove `admin` from the last enabled `admin`) counts users with the `admin` role. Response code unchanged (whatever `UserAdminService` returns today).
- **R5** API contract: `GET /api/auth/me` and `GET /api/users` return `roles: string[]` (sorted) instead of `role`. `POST /api/users` takes `{ username, password, roles }` (`roles` missing or null → `[]`); `PATCH /api/users/{id}` takes `{ roles?, isDisabled? }` (missing/null → unchanged). `roles` must be an array of distinct known role names (else `400 ValidationFailed`, field `roles`, "Roles must be any of: admin, rom-manager."). Changing roles rotates the security stamp (as a role change does today).
- **R6** The session cookie carries one role claim per role; policies `AdminOnly` (role `admin`) and `LibraryEditor` (role `rom-manager`).
- **R6a** `tests/RnRomManager.Api.Tests/Integration/EndpointRoleMatrixTests.cs` (the project's rule: every `/api` endpoint has a row) gains a `RomManager` expectation (rom-manager-only user passes, admin-only user gets 403) and test users with each role; every endpoint this spec adds or re-gates gets a row in the step that adds or changes it.
- **R7** SPA: `auth.isAdmin` = has `admin`; new `auth.canEditLibrary` = has `rom-manager`. Game page admin actions and image management show for `canEditLibrary`; Admin/Review/Ingestion/Integrity stay on `isAdmin`.

### Tables and pagination (shared)
- **T1** Standard table style, used by every table this spec touches: `table table-sm table-hover align-middle mb-0` inside `card` > `div.table-responsive`, header row `small text-uppercase text-body-secondary`, numbers/sizes right-aligned and `text-nowrap`, dates `text-nowrap`, long file names truncated with `text-truncate` + `title` attribute (max-width 32rem), row actions right-aligned as `btn-sm` icon buttons with `aria-label`s. Empty tables use `EmptyState`.
- **T2** A shared `Pagination.svelte` (Previous / "Page x of y" / Next, plus an optional page-size `<select>` 10/20/50/100). Hidden when there is one page and no selector.
- **T3** Page size persistence: `localStorage` key `rnrm.pageSize.<table>`; invalid or missing → 20. Read/write wrapped in try/catch (private mode).

### Ingestion page
- **G1** "Pending files": each system's table is paged client-side, 20 rows per page (T2 without selector).
- **G2** "Last runs": server-paged with `/api/jobs?type=Ingest&page=n&pageSize=20`.
- **G3** Both tables use T1.

### Review queue (`/review`)
- **V1** T1 table; System column shows a `SystemBadge` (system icon + display name, a fixed colour per system id, `text-bg-secondary` fallback).
- **V2** Page size selectable (T2 selector) and persisted (T3 key `review`), default 20.
- **V3** Each row has a Delete icon button opening the `ReviewDeleteModal` (D1).

### Review delete (API + modal)
- **D1** `ReviewDeleteModal` (shared by `/review` and `/review/{id}`) loads `GET /api/review/{id}` and lists every extracted field before confirming: file name, system, reason, size, CRC32/MD5/SHA1, source path, archive path + entry (if any), header fields (formatted as on the Review item page) and created time. For an archive entry it shows a warning: "This deletes the whole archive `<archivePath>`, including every other file in it." Confirm button `btn-danger` "Delete file"; disabled while loading or busy.
- **D2** `DELETE /api/review/{id}` (Admin). Takes the job lock (`409 JobRunning`). Only a `Pending` item (`404 NotFound` otherwise). Deletes the source from the ingest folder with `File.Delete` (no `_failed/` move): the loose file, or the whole archive when `archivePath` is set. Then deletes every `Pending` review row with the same source (same `sourcePath` for a loose file; same `archivePath` for an archive). Resolved/Dismissed rows are kept as history. Returns `204`.
- **D2a** `File.Delete` fails (locked, permission denied) → `500 DeleteFailed`, no rows are touched, the error is logged at Warning.
- **D3** Source already missing → the matching pending rows are deleted and `204` is returned (the goal is reached). The path is resolved with `SafePath` under the ingest root; a path outside it → `500` (as for dismiss today).
- **D4** Logged at Information: user id, review id, deleted path, number of rows removed.
- **D5** After a delete, `/review` reloads the current page (stepping back a page if it is now empty) and `/review/{id}` navigates to `/review` with a success toast.

### Review item page (`/review/{id}`)
- **W1** Layout: one header row with the file name (h1 h4, `text-truncate` + title), system badge, reason badge and actions on the right: Dismiss (existing) and Delete (D1, `btn-outline-danger`).
- **W2** Below, two columns at `lg`: left (col-lg-5) a card "File" (size, hashes with copy buttons, source/archive path, created) and a card "Header" (T1-styled two-column key/value table); right (col-lg-7) the resolution options as `nav-tabs`: for NoDatMatch Suggestions, Attach, Manual (Suggestions is the default when there are suggestions, else Manual); for AmbiguousGroup only Matched DAT entry. Each tab is the existing form unchanged in behaviour. Below `lg` the columns stack.
- **W3** Suggestions use a T1 table.

### Admin
- **A1** Users tab: T1 table showing username, roles (one badge each), status, last login, actions. "Create user" is a button above the table opening a `ConfirmModal size="lg"` with the existing form plus role checkboxes (`admin`, `rom-manager`, both unchecked by default); field errors from the API show inline; the modal closes and the table reloads on success. Editing roles in the table: the existing role control becomes two checkboxes per row; each change saves through `PATCH /api/users/{id}` with the full `roles` array.
- **A2** DATs, Settings, About: their tables use T1. Settings loses the "Download logs" button.
- **A3** New Logs tab (`/admin/logs`, Admin): "Download logs" button (existing `GET /api/admin/logs`) and a live view.
- **L1** In-memory log buffer: an NLog target keeps the last 500 rendered events (time UTC, level, logger, message, exception text) in a ring buffer with an increasing sequence number seeded from the process start time in Unix milliseconds × 1000, so ids keep increasing across restarts. It receives what the file target receives (same `minLevel`).
- **L2** `GET /api/admin/logs/stream` (Admin, `text/event-stream`): on connect sends buffered events with `id` > `Last-Event-ID`, or all buffered events when the header is absent, unparsable, or higher than the newest id; then every new event as it arrives (`id: <seq>`, `data: <json>`). A `: keep-alive` comment every 15 s; at each keep-alive the user is re-loaded and the stream ends if they are disabled, lost `admin`, or their security stamp changed. Ends when the client disconnects or the app stops. Response buffering disabled (`IHttpResponseBodyFeature.DisableBuffering`), `Cache-Control: no-cache`.
- **L3** Logs view: monospace, newest at the bottom, auto-scroll while scrolled to the bottom, keeps at most 2,000 lines in the browser, Pause/Resume (paused events are buffered client-side and appended on resume), a minimum-level filter (Trace…Fatal, default Info, client-side), level shown as a coloured badge. While `EventSource` is reconnecting a small "Reconnecting…" badge shows; only when `readyState === CLOSED` (e.g. 401/403) does `ErrorAlert` with Retry appear (Retry opens a new `EventSource`).

### Navigation and modals
- **N1** The navbar brand shows only the icon, with `aria-label="RnRomManager home"`.
- **M1** Every modal (`ConfirmModal`, `GlobalSearch`, and any new one, which must be built on `ConfirmModal`) uses `backdrop: 'static'` and `keyboard: true`: a background click does not close it; the close button, the header × and Escape do. `ConfirmModal` gets an info mode: when `onconfirm` is omitted the footer shows only "Close".

### Game page (`/games/{id}`)
- **P1** Next to "Download ZIP": one ROM → a "Download ROM" button; several → a Bootstrap split/dropdown button listing every ROM (primary first, then the Releases order) as "fileName — size". Each uses the existing `GET /api/games/{id}/roms/{romId}/download`, which already streams the entry out of the ZIP (no temp files).
- **P2** Releases table: T1 style; Region column shows one `badge text-bg-secondary` per region; the Header button opens a `ConfirmModal` (info mode, `size="lg"`) titled "Header — `<fileName>`" with the header key/value table, replacing the inline expanding row.
- **P3** The top image on the page is the cover (`isCover`); the separate top "snap" image is removed (screenshots are in the gallery). Image management (visible to everyone as a gallery; edit controls for `canEditLibrary`): an "Images" section shows every image as a thumbnail card with a type badge ("Box art" / "Screenshot" / "Title screen" / "Other"), a "Cover" badge (`text-bg-primary`) on the current cover, a "Placeholder" badge on the default image, size and dimensions; clicking a thumbnail opens it full size in an info-mode modal. Editors get: Upload (file input `accept="image/png,image/jpeg"` + type select "Box art"/"Screenshot"; client-side 8 MiB check), "Use as cover" on non-cover images, Delete (confirm modal naming the file; not shown on the default placeholder).

### Images (ZIP format v3)
- **I1** `info.json` schema version 3: each image gets `isCover` (bool, required); `source` may also be `upload`; image file names may end in `.png` or `.jpg`. Validation: exactly one `isCover` when there are images (zero images is valid and reported by integrity as `MissingBoxArt`, as today); at most 20 images; at most one `isDefault` image, and it must be a `BoxArt`; the existing name rules. The "at most one BoxArt / at most one Snap" rules are removed. The v2→v3 upgrader sets `isCover` on the BoxArt image if there is one, else on the first image (array order), else no image (a v2 file may have none).
- **I1a** Size limits: uploads, stored images and served images may be up to 8 MiB (`PngInfo.MaxBytes` becomes 8 MiB, plus a `JpegInfo` with the same limit); thumbnails fetched from libretro keep their 5 MiB limit (`MediaService` gets its own constant).
- **I2** Uploaded names: `boxart-NN.<ext>` / `snap-NN.<ext>` with the lowest free `NN` (01–99) for that prefix, case-insensitive over existing names. `<ext>` is `png` or `jpg` from the detected format (magic bytes), never from the client's file name.
- **I3** Auto-managed placeholder: uploading a `BoxArt` while the default placeholder exists removes the placeholder; the new image becomes the cover if the placeholder was the cover. Uploading a `Snap` never changes the cover. In every add path (upload, merge, ingest, media retry), the first image added to a game with no images becomes the cover. Deleting the cover makes the next cover the first remaining `BoxArt` (by file name), else the first remaining image, else the system's default placeholder is re-added as cover.
- **I4** Ingestion and media retry only ever replace the default placeholder: fetched box art replaces a placeholder (keeping its cover status) and is otherwise not added; a fetched snap is added only if the game has no `Snap`. Uploaded and fetched non-default images are never removed by them.
- **I4a** Move release to a new game: the new game's fetched box art (or placeholder) is its cover; a fetched snap is added as non-cover. Merge (`MergeAsync`): the target keeps its cover; the source's non-default images are copied into the target with names re-assigned per I2 (keeping their type and extension); the source's placeholder is dropped; if the target has a placeholder and the source brings a non-default `BoxArt`, the placeholder is removed (whether or not it was the cover), and if it was the cover the first such box art (by new file name) becomes the cover; a target with no images gets the first copied image as cover. If the result would exceed 20 images the merge is refused with `409 TooManyImages` and nothing changes.
- **I5** `POST /api/games/{id}/images` (`LibraryEditor`), `multipart/form-data` with `file` and `type` (`BoxArt`|`Snap`). The handler reads the form itself (`ctx.Request.ReadFormAsync`, no `IFormFile` binding, so ASP.NET's built-in antiforgery metadata isn't added; the group's existing `RequireAntiforgery` filter still checks `X-XSRF-TOKEN`). Request size limit 8 MiB + 64 KiB via `.WithMetadata(new RequestSizeLimitAttribute(...))` and matching `FormOptions.MultipartBodyLengthLimit` (Kestrel `413` beyond it). Errors: `400 ValidationFailed` for missing file, file over 8 MiB, not a PNG/JPEG by magic bytes, unreadable dimensions (fields `file`), or bad `type` (field `type`); `409 TooManyImages` at 20 images; `409 DuplicateImage` when an image with the same SHA1 exists in the game; `404 NotFound`; `409 JobRunning`; `409 InfoJsonNewerSchema`. Takes the job lock and rewrites the ZIP atomically like every edit. Returns the game detail.
- **I6** `DELETE /api/games/{id}/images/{imageId}` (`LibraryEditor`): applies I3; `409 DefaultImage` for the placeholder; returns the game detail.
- **I7** `POST /api/games/{id}/images/{imageId}/cover` (`LibraryEditor`): sets `isCover` on that image only; returns the game detail.
- **I8** Game list/detail and the image endpoint: the list's `boxArtUrl` is the cover image; detail images include `isCover`, `source`; `GET /api/games/{id}/images/{imageId}` serves `image/png` or `image/jpeg` by file extension (8 MiB limit, I1a). Integrity: `MissingBoxArt` = no cover image; `DefaultBoxArt` = the cover is the placeholder (codes unchanged).

### Search
- **S1** `q` words (existing `GameQuery.Words`, max 8) each must match at least one of `title`, `display_title`, `developers`, `publishers`, `genres` (case-insensitive `LIKE '%word%'`; JSON columns compare as `utf8mb4_bin`, so they are matched as `CAST(col AS CHAR) COLLATE utf8mb4_unicode_ci LIKE @w`).
- **S2** Relevance (`sort=relevance` with `q`): score = 100 if a title equals the phrase + 60 if a title starts with the phrase + per word: 40 title contains, 20 developers contains, 20 publishers contains, 10 genres contains. Order: score DESC, `title`, `id`. Other sorts unchanged; they just see the wider match set.
- **S3** List items include `matchedOn: { field: 'developer'|'publisher'|'genre', value: string } | null`, set only when `q` is present and no word matched the title: the first matching value in field order developer, publisher, genre (computed in C# from the row's lists). `GlobalSearch` shows it as one muted line ("Publisher: Nintendo"); the Library page ignores it.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Data | `src/RnRomManager.Data/Migrations/0011_user_roles.sql` | `user_roles (user_id INT FK→users ON DELETE CASCADE, role VARCHAR(32), PK(user_id, role))`; backfill (R3); rotate `security_stamp` for all users; drop `users.role` |
| Data | `src/RnRomManager.Data/Migrations/0012_image_cover.sql` | `images.is_cover TINYINT(1) NOT NULL DEFAULT 0`; backfill `is_cover = 1` for `type = 'BoxArt'` rows (at most one per game today), then for games still without a cover, on their `MIN(id)` image (matches the v2→v3 upgrader's "first image") |
| Data | `Repositories/UserRepository.cs`, `Repositories/LibraryRepositories.cs` | roles read/write (one transaction with the user row); mirror `is_cover`; list query uses the cover image; search per S1–S2, returns developers/publishers/genres for S3 |
| Core | `Users/UserRole.cs` → `Users/Roles.cs` | string constants `admin`, `rom-manager`, `All`; `User.Roles` (`IReadOnlySet<string>`) replaces `User.Role` |
| Core | `Users/UserAdminService.cs`, `Users/AdminSeeder.cs`, `Users/AccountService.cs` | roles instead of role; last-admin rule on `admin` |
| Core | `Library/InfoJson*.cs`, `Library/InfoJsonValidator.cs`, `Library/ImageType.cs`, new `Library/JpegInfo.cs`, new `Library/ImageFormat.cs` | schema v3, `isCover`, `upload` source, JPEG SOF dimension reader, format detection |
| Core | `Ingest/IngestPipeline.cs`, `Library/GameMediaRetry.cs`, `Library/LibraryEditor.cs`, `Integrity/IntegrityJob.cs` | I3/I4 rules; new `AddImageAsync`, `DeleteImageAsync`, `SetCoverAsync` on `LibraryEditor` |
| Core | `Library/PngInfo.cs`, `Media/MediaService.cs` | 8 MiB stored/served limit; fetch keeps its own 5 MiB constant (I1a) |
| Core | `Library/LibraryRows.cs`, `Library/LibraryEditor.cs` (`MergeAsync`, move-to-new-game) | `is_cover` in `ImageRow`/`ToRows`; `HasDefaultBoxArt` = the cover is the placeholder; I4a |
| Core | `Ingest/ReviewResolver.cs`, `Ingest/IngestOutcome.cs` (holds `IIngestReviewRepository`) | `DeleteSourceAsync` (D2–D4); repository method deleting pending rows by source |
| Data | `Repositories/LibraryRepositories.cs` `ListMediaRetryCandidatesAsync` | keeps using `has_default_boxart` (now "cover is the placeholder") |
| Tests | `tests/RnRomManager.Api.Tests/Integration/EndpointRoleMatrixTests.cs`, `SchemaMigrationTests`, `AuthTests.cs`, `tests/RnRomManager.TestData` (`TestUsers`, Basic seed set) | role matrix (R6a), migration count/columns, roles contract, rom-manager test user, a second review file in Basic (step 20) |
| Api | `Auth/AuthSetup.cs`, `Auth/Session.cs`, `Cli/ResetPasswordCommand.cs` | role claims, `LibraryEditor` policy, CLI output lists roles |
| Api | `Endpoints/UserEndpoints.cs`, `Endpoints/AuthEndpoints.cs` | `roles` contract (R5) |
| Api | `Endpoints/LibraryEndpoints.cs` | policies (R2), image endpoints (I5–I8), `matchedOn` (S3) |
| Api | `Endpoints/ReviewEndpoints.cs` | `DELETE /api/review/{id}` |
| Api | `Endpoints/AdminEndpoints.cs`, new `Logging/MemoryLogTarget.cs`, `nlog.config`, `Logging/NLogSetup.cs` | ring-buffer target + SSE stream (L1–L2) |
| Web | `lib/api/types.ts`, `lib/api/client.ts`, `lib/stores/auth.svelte.ts` | `roles`, `canEditLibrary`, `FormData` bodies (no JSON content type), new DTO fields |
| Web | new `lib/components/Pagination.svelte`, `SystemBadge.svelte`, `ReviewDeleteModal.svelte`, `GameImages.svelte`, `lib/pageSize.ts` | shared pieces (T2, T3, V1, D1, P3) |
| Web | `lib/components/ConfirmModal.svelte`, `GlobalSearch.svelte`, `Navbar.svelte` | M1 info mode + static backdrop, S3 hint, N1 |
| Web | `routes/ingestion`, `routes/review`, `routes/review/[id]`, `routes/admin/{users,dats,settings,about}`, new `routes/admin/logs`, `routes/admin/+layout.svelte`, `routes/games/[id]` | pages per G, V, W, A, L, P |
| Tests | `tests/RnRomManager.*.Tests`, `src/RnRomManager.Web/**/*.test.ts`, `tests/e2e/specs/mvp.spec.ts` | per step |
| Docs | `docs/zip-format.md`, `docs/features.md`, `docs/development.md`, `docs/deployment.md`, `README.md` (if it lists roles) | v3 format, roles, image management, review delete, logs tab, search, T1/M1 conventions |

**API contract changes (breaking for any external client):** `role` → `roles` on `/api/auth/me` and `/api/users*`; `boxArtUrl` now points at the cover (may be a screenshot or JPEG). **Data:** `users.role` dropped after backfill; info.json files are rewritten as v3 on their next normal write (app versions before this one treat v3 as `InfoJsonNewerSchema`, read-only). **Config:** none.

## Risks
- **Everyone is signed out** on upgrade (R3). Intentional; mention in the release notes / PR body.
- **Older app versions** see v3 ZIPs as read-only. Same as the v1→v2 bump; documented in zip-format.md's version table.
- **No DB downgrade**: migration 0011 drops `users.role`, so an older app can't start on the upgraded database; downgrading needs a database restore. Stated in deployment.md and the PR body.
- **Hard delete is irreversible** and can remove a whole archive. Mitigated by D1's full listing and archive warning; Admin only; logged (D4).
- **Search uses `LIKE '%…%'`** across five columns (full scan). Fine for tens of thousands of games; noted as a `shortcut:` comment with "move to FULLTEXT if search gets slow". JSON-escaped characters (`"`, `\`) inside list values may not match literally — acceptable.
- **SSE and proxies**: buffering proxies can delay events; the keep-alive and `DisableBuffering` cover Kestrel; deployment docs note `proxy_buffering off` for nginx.
- **Log content** is visible to Admins only — same exposure as the existing log download. Settings are already redacted; the stream adds nothing new.
- **Uploads** are untrusted: format is detected from magic bytes, dimensions must parse, size is capped before buffering, names are generated server-side, images are served with `X-Content-Type-Options: nosniff` (existing security headers).

## Steps
- [x] **1. Roles: data and core**: migration 0011 (R3); `Roles` constants; `User.Roles`; `UserRepository` roles read/write in one transaction; `UserAdminService` (create/update with roles, R4 last-admin on `admin`, R5 validation); `AdminSeeder` and `ResetPasswordCommand` give both roles; `Session` writes one role claim per role; `AuthSetup` `AdminOnly` → `admin`, new `LibraryEditor` → `rom-manager` (not yet applied); `/api/auth/me` and `/api/users*` use `roles` (R5); `TestUsers` gets roles. SPA contract follow-through so the app stays usable: `types.ts`, `auth.isAdmin`, `Navbar.svelte` admin check, the Users page sends/shows `roles` (a minimal multi-checkbox in place of the role select; styling and modal come in step 4), and every vitest fixture using `role`. Files: as in Changes (Roles rows). Tests: Data integration — `SchemaMigrationTests` (script count, users columns), migration backfill (Admin→both, User→none, stamps changed, column gone) and roles round-trip; Core — `UserAdminService` role validation, `roles` missing → `[]`, last-admin rule with roles, stamp rotation on role change; Api integration — `AuthTests` `/me` returns `roles`, create/patch with `roles`, invalid role → 400 `roles`; `EndpointRoleMatrixTests` still green with Admin = `admin`; vitest `admin-pages.test.ts` posts `roles`. Done when: listed tests pass; `grep -rn "UserRole\|\.role\b\|role:" src tests --include=*.cs --include=*.ts --include=*.svelte --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj` finds no use of the old user-role contract (ARIA `role` attributes are fine).
- [x] **2. rom-manager gates library edits**: apply `LibraryEditor` to the R2 endpoints; `auth.canEditLibrary`; Game page admin actions/`GameAdminModal` gated on it; `EndpointRoleMatrixTests` gets the `RomManager` expectation and a rom-manager-only test user (R6a). Tests: the role matrix rows for all six R2 endpoints (rom-manager passes, admin-only 403, user 403); vitest `game-admin.test.ts` shows actions for a rom-manager and hides them for an admin-only user. Done when: the role matrix passes with the new rows.
- [x] **3. Shared UI pieces and modal rule**: `Pagination.svelte` (T2), `lib/pageSize.ts` (T3), `SystemBadge.svelte` (V1), `ConfirmModal` info mode (M1), `GlobalSearch` static backdrop (M1), navbar icon-only brand (N1), T1 written up in `docs/development.md`. Tests: vitest for Pagination (page math, selector emits size, hidden on one page), pageSize (default 20, invalid → 20, storage throwing), ConfirmModal info mode (no confirm button), GlobalSearch/ConfirmModal construct `Modal` with `backdrop: 'static'`, Navbar brand has the aria-label and no text. Done when: tests pass; `npm run lint/check/test` green.
- [x] **4. Users tab**: create-user modal with role checkboxes, roles badges + per-row role editing, T1 table (A1). Tests: vitest in `admin-pages.test.ts` — modal opens, posts `{username,password,roles}`, shows a field error, reloads on success; role checkbox PATCHes `roles`. Done when: A1 met.
- [x] **5. Admin DATs/Settings/About restyle**: T1 tables (A2; the Settings Download logs button stays until step 7). Tests: vitest — the three tables render with the T1 classes. Done when: tests pass.
- [x] **6. Live log buffer and stream (API)**: `MemoryLogTarget` (L1) registered in `nlog.config`/`NLogSetup`; `GET /api/admin/logs/stream` (L2); role-matrix row (Admin). Tests: unit — ring buffer keeps the last 500 in order with increasing ids, ids seeded from start time, `Since(id)` returns only newer, `Since(id > newest)` returns all; Api integration — stream returns buffered events as SSE, honours `Last-Event-ID`, ends after the user is disabled (keep-alive interval injectable for the test). Done when: those tests and the role matrix pass.
- [x] **7. Logs tab (SPA)**: `/admin/logs` route + tab in `admin/+layout.svelte`; download button moved here from Settings (A2, A3); live view per L3. Tests: vitest with a fake `EventSource` — lines appended, level filter, pause buffers then appends, 2,000-line cap, "Reconnecting…" on error with OPEN/CONNECTING state, ErrorAlert only when CLOSED; Settings has no Download logs button. Done when: tests pass.
- [x] **8. Review delete (API)**: `ReviewResolver.DeleteSourceAsync` + repository support + `DELETE /api/review/{id}` (D2–D4); role-matrix row (Admin). Tests: Core — loose file deleted + row removed; archive deleted + all pending rows of that archive removed, resolved rows kept; missing source → rows removed, success; delete failure (read-only/locked temp file) → DeleteFailed and rows kept; non-pending → NotFound; lock held → JobRunning; Api integration — 204/404/409. Done when: tests and role matrix pass.
- [x] **9. Review queue page**: T1 table, SystemBadge, persisted page size (default 20), `ReviewDeleteModal` (D1) and delete per row (V1–V3, D5). Tests: vitest `review-page.test.ts` — page size read/written to storage and sent as `pageSize`; delete modal lists the fields and archive warning; confirm calls DELETE and reloads. Done when: V1–V3, D1, D5 met.
- [x] **10. Review item page rework**: layout W1–W3 with Delete next to Dismiss. Tests: vitest — tabs switch; Suggestions is the default tab when suggestions exist; resolve (Dat/Attach/Manual) and dismiss post the same bodies as before; Delete opens the modal and navigates to `/review` after success. Done when: those tests pass.
- [ ] **11. Ingestion page**: G1–G3. Tests: vitest `ingestion-page.test.ts` — 45 pending files show 20/20/5 across pages; Last runs requests `page=2&pageSize=20`. Done when: G1–G3 met.
- [ ] **12. Search (data + API)**: S1–S2 in `GameRepository.ListAsync`; `matchedOn` (S3) in the list DTO. Tests: Data integration — word matching each field, case-insensitive publisher match (`nintendo` finds `Nintendo`), every-word-must-match, score order (title exact > prefix > title word > developer/publisher > genre), non-relevance sorts unchanged; Api — `matchedOn` null on title match, set for publisher match, absent without `q`. Done when: S1–S3 (API) met.
- [ ] **13. Global search hint**: S3 line in `GlobalSearch`. Tests: vitest — hint shown for a non-title match, not for a title match. Done when: S3 met.
- [ ] **14. info.json v3 (core)**: model `isCover`, `upload` source, validator (I1), v2→v3 upgrader, `ImageFormat` + `JpegInfo`, size limits (I1a), ingestion/media retry/move-to-new-game/merge/integrity per I4, I4a, I8, `LibraryRows` carries `isCover`, `docs/zip-format.md` v3 row. Tests: Core — upgrader (with BoxArt, without BoxArt but with a Snap, with no images), each validator rule, merge (images renamed, target cover kept, placeholder removed when non-default box art arrives whether or not it was the cover, empty target gets a cover, >20 → TooManyImages), move to new game (cover set), JPEG dimension reader (baseline + progressive SOF, truncated → null), format detection, I4 cases (placeholder replaced keeps cover; uploaded never removed; snap only when none), integrity codes. Done when: all Core tests pass and existing ZIP fixtures upgrade cleanly.
- [ ] **15. Image mirror (data + read API)**: migration 0012; mirror `is_cover`; list uses cover; detail `isCover`/`source`; image endpoint content type by extension (I8). Tests: Data integration — backfill and mirror; Api integration — JPEG image served as `image/jpeg`, `boxArtUrl` follows the cover. Done when: I8 met.
- [ ] **16. Image edits (core)**: `LibraryEditor.AddImageAsync/DeleteImageAsync/SetCoverAsync` with I2, I3, limits and error codes. Tests: Core with temp dirs — naming (lowest free NN, png/jpg), placeholder removal on BoxArt upload, first upload into an empty game becomes cover, cover fallback chain on delete incl. re-adding the placeholder, DefaultImage, TooManyImages, DuplicateImage, JobRunning, ZIP re-verified after write. Done when: I2–I3 met in core.
- [ ] **17. Image endpoints**: I5–I7 with size limit and validation; `client.ts` FormData support; role-matrix rows (RomManager) for the three endpoints. Tests: Api integration — upload without `X-XSRF-TOKEN` → 400 as other POSTs, upload PNG and JPEG, oversize → 413 (confirms the size-limit metadata works on minimal APIs; if not, check `Content-Length` in the handler and say so in Decisions), wrong magic → 400 `file`, bad type → 400 `type`, 403 for a user without `rom-manager`, delete, set cover; vitest client sends FormData without JSON content type but with the XSRF header. Done when: I5–I7 met.
- [ ] **18. Game page: images**: `GameImages.svelte` and the cover-based top image (P3). Tests: vitest — top image is the cover even when it's a Snap, gallery for all users, edit controls only with `canEditLibrary`, upload posts FormData, 8 MiB client check, set cover and delete call the API and refresh. Done when: P3 met.
- [ ] **19. Game page: ROM download and Releases**: P1, P2. Tests: vitest `game-page.test.ts` — one ROM → button, several → dropdown with sizes in primary-first order, region badges, Header opens the modal. Done when: P1–P2 met.
- [ ] **20. e2e and docs**: update `mvp.spec.ts` for the role contract, create-user modal, header modal and Logs tab visit; add a second unmatched file to the `Basic` seed set (`tests/RnRomManager.TestData`) so ingest gives "Review 2", updating every hard-coded count: the e2e run assertion, `SeedIngestTests` (Review → 2), `IngestSeederTests` (Basic file count), any Full-set expectations it changes, and the development.md table; the resolve flow uses one item and a new review-delete flow deletes the other; add an image upload (PNG fixture generated in the test)/set cover/delete flow; axe scans on the new Logs tab and modals. Docs: `features.md` (roles, review delete, images, logs, search), `development.md` (components list), `deployment.md` (SSE proxy note, sign-out on upgrade), README if it mentions roles. Done when: `scripts/test-all.ps1` and `scripts/test-e2e.ps1` pass.

## Decisions
- 2026-11-06 Review delete of an archive entry → deletes the whole archive and all its pending review rows (user)
- 2026-11-06 Which image can be the cover → any image; new `isCover` in info.json v3 (user)
- 2026-11-06 Upload formats → PNG + JPEG, 8 MiB, stored as-is (user)
- 2026-11-06 Image permissions → new role system: `admin` + `rom-manager`; rom-manager may manage images (user)
- 2026-11-06 Role model → set of roles in `user_roles`, replacing `users.role`; Admin → both roles (user)
- 2026-11-06 rom-manager scope → all library edits (metadata, retry media, move, merge, delete release/game, images) (user)
- 2026-11-06 Live logs → SSE from an in-memory 500-event NLog buffer (user)
- 2026-11-06 Search ranking → weighted LIKE score in SQL, no migration (user)
- 2026-11-06 Placeholder handling → auto-managed (I3) (user)
- 2026-11-06 Global search shows a "matched on" hint (user)
- 2026-11-06 Library page search uses the same wider matching (user)
- 2026-11-06 Review stays `admin`-only; the media-retry job stays `admin` (code: not part of "library edits")
- 2026-11-06 ROM download reuses the existing streaming endpoint; no temp unpacking (code)
- 2026-11-06 Page-size persistence is per browser in localStorage (default)
- 2026-11-06 Global search gets a static backdrop like every modal (M1 applies to all modals) (default)

- 2026-11-06 v2 files without box art → upgrader makes the first image the cover; zero images stay valid (MissingBoxArt) (reviewer R1)
- 2026-11-06 Merge/move image rules → I4a; merge over 20 images is refused (reviewer R2)
- 2026-11-06 Stored/served image limit raised to 8 MiB, libretro fetch stays 5 MiB (reviewer R3)
- 2026-11-06 Upload endpoint reads the form manually to keep the app's own antiforgery filter (reviewer R4)
- 2026-11-06 Role matrix test gains a RomManager expectation; step 1 carries the SPA contract change (reviewer R5, R6)
- 2026-11-06 JSON columns matched with a unicode_ci collation (reviewer R7)
- 2026-11-06 Log ids seeded from start time; unknown/higher Last-Event-ID resends the buffer; stream re-checks the user every keep-alive (reviewer R8, R12)
- 2026-11-06 e2e: Basic seed gets a second review file (reviewer R9)
- 2026-11-06 Game page top image = the cover; type badge "Box art" vs "Cover" badge (reviewer R10)
- 2026-11-06 Delete failure → 500 DeleteFailed, rows kept (reviewer R11)
- 2026-11-06 Review item tabs: a NoDatMatch item without suggestions opens on Manual; AmbiguousGroup items keep only the Matched DAT entry tab (user, step 10)

## Kickoff prompt
```text
/spec implement 2026-11-06-ui-polish-images-roles.md
```
Starts the spec, or resumes it at the first unticked step.
