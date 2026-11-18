---
title: Review QoL and Multiple Releases report
status: ready
created: 2026-11-18
---

# Review QoL and Multiple Releases report

## Summary
Cuts clicks for an Admin curating the library: Next missing image lands on the Images tab with the upload dialog open and focused, the Upload Image dialog submits on Enter, toasts with a link close themselves like every other toast, the review item page gets a styled back button and **Use and Next**, and the Images tab gets a Google Images button. Adds a new **Multiple Releases** report (every signed-in user) listing games with N or more releases, with sortable columns, total size and update count.

## Out of scope
- A user setting for the toast timeout (fixed 5 s).
- **Use and Next** on the Search tab's results (Suggestions only).
- A system filter on the Multiple Releases report.
- Sortable columns on any other table.
- Changing the Review-next hotkey's own behaviour.

## Behaviour

### Toasts
- **B1** Every toast closes itself `toastDurationMs` (5 s) after it appears, including toasts with a link (today those stay until closed). The close button still works.
- **B2** While the pointer is over a toast or focus is inside it (its link or close button), its timer pauses; when the pointer leaves and focus leaves, it gets a fresh 5 s. Pausing one toast doesn't affect others. Clicking the link dismisses it at once (unchanged).

### Next missing image and the Upload Image dialog
- **B3** The Next missing image shortcut opens `/games/<id>?tab=images&upload=1` instead of `/games/<id>`. The toasts for "only one left", "none left" and lookup failure are unchanged.
- **B4** On the game page, `upload=1` with an Admin opens the Upload Image dialog once the game has loaded. In the same step the page replaces the URL with `?tab=images` (`goto(…, { replace: true, reset: false })`: `upload` removed, `tab` forced to `images` whatever it was, since the dialog lives in the Images panel and a hidden ancestor would hide it), so a reload or Back doesn't reopen it. The dialog opens only after that navigation has made the Images panel visible. A Site user ignores it (the parameter is still removed, `tab` left as it was). A game that fails to load or isn't found ignores it.
- **B5** The trigger button and the dialog title read **Upload Image** (capital I). The confirm button stays **Upload**.
- **B6** Each time the dialog finishes opening (Bootstrap's `shown.bs.modal`), focus moves to the **Image URL** input (the dialog always opens on URL, as today). Switching to File and back doesn't refocus.
- **B7** Pressing Enter in the Image URL input submits the dialog exactly as **Upload** does (`preventDefault`, ignored while `busy`). Enter elsewhere in the dialog keeps its default behaviour (radio buttons, file input, buttons).

### Game page Images tab
- **B8** For Admins, a **Google Images** button (`btn btn-outline-secondary btn-sm`, Google icon, text "Google Images" so it can't be confused with the header's "Google" link) sits immediately before **Upload Image**; `ms-auto` moves from Upload Image to it, so the pair stays right-aligned. It is a link opening, in a new tab (`target="_blank" rel="noopener noreferrer"`), Google Images for the header's query: `https://www.google.com/search?udm=2&q=<encodeURIComponent(gameSearchQuery(game))>`. Site users don't see it.

### Review item page
- **B9** The `← Review queue` link becomes `<a class="btn btn-sm btn-outline-primary">` with the `back` icon and text **Review queue**, the same style as the game page's **Back to Library**. It still links to `/review`.
- **B10** Each Suggestions row gets **Use and Next** (`btn btn-sm btn-outline-primary`) right after **Use**. It resolves exactly as **Use** does (same request, same update checkbox, same done toast text).
- **B11** "Next" is the first Pending item, in the queue's default order, that isn't the current item, within the saved Review filter (`readSavedReviewFilter()` → `parseReviewFilter`; `q`, `system`, `reason` apply, `page` is ignored). It's looked up with `GET /api/review` built by `reviewApiUrl({...filter, page: 1}, 2)` and the first item whose id differs from the current one is taken.
- **B12** When the page loads an item (and again after each item change), it does that lookup. While it's running, or when it finds no other item, or it fails, **Use and Next** is disabled (as are all actions while `disabled` is true, unchanged). A disabled button has a tooltip-free `title` "No more items in the review queue." only when the lookup found none.
- **B13** When a **Use and Next** resolution ends successfully while the user is still on that item's page, `reviewJobs` repeats the B11 lookup (the queue may have changed) and navigates to that item, showing the usual success toast; if none is left, or the lookup fails, it opens the game, as **Use** does. `running` stays set until that lookup has settled, so the page's actions stay disabled meanwhile. After the lookup, `here` is checked again: if the user has left the item page in the meantime, nothing navigates and the existing off-page success toast (with **Open game**) is shown instead. A failure behaves exactly as **Use** (error shown on the page, no navigation). If the user left the page before the job ended, the existing off-page toast behaviour applies unchanged.

### Multiple Releases report
- **B14** A new Reports tab **Multiple Releases** (`/reports/multiple-releases`, icon name `report-multiple-releases` → `bi bi-layers`, unused elsewhere), after Duplicates and before Activity, for every signed-in user.
- **B15** A game's **releases** are its `roms` rows whose `flags` don't contain `"Update"` (DLC counts as a release). **Updates** are the rows that do. **Size** is the Library Size report's game bytes: `games.zip_size_bytes` plus the recorded `size_bytes` of its external ROMs and their set files (all external rows, updates included).
- **B16** `GET /api/reports/multiple-releases?min=&sort=&dir=&page=&pageSize=` (any signed-in user) returns `{maxReleases, items, page, pageSize, total}`. `min` defaults to 2 and must be 2–10000; `sort` is `name|system|releases|updates|size` (default `releases`); `dir` is `asc|desc` (default per sort key: `asc` for `name` and `system`, `desc` for the others); paging as the other reports (page ≥ 1, pageSize 1–200, default 50). `min`, `page` and `pageSize` bind as `int?` like the other reports, so a non-number is minimal-API binding's plain `400`; a well-formed number out of range, or an unknown `sort`/`dir` value (case-insensitive match), is `400 ValidationFailed` naming the field. Items are `{id, displayTitle, system, systemName, releaseCount, updateCount, bytes}` for games with `releaseCount >= min`. `maxReleases` is the highest release count of any game in the library (0 when empty), independent of `min`.
- **B17** Ordering: only the primary key follows `dir`; tie-breakers are always ascending. `releases`/`updates`/`size`: key, then `display_title`, then `id`. `name`: `display_title`, then `system_id`, then `id`. `system`: `system_id`, then `display_title`, then `id`.
- **B18** The page shows a **Minimum releases** `<select>` at the top with options 2…`max(maxReleases, 2)`, default 2. Until the first response arrives it is disabled and shows only the current `min`. A URL `min` within 2–10000 but above `max(maxReleases, 2)` is kept and added as one extra option (the table then shows B20's empty state). Changing it resets to page 1. State lives in the URL (`?min=&sort=&dir=&page=`) written with `goto(…, { replace: true, reset: false })`; a value equal to its default is omitted (`min` 2, `sort` releases, `page` 1, `dir` the sort key's default from B16). Bad URL values (non-numbers, out of range, unknown sort/dir) fall back to those defaults. The page always sends `sort` and `dir` explicitly.
- **B19** The table (T1–T4 table rules) has columns **Game** (link to `/games/<id>`), **System** (`SystemBadge`), **Releases**, **Updates**, **Size** (`<FileSize>`); numbers and size right-aligned `text-nowrap`. Every header is a sort button inside its `<th scope="col">`; the active one carries `aria-sort="ascending|descending"` and the `sort-asc` / `sort-desc` icon (existing `icons.ts` names); inactive headers have no icon and no `aria-sort`. Clicking the active header flips the direction; clicking another sorts by it in its default direction (B16) and resets to page 1. `Pagination` below, as on Missing Images. Loading/ErrorAlert as the other reports.
- **B20** `total === 0`: `EmptyState` "No games have <min> or more releases.". A `page` past the last page (`items` empty, `total > 0`) replaces the URL with the last page and reloads instead.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Web | `src/lib/stores/toasts.svelte.ts`, `src/lib/components/ToastHost.svelte` | B1–B2: always schedule dismissal; `pause(id)` / `resume(id)` per toast; host wires `mouseenter`/`mouseleave`/`focusin`/`focusout` |
| Web | `src/lib/components/ConfirmModal.svelte` | optional `onshown` prop, called on `shown.bs.modal` (B6) |
| Web | `src/lib/components/GameImages.svelte` | B4–B8: label/title, focus, Enter, Google button, `upload=1` handling (via `page.url`) |
| Web | `src/lib/googleSearch.ts` | `googleImagesUrl(query)` (B8) |
| Web | `src/lib/stores/hotkeys.svelte.ts` | B3: `?tab=images&upload=1` |
| Web | `src/routes/review/[id]/+page.svelte`, `src/lib/stores/reviewJobs.svelte.ts`, `src/lib/review.ts` | B9–B13: back button, Use and Next, a `nextReviewItem(currentId)` helper in `review.ts`; `ReviewJob` gains optional `next: true`, and `#finish` on the item's own page navigates to the next item instead of the game |
| Core | `src/RnRomManager.Core/Reports/IReportRepository.cs` (+ new record `MultipleReleaseItem`, enum `MultipleReleaseSort`) | B15–B17 contract |
| Data | `src/RnRomManager.Data/Repositories/ReportRepository.cs` | `ListMultipleReleasesAsync(min, sort, descending, page, pageSize, ct)` and `MaxReleaseCountAsync(ct)`; reuses the `GameBytes` size expression; sort key mapped from the enum (never interpolated from input) |
| Api | `src/RnRomManager.Api/Endpoints/ReportEndpoints.cs` | B16 endpoint and validation |
| Web | `src/routes/reports/multiple-releases/+page.svelte`, `src/routes/reports/+layout.svelte`, `src/lib/components/icons.ts` | B14, B18–B20 |
| Tests e2e | `tests/e2e/specs/mvp.spec.ts` | "Upload image" → "Upload Image" names; Multiple Releases tab visit + axe |
| Docs | `docs/features.md`, `docs/development.md` | toasts (remove "Toasts with a link stay until you close them"), shortcuts, Upload Image, Images tab Google, Review back button and Use and Next, Reports list and Report APIs, `development.md` Reports section |

No migrations, no config keys. API: one new GET; no existing contract changes.

## Risks
- **Size query cost:** the report computes per-game subqueries over all games (as Library Size already does). Acceptable at current scale; `maxReleases` is a second scan. No index added.
- **SQL injection:** sort column and direction come only from the enum/bool mapping, never from the query string.
- **Accessibility:** auto-dismissing link toasts would fail WCAG 2.2.1; B2's pause on hover/focus covers it. Auto-focus happens only after an explicit open (button or shortcut).
- **Use and Next race:** the queue can change between the load lookup and job end; B13 re-checks at the end.
- **e2e names:** the case change breaks `getByRole("button", { name: "Upload image" })` and the dialog name checks; updated in step 2.

## Steps
- [ ] **1. Toast auto-dismiss with pause**: B1–B2. Files: `src/lib/stores/toasts.svelte.ts`, `src/lib/components/ToastHost.svelte`. Also update the `ToastMessage.link` doc comment ("stays until closed") and replace the existing test "renders the link, and a toast with one stays until closed" in `stores.svelte.test.ts`. Tests: `src/lib/stores/stores.svelte.test.ts` (link toast dismissed after 5 s with fake timers; paused toast survives past 5 s and is dismissed 5 s after resume; other toasts unaffected), new `src/lib/components/ToastHost.test.ts` (mouseenter/focusin pause, mouseleave/focusout resume). Done when: link toasts close after 5 s; hovered/focused toasts stay; tests, `npm run lint`, `npm run check` pass.
- [ ] **2. Upload Image dialog and Images-tab Google**: B4–B8. Files: `ConfirmModal.svelte`, `GameImages.svelte`, `googleSearch.ts`, `tests/e2e/specs/mvp.spec.ts` (renamed button/dialog names only). Tests: `ConfirmModal.test.ts` (`onshown` fires on `shown.bs.modal`), `googleSearch.test.ts` (`googleImagesUrl` encodes and uses `udm=2`), `src/routes/games/game-images.test.ts` (label/title text; URL input focused on shown; Enter submits URL upload once and not while busy; "Google Images" link href/target for Admin, absent for Site user, placed before Upload Image with `ms-auto`; `upload=1` opens the dialog for Admin and the URL is replaced with `?tab=images`, also when `tab` was missing or another tab; ignored for Site user with `upload` removed), `src/routes/games/game-page.test.ts` (header "Google" link still found unambiguously). Done when: listed tests pass; lint/check pass.
- [ ] **3. Next missing image lands on Images**: B3. Files: `src/lib/stores/hotkeys.svelte.ts`. Tests: `src/lib/stores/hotkeys.svelte.test.ts` (navigates to `/games/<id>?tab=images&upload=1`). Done when: tests pass.
- [ ] **4. Multiple Releases query and API**: B15–B17, B16 endpoint. Files: `IReportRepository.cs`, new `MultipleReleaseItem`/`MultipleReleaseSort` in `Core/Reports`, `ReportRepository.cs`, `ReportEndpoints.cs`. Tests: new `tests/RnRomManager.Data.Tests/Database/MultipleReleasesQueryTests.cs` (Integration: update rows excluded from releases and counted as updates, DLC counted as release, threshold, each sort key both directions with tie-breakers, size includes external ROMs and set files, `maxReleases` on empty and filled library, paging total); `ReportEndpointsTests` (defaults incl. per-key `dir`, response shape incl. `systemName`, `ValidationFailed` for out-of-range `min`/`page`/`pageSize` and unknown `sort`/`dir`, Site user allowed). Done when: those classes pass (Docker running).
- [ ] **5. Multiple Releases page**: B14, B18–B20. Files: `src/routes/reports/multiple-releases/+page.svelte`, `src/routes/reports/+layout.svelte`, `src/lib/components/icons.ts`. Tests: `src/routes/reports/reports-pages.test.ts` (tab and icon present for Site user; threshold options 2…max, disabled before the first response, extra option for a URL `min` above max; changing threshold requests `min` and resets page; header click toggles `dir`, sets `aria-sort` and the sort icon, another header uses its default direction; URL state parsed with bad values falling back and defaults omitted; empty state only when `total` is 0; out-of-range page goes to the last page), `src/lib/table-style.test.ts` passes. Done when: tests, lint, check pass.
- [ ] **6. Review page back button and Use and Next**: B9–B13. Files: `src/routes/review/[id]/+page.svelte`, `src/lib/review.ts`, `src/lib/stores/reviewJobs.svelte.ts`. Tests: `src/routes/review/review-page.test.ts` (back button class/href; Use and Next present after each Use; disabled while lookup runs, when none and on failure; enabled with another item; respects saved filter in the request URL), `src/routes/review/review-job.test.ts` and `src/lib/stores/stores.svelte.test.ts` (on-page success with `next` re-looks-up and navigates to the next item, falls back to the game when none or the lookup fails; `running` stays set until the lookup settles; leaving the page during the lookup shows the off-page toast and doesn't navigate; failure unchanged; off-page toast unchanged). Done when: tests, lint, check pass.
- [ ] **7. Docs and e2e**: Files: `docs/features.md`, `docs/development.md`, `tests/e2e/specs/mvp.spec.ts` (visit Reports → Multiple Releases, axe scan, threshold select visible; the Basic seed's Switch game Orbit Expedition has a base and a DLC release plus updates, so assert its row is listed at threshold 2). Tests: e2e runs in Finish. Done when: every behaviour above is documented; the old "Toasts with a link stay until you close them" sentence is gone.

## Decisions
- 2026-11-18 Link toasts: auto-dismiss after 5 s with pause on hover/focus; no user setting → user
- 2026-11-18 "Releases" excludes `Update`-flagged rows; DLC counts → user
- 2026-11-18 Use and Next's next item honours the saved Review filter → user
- 2026-11-18 Report sorts and pages on the server → user
- 2026-11-18 Images-tab Google button: Admins only, Google Images with the header's query → user
- 2026-11-18 Next missing image also auto-opens the Upload Image dialog (Admin) → user
- 2026-11-18 Use and Next on Suggestions only, not Search results → user
- 2026-11-18 Back button style matches the game page's Back to Library (`btn-sm btn-outline-primary` + back icon) → code
- 2026-11-18 `upload=1` forces `tab=images` before opening the dialog (hidden panel would hide the modal) → reviewer
- 2026-11-18 Use and Next keeps the resolution "running" until the next-item lookup settles and re-checks the page after it → reviewer
- 2026-11-18 Report `dir` default is per sort key; tie-breakers always ascending; non-number params are binding's plain 400 → reviewer
- 2026-11-18 Images-tab button reads "Google Images" to tell it apart from the header's "Google" link → reviewer

## Kickoff prompt
```text
/spec implement 2026-11-18-review-qol-multi-release-report.md
```
Starts the spec, or resumes it at the first unticked step.
