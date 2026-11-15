---
title: Bundle Creator for ROM files
status: in-progress
created: 2026-11-15
---

# Bundle Creator for ROM files

## Summary

Signed-in users can collect specific ROM files across games and systems into a cart, save named queues to their account, and send the files to the browser's Downloads flow one at a time, in order, with per-file status. Split out of the earlier combined "Library curation and ROM bundles" spec.

## Out of scope

- New ROM sources, and changes to emulator play or ingestion.
- Shared queues between accounts.
- Automatically adding a disc set's companion files (cue, other tracks) with its main file.
- A combined archive of the cart.
- Account deletion (the app has none today).

## Behaviour

- **B1.** Add an accessible cart link with the working-cart count to the authenticated top navigation on desktop and mobile, hidden behind the same `mustChangePassword` gate as Search. Add-to-cart controls sit on the game detail page's existing download surfaces: the main file of each release and each separately downloadable set file. Users combine files across games, systems, publishers and collections by opening each game's detail page. Cart entries identify and display individual files. Insertion order is kept, duplicate file identities are rejected, and the user can remove one file or clear the cart. A different file with the same display filename is allowed.
- **B2.** The working cart lives in this browser's `localStorage`, keyed by the signed-in user id, and is restored on refresh. Sign-out or an account change clears in-memory state, and stale asynchronous results are ignored. Named snapshots are saved to the account on the server:
  - At most 100 queues per account and 500 files per queue or cart.
  - Names are trimmed, 1–100 characters, and unique per account under case-insensitive, accent-sensitive comparison, the same rule as collection names (`utf8mb4_uca1400_as_ci`).
  - Saving over an existing queue needs an explicit overwrite action and the latest revision.
  - Loading replaces the working cart, after confirmation when the cart has files and differs from the queue.
  - Deleting a saved queue needs confirmation and leaves the working cart intact.
  - Other accounts can't list, read, change or delete a queue, including by guessed ID.
  - Queues reference `users` with `ON DELETE CASCADE`, like collections.
- **B3.** A cart or queue entry is an ordered stable identity plus last-known labels:
  - The game by its GUID (nonzero); the release by its `roms.sha1` (globally unique); a set file by its SHA-1 plus stored `rom_files.file_name` within that release. A main-file entry has `member: null`. Main and set-file entries are distinct.
  - SHA-1 values are lowercase 40-character hexadecimal. Set-file names are compared exactly and ordinally.
  - Resolve to the current numeric release and file IDs through the live library, so a mirror rewrite or DB rebuild resolves the same file again. An entry that can't be resolved uniquely (the release moved to another game, was deleted, or the set file was renamed or is ambiguous) stays visible and removable as unavailable. Never substitute another release or game.
  - Create and replace apply the same structural validation, and both accept well-formed unresolved entries so users can resave queues after a library change.
  - A game-page add uses the currently rendered file metadata.
  - Labels and filenames are capped at 255 Unicode code points (as `VARCHAR(255)` counts them; C# counts runes, the SPA counts `[...s].length`). Labels render as text. Attachments use the current library's canonical filenames.
- **B4.** Queue API, all caller-scoped:
  - `GET /api/bundle-queues` lists summaries `{ id, name, revision, itemCount }`.
  - `POST /api/bundle-queues` with `{ name, entries }` creates and returns the snapshot with 201.
  - `GET /api/bundle-queues/{id}` returns the snapshot `{ id, name, revision, entries }`.
  - `PUT /api/bundle-queues/{id}` with `{ name, entries, revision }` atomically replaces the contents and returns the updated snapshot.
  - `DELETE /api/bundle-queues/{id}?revision=<n>` deletes with 204.
  - Entries are `{ gameId, releaseSha1, member: null | { sha1, fileName }, gameTitle, fileName }`.
  - Queue IDs are application-generated GUIDs with GUID route constraints. Revision starts at 1 and increments on each successful replace.
  - Creates for the same owner are serialized by locking the owning `users` row (`SELECT … FOR UPDATE`) before checking and inserting. Name conflicts are checked before the account cap.
  - For replace and delete: resolve ownership first, then check revision, then name conflict, using conditional mutations.
  - Errors: 400 for malformed names or identities, duplicates or size limits; 409 `BundleQueueChanged` for a stale revision; 409 `BundleQueueNameTaken`; 409 `BundleQueueLimit` at the account cap; 404 for missing or foreign queue IDs; 413 when a snapshot or batch-resolution body exceeds 2 MiB. Use caller-derived identity, existing API auth and existing request hardening.
- **B5.** Authenticated, state-free `POST /api/bundle-files/resolve` takes `{ items: [{ gameId, releaseSha1, member }] }` (up to 500) and returns `{ items: [{ available, gameTitle, fileName, downloadUrl }] }` in input order. The last three fields are null when unavailable. Available items carry current names and the normal same-origin single-file download URL. Use a bounded batched lookup and the existing antiforgery client. The SPA refreshes on opening or loading a cart, after a download failure, and on manual refresh. This reports catalog availability only; opening the file can still fail.
- **B6.** The Bundle Creator page has **Download queue**. It takes an immutable snapshot of the working cart for the run and sends one attachment at a time, in cart order, through the browser's Downloads flow using authenticated same-origin links. The server uses the same open/check logic, filename and streaming as the existing ROM download routes, never reading a whole file into browser or API memory. The next file starts only after the server has finished sending the previous body or the previous file reaches an explicit failure. Browser permission prompts may gate multiple downloads. Show visible progress, named failures, **Stop after current file**, and manual download links for remaining downloadable entries. The browser owns final file writes and same-name handling; UI status describes files sent by the server and makes no claim about files saved to disk.
- **B7.** Each current item uses a short-lived, random, caller-owned transfer ticket:
  - `POST /api/bundle-downloads` takes `{ gameId, releaseSha1, member }`, resolves it and returns 201 `{ ticket, fileUrl }`; 404 when unavailable; 409 `BundleDownloadBusy` while a prior ticket for that user is pending or sending; 503 when 100 tickets are pending or active globally.
  - `GET /api/bundle-downloads/{ticket}/file` claims the pending ticket once, atomically, rechecks resolution when opening, and sends a full response (no ranges) through the shared opener and its disposal rules. 404 for unknown or foreign tickets, 409 for a consumed ticket.
  - `GET /api/bundle-downloads/{ticket}` returns `{ state, errorCode }`, where `state` is `pending`, `sending`, `sent`, `failed` or `expired` and `errorCode` is null except for `failed`. Status responses and ticket URLs are `Cache-Control: private, no-store`.
  - Mark `sent` only after writing the expected length and completing the response, checking cancellation and exceptions. A cancelled or disconnected stream is never marked sent just because an `OnCompleted` callback fired. Abort or I/O error marks `failed`.
  - A ticket that never starts expires after 30 seconds and releases the user's slot; the client pauses with instructions to allow automatic downloads and retry. A two-minute read/write inactivity timeout cancels a stalled stream. Abort the response and dispose the stream before releasing the slot.
  - Terminal status is kept for up to five minutes and up to 2,000 tickets, evicting the oldest terminal records first. App restart clears tickets and the user retries remaining files.
  - The client polls every 500 ms while the page owns the run. Ordinary unavailable or open failures are recorded and skipped. A lost status response or 401 pauses with a retry or sign-in action. Leaving the page or signing out stops scheduling more files; a native transfer already started may finish on its own.
- **B8.** Transfer results are tied to the run's snapshot. After a status-fetch failure, polling resumes on the same ticket. Once a terminal status is known, retry reissues failed or expired items in original order and continues pending ones, skipping files already marked sent. If the server has lost the current ticket, that item shows "Transfer status unknown" and the user chooses whether to retry it. A fresh **Download queue** after the run ends takes a new snapshot. While the page is open, results survive temporary network loss. Closing or reloading the page ends the run and keeps the cart.

## Changes

| Area | File(s) | Change |
|---|---|---|
| Data | `src/RnRomManager.Data/Migrations/00NN_bundle_queues.sql` (next free number) | New `bundle_queues` table, below. No existing data changes. |
| Data | `src/RnRomManager.Data/Repositories/BundleQueueRepository.cs` (new), `LibraryRepositories.cs` | Owned queue storage; batched identity resolution in the ROM repository. |
| Core | `src/RnRomManager.Core/Bundles/` (new): `BundleQueue.cs`, `BundleFile.cs`, `BundleQueueValidation.cs`, `BundleDownloadTickets.cs` | Contracts, identity validation and resolution, limits, ticket lifecycle. |
| Api | `src/RnRomManager.Api/Endpoints/LibraryDownload.cs` (new), `LibraryEndpoints.cs` | Extract the shared attachment opener from the existing ROM and set-file routes. |
| Api | `src/RnRomManager.Api/Endpoints/BundleQueueEndpoints.cs`, `BundleFileEndpoints.cs`, `BundleDownloadEndpoints.cs` (new), `AppHost.cs` | Authenticated routes with existing ProblemDetails conventions. |
| Web | `src/RnRomManager.Web/src/lib/stores/bundle.svelte.ts`, `src/lib/bundles.ts`, `src/lib/bundleDownload.ts`, `src/lib/api/types.ts` | Cart store, queue client, download runner. |
| Web | `src/RnRomManager.Web/src/routes/bundle-creator/+page.svelte`, `src/lib/components/SavedBundleQueues.svelte`, `BundleDownload.svelte` | Bundle Creator page. |
| Web | `src/RnRomManager.Web/src/routes/+layout.svelte`, `src/lib/components/Navbar.svelte`, `src/lib/components/icons.ts`, `src/routes/games/[id]/+page.svelte` | Cart link and add controls. |
| Docs | `docs/features.md`, `docs/development.md` | Behaviour, browser download permissions, queue ownership, ticket lifecycle. |

`bundle_queues`: `id CHAR(36)` primary key; `user_id INT NOT NULL` with FK to `users` `ON DELETE CASCADE`; `name VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci NOT NULL`; `revision INT NOT NULL`; `item_count INT NOT NULL`; `entries MEDIUMTEXT NOT NULL` (the validated entries re-serialized with relaxed escaping, like `Hotkeys`' stored JSON, so non-ASCII text isn't expanded to `\uXXXX`; MEDIUMTEXT holds the 2 MiB request cap with room to spare); `created_at`, `updated_at DATETIME(6) NOT NULL`; `UNIQUE (user_id, name)`. No new configuration or storage mount.

## Risks

- Numeric release and file IDs change on every mirror rewrite. Entries store stable identities and resolve at use time; ambiguity is unavailable, never a substitute.
- A browser or proxy can block or buffer downloads. Chrome asks for site-level permission for automatic downloads ([Chrome Help](https://support.google.com/chrome/answer/95759)). Progress waits on server completion, pending-ticket expiry and interrupted streams are shown, and manual links give a way forward.
- Ticket state is in memory, which suits the app's single instance; restart loses it and the UI says so.

## Steps

Every step adding a route also updates `tests/RnRomManager.Api.Tests/Integration/EndpointRoleMatrixTests.cs` for the signed-in User role and uses the normal signed-in API and antiforgery group.

- [x] **1. Store owned queue snapshots.** Files: `Data/Migrations/0022_bundle_queues.sql`, `Data/Repositories/BundleQueueRepository.cs`, `Core/Bundles/BundleQueue.cs`. Tests: `BundleQueueRepositoryTests` (Data Testcontainers) round-trip, order, ownership, case-insensitive and accent-sensitive unique names, concurrent create at the limit, overwrite and delete with stale revision. Done when: concurrent stale writes can't replace a newer snapshot.
- [x] **2. Resolve durable ROM identities.** Files: `Core/Bundles/BundleFile.cs`, `Data/Repositories/LibraryRepositories.cs`, the ROM repository contract. Tests: `BundleFileTests` (NUnit) validation; `BundleFileResolutionTests` (Data Testcontainers) main and set file across numeric ID changes, moved release, ambiguity, deleted, same-name files, exact member comparison for case and accent variants. Done when: each identity resolves to the current download target or unavailable.
- [x] **3. Validate queue writes and expose create/read.** Files: `Core/Bundles/BundleQueueValidation.cs`, `Api/Endpoints/BundleQueueEndpoints.cs`, `Api/AppHost.cs`. Tests: `BundleQueueValidationTests` (NUnit) input validation and code-point limits; `BundleQueueEndpointsTests` (API Testcontainers) auth isolation, bounded create/list/load, name and limit conflicts, 413. Done when: a caller can create and restore an ordered personal snapshot.
- [x] **4. Expose replace/delete.** Files: `Api/Endpoints/BundleQueueEndpoints.cs`. Tests: `BundleQueueEndpointsTests` atomic overwrite, unresolved identities kept, cross-user denial, conditional delete. Done when: every B4 mutation returns its specified status.
- [x] **5. Expose batch file resolution.** Files: `Api/Endpoints/BundleFileEndpoints.cs`, `Api/AppHost.cs`. Tests: `BundleFileEndpointsTests` (API Testcontainers) ordered mixed availability, stable identity after a mirror rewrite, bounds. Done when: the cart can show unavailable entries and get current download links.
- [x] **6. Extract common attachment opening.** Files: `Api/Endpoints/LibraryEndpoints.cs`, `Api/Endpoints/LibraryDownload.cs`. Tests: existing `LibraryEndpointsTests` and `LibraryEndpointsTests.Sets` embedded, external and set downloads, plus a regression check for attachment names and ranges. Done when: the normal routes keep their behaviour and the shared opener returns an owned stream with its expected length.
- [x] **7. Track download ticket lifecycle.** Files: `Core/Bundles/BundleDownloadTickets.cs`. Tests: `BundleDownloadTicketsTests` (NUnit, fake clock) single claim, concurrent users, global bound, pending expiry, status retention and eviction. Done when: each ticket has one owner, one consumer and a bounded lifetime.
- [x] **8. Implement ticket issue and status handlers.** Files: `Api/Endpoints/BundleDownloadEndpoints.cs`. Tests: `BundleDownloadHandlerTests` (NUnit/NSubstitute) caller isolation, unavailable, busy, 503 and expiry. Done when: the handlers return the issue and poll contracts. Routes are mapped in step 9.
- [x] **9. Stream attachments and publish the ticket routes.** Files: `Api/Endpoints/BundleDownloadEndpoints.cs`, `Api/Endpoints/LibraryDownload.cs`, `Api/AppHost.cs`. Tests: `BundleDownloadEndpointsTests` (API integration) issue/status/file auth and isolation, full and aborted streams, duplicate claims, file gone after issue, completion gating; `BundleDownloadStreamTests` (NUnit, fake clock) the measured copy and stalled read/write cancellation. Done when: the stream uses the shared opener and a ticket releases its slot after any termination.
- [x] **10. Keep the account-scoped working cart.** Files: `Web/src/lib/stores/bundle.svelte.ts`, `Web/src/lib/api/types.ts`. Tests: `bundle.svelte.test.ts` deduplication, order, cap, unavailable labels, account switch and restore. Done when: a signed-in account can reload the browser and keep its cart.
- [ ] **11. Add cart entry points.** Files: `Web/src/routes/+layout.svelte`, `Web/src/lib/components/Navbar.svelte`, `Navbar.test.ts`, `Web/src/routes/games/[id]/+page.svelte`, `Web/src/lib/components/icons.ts`, `Web/src/routes/bundle-creator/+page.svelte`. Tests: `Navbar.test.ts` count, password-change gating, keyboard and mobile access; game page tests for adding a main file and a set file; Bundle Creator remove and clear; Playwright adds from two games (run in Finish). Done when: each entry point feeds a visible working cart.
- [ ] **12. Manage saved queues in the UI.** Files: `Web/src/lib/bundles.ts`, `Web/src/lib/components/SavedBundleQueues.svelte`, `Web/src/routes/bundle-creator/+page.svelte`. Tests: `SavedBundleQueues.test.ts` load and overwrite confirmation, stale conflict, save and delete. Done when: B2 works across browser sessions for the same account.
- [ ] **13. Run sequential native downloads.** Files: `Web/src/lib/bundleDownload.ts`, `Web/src/lib/components/BundleDownload.svelte`, `Web/src/routes/bundle-creator/+page.svelte`. Tests: `bundleDownload.test.ts` fake polling and click dispatch for order, blocked downloads, failures, retries skipping sent files, lost ticket, stop and sign-out; Playwright native download events for two cross-system files and fallback/retry (run in Finish). Done when: B6 advances only after a terminal server state.
- [ ] **14. Document.** Files: `docs/features.md`, `docs/development.md`. Done when: the cart, saved queues, browser download permissions and ticket lifecycle are documented and the full repository and Playwright suites pass.

## Decisions

- 2026-11-15 How are bundles delivered? → the browser's Downloads flow, including automatic-download prompts (user).
- 2026-11-15 Where do queues live? → named queues are account-owned server snapshots; the working cart is browser-local per account (default).
- 2026-11-15 When does a run advance? → after server-side completion, with per-file results kept while the page stays open (design). The simpler alternative, fixed-delay sequential clicks with manual links, would drop steps 7–9 but lose reliable per-file status.
- 2026-11-15 Queue name uniqueness? → same collation rule as collection names (reviewer).
- 2026-11-15 Split from the combined curation/bundles spec to keep each branch reviewable (reviewer).

- 2026-11-15 Where are stalled streams tested? → in `BundleDownloadStreamTests`, a fake-clock unit test of the copy loop, since a two-minute stall can't be driven through the test host; `BundleDownloadEndpointsTests` uses a host per test because tickets live outside the shared-host reset (implementation).

## Kickoff prompt

```text
/spec implement 2026-11-15-rom-bundles.md
```
