---
title: UI polish, global search and ratings
status: ready
created: 2026-11-05
branch: dev/ui-search-ratings
---

# UI polish, global search and ratings

## Summary
A round of UI improvements for everyone who uses the SPA: a favicon, a clearer active page in the navbar, a global search modal (Ctrl+Space / Ctrl/Cmd+K / navbar button) that shows the top 5 matching games, a wider and denser Edit metadata modal, and a tidier library grid (uniform box art, system badges, one-line titles with tooltips). Games also get a shared 1–5 star rating and a favourite flag, stored in `info.json` (schema v2) and mirrored in the database, with a favourites filter and a rating sort.

## Out of scope
- Per-user ratings or favourites (values are shared per game).
- Searching anything except game titles (no developer/genre/ROM-name search, no fuzzy matching).
- Other tooltips beyond the ones named here (the tooltip action is built so later work can reuse it).
- Rating or favouriting in bulk.
- A favicon `.ico` file or a web app manifest.

## Behaviour

### Rating and favourite (data)
- **B1** Every game has `rating` (whole number 1–5, or null = not rated; default null) and `favourite` (bool, default false). They are shared: every user sees and edits the same values.
- **B2** `info.json` becomes **schema version 2**, adding game fields `rating` (int?, required key, null allowed) and `favourite` (bool, required). An upgrader v1→v2 adds `rating: null, favourite: false` in memory; files are rewritten as v2 on their next normal write (existing rule in [zip-format.md](../../docs/zip-format.md)). Validation: `rating` must be null or 1–5, otherwise `InfoJsonInvalid` naming the rule.
- **B3** The `games` table gets `rating TINYINT NULL` and `favourite TINYINT(1) NOT NULL DEFAULT 0`, written by every mirror write (ingestion, edits, integrity DB rebuild) from `info.json`. Existing rows get null/0, which matches the v1→v2 upgrade, so no rebuild is needed.
- **B4** Edits keep the values: a title/metadata edit, release delete, media retry and ingestion adding a release keep the game's rating and favourite. **Merge**: the target keeps its rating, or takes the source's if it has none; favourite = target OR source. **Move a release to a new game**: the new game starts with null / false.

### Rating and favourite (API)
- **B5** `PATCH /api/games/{id}/rating` (any signed-in user, Admin or User; antiforgery as for every PATCH). Body `{ "rating"?: 1–5 | null, "favourite"?: bool }`; an absent property is unchanged, `rating: null` clears it. Errors: not a JSON object, neither property present, `rating` not null or an integer 1–5, `favourite` not a bool → `400 ValidationFailed` with field errors (`""`, `rating`, `favourite`). Unknown id → `404`. A running job → `409 JobRunning`. A ZIP with a newer schema → `409 InfoJsonNewerSchema`. Write failure → `500` (same mapping as `ToProblem` today). Success → `200` with the full game detail, the same body as `GET /api/games/{id}`, because the rewrite gives the game new ROM and image ids (see `docs/features.md`). It goes through `LibraryEditor.PatchAsync` (job lock, atomic ZIP write, `metadata.updatedAt` touched).
- **B6** The Admin `PATCH /api/games/{id}` stays Admin-only and ignores `rating`/`favourite` (unknown properties, as today).
- **B7** `GET /api/games` items and `GET /api/games/{id}` gain `rating` and `favourite`. List items also gain `systemName` (the handler's `DisplayName`, or the id if the system isn't registered).

### Search, filter, sort (API)
- **B8** `q` is split on whitespace into distinct words (case-insensitive, at most 8; more → `400 ValidationFailed` on `q`: "At most 8 words."). A game matches when **every** word appears in `display_title` or `title` (each word `LIKE '%word%'` against either column, `%`/`_`/`\` literal as today). One word behaves exactly as today. The 255-character limit on `q` stays.
- **B9** New `sort=relevance`. For ranking, `q` is trimmed and internal whitespace runs are collapsed to one space; call this `qn`. Order: exact match first (`display_title = qn OR title = qn`, case-insensitive by collation), then prefix matches (`display_title LIKE 'qn%' OR title LIKE 'qn%'`, literal as in B8), then the rest; ties by `title`, then `id`. `dir` is ignored for relevance. Without `q`, relevance sorts like `title asc`.
- **B10** New `sort=rating`: default `desc`; unrated games always last; ties by `title`, then `id`. The sort error message becomes "Must be title, year, added, rating or relevance."
- **B11** New `favourite` filter, bound as `string?` and checked in `ParseQuery`: exactly `true` returns only favourites, exactly `false` only non-favourites, absent = all. Any other value → `400 ValidationFailed` on `favourite` ("Must be true or false.").

### Global search (SPA)
- **B12** For a signed-in user who doesn't have to change their password (the Search button is hidden and the shortcuts are off while `auth.me.mustChangePassword` is set), **Ctrl+Space**, **Ctrl+K** and **Cmd+K** open the search modal from anywhere, including inside text fields (`preventDefault` on those combinations). The navbar has a **Search** button (icon + "Search", with a `Ctrl K` hint from `lg` up) that does the same. A shortcut while the search modal is open just refocuses the input. While any other Bootstrap modal is open (`.modal.show`, e.g. Edit metadata or a confirm), the shortcuts do nothing.
- **B13** The modal (Bootstrap `Modal`, so focus is trapped and Escape closes it) blurs the page behind it (`backdrop-filter: blur(…)` on the modal element) and auto-focuses its text input. Closing with Escape, the close button or a backdrop click returns focus to the element that had it. Opening a result (B16) doesn't restore focus; SvelteKit's normal navigation focus handling applies.
- **B14** Typing queries `GET /api/games?q=<text>&sort=relevance&pageSize=5`, debounced 250 ms, once the trimmed text has at least 2 characters (same rule as the library). Older requests are aborted. States: under 2 chars → hint "Type at least 2 characters"; loading → small spinner; no results → "No games match"; error → inline error text (the modal stays open).
- **B15** Each result shows the box art thumbnail (fixed square, `object-fit: contain`, system icon placeholder when there's none), the display title (one line, truncated), a system badge with `systemName`, the year if known, and the read-only rating control (B22): when rated, filled/empty star icons with one accessible text "Rated N of 5"; when a favourite, a filled heart with the text "Favourite"; nothing at all when it's unrated and not a favourite.
- **B16** Keyboard: Up/Down move the highlighted result (combobox + listbox ARIA: `role="combobox"`, `aria-activedescendant`, `role="option"`), Enter opens the highlighted one (the first by default), clicking a result opens it. Opening navigates to `/games/{id}` and closes the modal (and clears the query).

### Navbar, favicon (SPA)
- **B17** The active navbar link (current rules in `isActive`) stands out: primary-coloured text, semibold, and a 3 px primary underline (from `lg` up; a left bar of the same colour in the collapsed mobile menu). In the dark theme it uses `--bs-primary-text-emphasis` so it keeps AA contrast. `aria-current="page"` stays.
- **B18** The site has a favicon: `static/favicon.svg` (the Bootstrap Icons `controller` glyph, MIT, filled Bootstrap primary `#0d6efd`) and a 32×32 `static/favicon-32.png` fallback, linked from `app.html` (`<link rel="icon" type="image/svg+xml">` then `<link rel="icon" type="image/png" sizes="32x32">`).

### Game page (SPA)
- **B19** The Edit metadata modal uses `modal-lg` (other admin modals keep the default width). Layout in a `row g-3` grid: Title (`col-md-9`) + Release year (`col-md-3`); Developers, Publishers, Genres side by side (`col-md-4` each, 3-row textareas, one shared "One per line." help text referenced by all three through `aria-describedby`); Description full width. Below `md` every field is full width. Validation messages and behaviour are unchanged.
- **B20** The game detail page shows the rating and favourite controls (B22) next to the title, editable by any signed-in user. After a successful save the page replaces its game detail with the B5 response, so release downloads, admin actions and image URLs use the new ids.

### Library (SPA)
- **B21** Grid cards: box art sits in a square (`ratio ratio-1x1`) frame with `object-fit-contain` on a `bg-body-secondary` background, so every card's image area has the same height whatever the art's aspect ratio. A system badge (upper-case system id, e.g. `GB`, `GBC`, with the full `systemName` as visually hidden text; no tooltip) sits on the top-left of the image. The title is one line (`text-truncate`). The card stays fully clickable: the title link has `stretched-link`, and the rating control sits above it with `position-relative z-2`. The full-title tooltip is attached to that title link, placed on top, and triggered by hover and focus. Because the stretched link covers the card, hovering anywhere on the card except the rating control shows it.
- **B22** Rating control (`RatingControl.svelte`), used on cards, list rows, the game page and (read-only) in search results: 5 star buttons (`aria-label="Rate N of 5"`, `aria-pressed` for the current value; clicking the current value clears it) and a favourite heart button (`aria-label="Favourite"`, `aria-pressed`). Changes are optimistic: the UI updates at once and calls B5. The **whole control** (all stars and the heart) is disabled while any of its saves is in flight. `409 JobRunning` is retried once after 1 s, because the job lock is global and never waits, so another user's rating on any game can collide. If the retry also fails, the control shows the standard `toasts.jobRunning` toast and reverts. Other errors revert with an error toast. In the library, a changed card stays where it is until the list next loads, even when the Favourites filter or the Rating sort would now exclude or move it.
- **B23** List view: the System column shows the same badge (full name), and a Rating column shows the control.
- **B24** Filters: a **Favourites** toggle button (`aria-pressed`) next to the view toggle sets `?favourite=true` (B11), and **Rating** is added to Sort by (`?sort=rating`, default `desc`). Both reset to page 1 like other filters. Only `favourite` counts in `hasFilters` ("Clear filters" / "No games match"); `sort=rating` doesn't, like the other sorts.
- **B25** Tooltips use Bootstrap's Tooltip (already in the bundled Bootstrap JS) through one Svelte action, `use:tooltip={text}` in `src/lib/tooltip.ts`: creates the tooltip on mount, updates its text on change, disposes it on destroy. Nothing else creates tooltips directly.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Data | `src/RnRomManager.Data/Migrations/0010_game_rating.sql` | `ALTER TABLE games ADD rating TINYINT NULL, ADD favourite TINYINT(1) NOT NULL DEFAULT 0`. Existing rows: null/0 (B3). |
| Data | `src/RnRomManager.Data/Repositories/LibraryRepositories.cs`, `SqlLike.cs` (reuse) | Insert/update/select the two columns; word matching, relevance and rating sorts, favourite filter; list rows return rating/favourite. |
| Core | `Library/InfoJsonGame.cs`, `InfoJsonReader.cs` (`CurrentVersion = 2`), new `InfoJsonUpgraderV1.cs`, `InfoJsonValidator.cs`, `LibraryRows.cs`, `GameListItem.cs`, `GameQuery.cs`, `GameSort.cs`, `GamePatch.cs`, `LibraryEditor.cs`, DI registration of the upgrader | Schema v2 fields + upgrader + validation; mirror columns; query words/favourite/sorts; patch fields; merge/move rules (B4). Every place that builds a new `InfoJsonGame` (ingestion, move-to-new) sets `Rating = null, Favourite = false`. |
| Api | `Endpoints/LibraryEndpoints.cs` | `PATCH /api/games/{id}/rating` (B5); DTO fields (B7); `ParseQuery` words/sorts/favourite (B8–B11). |
| Web | `static/favicon.svg`, `static/favicon-32.png`, `src/app.html` | Favicon (B18). |
| Web | `src/app.css` | Navbar accent (B17), search modal blur (B13). |
| Web | `src/lib/bootstrap.ts`, new `src/lib/tooltip.ts` | Export `Tooltip`; the tooltip action (B25). |
| Web | `src/lib/components/icons.ts`, `Navbar.svelte`, new `GlobalSearch.svelte`, `src/routes/+layout.svelte` | Icons (`search`, `star`, `star-fill`, `favourite`, `favourite-fill`); search button; the modal and shortcut (B12–B16). |
| Web | new `src/lib/components/RatingControl.svelte`, new `src/lib/rating.ts` (save + revert helper) | B22. |
| Web | `src/lib/components/ConfirmModal.svelte` (`size?: 'lg'` prop), `GameAdminModal.svelte`, `src/routes/games/[id]/+page.svelte` | B19, B20. |
| Web | `src/routes/+page.svelte`, `src/lib/libraryQuery.ts`, `src/lib/api/types.ts` | B21, B23, B24; `LibraryFilters.favourite`, `LibrarySort` += `rating`. |
| Tests | see Steps; `tests/RnRomManager.Api.Tests/Integration/EndpointRoleMatrixTests.cs` gets the new endpoint (User and Admin allowed, anonymous 401). |
| Docs | `docs/zip-format.md` (v2 fields + Versions row), `docs/features.md` (search, ratings, favourites, filters, API list), `docs/development.md` (new components, tooltip action, `app.css` now holds more than one override) |

API contract changes are additive (new fields, new endpoint, new query values). `info.json` v2 is a forward-only change (see Risks).

## Risks
- **Older app versions can't edit v2 ZIPs.** Once a ZIP is written as v2, app 0.1.0 treats it as `InfoJsonNewerSchema` (read-only). Acceptable: there is one deployment and no downgrade path is promised; the Versions table in `zip-format.md` records it.
- **Rating takes the job lock and rewrites the ZIP.** Rating during ingestion fails with `409 JobRunning` (toast, UI reverts). GB/GBC ZIPs are small, so the rewrite is fast; larger systems may need a cheaper path later (noted in the code with a `shortcut:` comment).
- **Concurrent edits**: the job lock is one global, non-waiting lock (`JobLock.TryAcquire`), so two ratings at the same moment on *any* games (or a rating during a job) give one of them `409 JobRunning`. B22 retries once after 1 s, then toasts and reverts. A later rating of the same game overwrites the earlier one.
- **ID churn**: every rating rewrites the mirror rows, so ROM and image ids change; B5 returns the full detail and the game page swaps it in (B20).
- **Nested interactive elements**: cards were a single `<a>`; buttons inside a link are invalid HTML and break axe. B21 moves to a `stretched-link`, keeping the whole card clickable.
- **Shortcut clashes**: Ctrl+Space is the input-method switch on some systems; Ctrl/Cmd+K is the fallback (B12). Browsers' own Ctrl+K (focus address bar search in Firefox/Chrome) is overridden while the app has focus.
- **Search SQL**: up to 8 `LIKE` groups on `games` only; no index is used for `%word%` today either. Fine for libraries of thousands of games.

## Steps
- [ ] **1. Schema v2 and DB columns**: migration `0010_game_rating.sql`; `InfoJsonGame.Rating`/`Favourite`; `CurrentVersion = 2`; upgrader v1→v2 (registered in DI); validator rule for `rating`; `GameRow`, `ListRow` and `GameListItem` + repository insert/update/select of the columns (including the `ListAsync` SELECT); every new-game construction sets the defaults; `docs/zip-format.md` v2 fields and Versions row. Fix the tests this breaks: `InfoJsonTests` (`NewerSchema_IsDetected_NotParsedFurther` uses version 3; `UpgraderChain_RunsForOlderVersions` covers v1→v2; `Game()` builds v2 and `Reader()` passes the real `InfoJsonUpgraderV1`), the `InfoJsonGame` initialisers in `TestLibrary.cs` and `LibraryZipTests.cs`, and the migration list in `SchemaMigrationTests` (add `0010_game_rating`). Files: `src/RnRomManager.Data/Migrations/0010_game_rating.sql`, `src/RnRomManager.Core/Library/{InfoJsonGame,InfoJsonReader,InfoJsonUpgraderV1,InfoJsonValidator,LibraryRows}.cs`, ingestion/editor constructors of `InfoJsonGame`, `src/RnRomManager.Data/Repositories/LibraryRepositories.cs`, `docs/zip-format.md`. Tests: `InfoJsonTests` (v1 file reads as v2 with null/false; v2 round-trips; rating 0 and 6 invalid; newer-than-2 still `InfoJsonNewerSchema`), `SchemaMigrationTests` (columns exist with defaults), `LibraryMirrorTests` (mirror writes and reads rating/favourite). Done when: all three test projects pass; a v1 ZIP still opens; new ZIPs are written with `schemaVersion: 2`.
- [ ] **2. Editor rules**: `GamePatch` gets `SetRating`/`Rating`/`Favourite`; `PatchAsync` applies them; merge and move-to-new follow B4; `shortcut:` comment on the ZIP rewrite cost. Files: `src/RnRomManager.Core/Library/{GamePatch,LibraryEditor}.cs`. Tests: `tests/RnRomManager.Api.Tests/Integration/LibraryEditTests.cs` (patch rating only keeps title/metadata and ZIP path; clearing rating; title edit keeps rating/favourite), `LibraryMoveMergeTests.cs` (merge: target-null takes source rating, target rating kept, favourite OR; move to new game → null/false). Done when: those tests pass and existing edit tests are unchanged.
- [ ] **3. Rating endpoint and DTO fields**: `PATCH /api/games/{id}/rating` (B5) with a `ParseRating(JsonElement)` validator; `rating`, `favourite` on list and detail, `systemName` on list (B7). Files: `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs`. Tests: new `tests/RnRomManager.Api.Tests/RatingRequestParsingTests.cs` (each 400 case from B5, valid bodies), `LibraryEndpointsTests` (User can rate → 200 with the full detail and the list/detail show it; 404; 409 while the job lock is held; Admin PATCH ignores `rating`), `EndpointRoleMatrixTests` (new row). Done when: tests pass and `info.json` in the ZIP holds the new values after a rating call.
- [ ] **4. Search words, relevance, rating sort, favourite filter**: `ParseQuery` and `GameQuery` (words list, `favourite`, `GameSort.Rating`/`Relevance`), SQL in `ListAsync` (B8–B11). Files: `src/RnRomManager.Core/Library/{GameQuery,GameSort}.cs`, `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs`, `src/RnRomManager.Data/Repositories/LibraryRepositories.cs`. Tests: `GameQueryParsingTests` (word split/distinct, 9 words → 400, new sort values, default dirs, `favourite` `true`/`false` accepted and `True`/`1`/`yes` → `ValidationFailed` on `favourite`), `tests/RnRomManager.Data.Tests/Database/GameQueryTests.cs` (multi-word any order; `%`/`_` literal per word; relevance order exact → prefix → rest, matching on either column (`Quest, The` and `The Quest`) and with collapsed whitespace; relevance without q = title; rating desc with unrated last in both directions; favourite true/false). Done when: tests pass; the existing single-word search tests are unchanged.
- [ ] **5. SPA foundation: favicon, navbar accent, tooltip action, types**: favicon files + `app.html` links; navbar active styling in `app.css`; `Tooltip` export and `use:tooltip`; new icons; `GameListItem`/`GameDetail` types gain `rating`, `favourite`, `systemName`; `libraryQuery.ts` gains `favourite` and `rating` sort (URL ↔ filters ↔ API, `hasFilters`). Files: `static/favicon.svg`, `static/favicon-32.png` (rendered once from the SVG, e.g. with the e2e project's Playwright, and committed), `src/app.html`, `src/app.css`, `src/bootstrap.d.ts` (declare `Tooltip`), `src/lib/bootstrap.ts`, `src/lib/tooltip.ts`, `src/lib/components/icons.ts`, `src/lib/api/types.ts`, `src/lib/libraryQuery.ts`. Tests: `tooltip.test.ts` (creates on mount with the text, updates on change, disposes on destroy, Tooltip mocked), `libraryQuery.test.ts` (favourite and rating round-trip; `sort=rating` default dir desc; bad values fall back; `hasFilters` true for `favourite`, false for `sort=rating`), a Navbar test that the active link has `aria-current="page"` and the accent class. Done when: lint, check and tests pass; the favicon shows in `npm run dev`.
- [ ] **6. Rating control, game page, Edit metadata layout**: `RatingControl.svelte` + `src/lib/rating.ts` (B22); game page shows it (B20); `ConfirmModal` `size` prop, edit form re-layout (B19). Files: `src/lib/components/{RatingControl,ConfirmModal,GameAdminModal}.svelte`, `src/lib/rating.ts`, `src/routes/games/[id]/+page.svelte`. Tests: `RatingControl.test.ts` (click star → PATCH body and optimistic update; click current → `rating: null`; favourite toggle; 409 then success on the retry keeps the value; 409 twice → jobRunning toast and revert; other error → revert; the whole control is disabled while a save is pending, so a heart click during a star save sends nothing; read-only mode renders no buttons, shows "Rated N of 5" / "Favourite", and renders nothing when unrated and not a favourite), `game-admin.test.ts` (edit modal has `modal-lg`, fields still bound and validated), `game-page.test.ts` (control shown for a User; after a save the page uses the returned detail, e.g. new ROM download URLs). Done when: SPA checks pass.
- [ ] **7. Library page**: uniform box-art frame, system badge, one-line titles with tooltips, stretched-link cards, rating control in cards and list, Favourites toggle and Rating sort (B21, B23, B24). Files: `src/routes/+page.svelte`. Tests: `library-page.test.ts` (badge text and hidden full name; title has `text-truncate` and a tooltip; image inside `ratio-1x1` with `object-fit-contain`; card link still goes to the game; Favourites toggle writes `favourite=true` and resets page; Rating sort in the URL; list view shows badge and rating). Done when: SPA checks pass.
- [ ] **8. Global search**: `GlobalSearch.svelte` mounted in `+layout.svelte` for signed-in pages, the navbar Search button, shortcuts (B12–B16). Files: `src/lib/components/{GlobalSearch,Navbar}.svelte`, `src/routes/+layout.svelte`, `src/app.css`. Tests: `GlobalSearch.test.ts` (Ctrl+Space, Ctrl+K and Meta+K open it and focus the input, also from inside another input; navbar button opens it; < 2 chars → hint and no request; debounced request with `sort=relevance&pageSize=5`; stale request aborted; results show title, `systemName` badge, year, box art or placeholder; shortcuts do nothing while another modal is open or when `mustChangePassword` is set (and the Search button is hidden); Up/Down/Enter and click navigate to `/games/{id}` and close; no results and error states). Done when: SPA checks pass.
- [ ] **9. E2E and docs**: extend `tests/e2e/specs/mvp.spec.ts`: open search with Ctrl+K, type part of a seeded title, Enter opens its game page; rate a game and favourite it on the library, reload and see both kept; Favourites filter shows only it; finally clear the rating and favourite so later steps see the seed state; axe scan with the search modal open (both themes, as the existing scans). Update `docs/features.md` (global search, ratings/favourites, filters/sorts, `PATCH /api/games/{id}/rating` and new query values in the API list) and `docs/development.md` (components, tooltip action, `app.css` overrides). Files: `tests/e2e/specs/mvp.spec.ts`, `docs/features.md`, `docs/development.md`. Tests: the e2e run. Done when: `pwsh scripts/test-all.ps1` and `pwsh scripts/test-e2e.ps1` pass; every behaviour above is in a doc.

## Decisions
- 2026-11-05 Rating/favourite shared per game or per user? → Shared per game, in `info.json` and the DB (user)
- 2026-11-05 Who can rate/favourite? → Any signed-in user, through a new endpoint; the metadata PATCH stays Admin-only (user)
- 2026-11-05 Rating scale? → 1–5 whole stars, clearable, null by default (user)
- 2026-11-05 Search matching? → Every word must match, ranked exact → prefix → A–Z via a new `relevance` sort; the library search box gets word matching too (user)
- 2026-11-05 Shortcut? → Ctrl+Space plus Ctrl/Cmd+K, because the OS takes Ctrl+Space on some systems (user)
- 2026-11-05 Where are the rating controls? → Library cards, list rows and the game page; read-only in search results (user)
- 2026-11-05 Extras → Favourites filter, Rating sort, rating/favourite shown in search results (user)
- 2026-11-05 Favicon → Bootstrap Icons `controller` SVG in primary blue, plus a 32×32 PNG (user)
- 2026-11-05 Tooltip framework → Bootstrap Tooltip, already in the bundled Bootstrap JS, so no new dependency (code)
- 2026-11-05 Box art frame → square (1:1), matching the existing placeholder and the 512×512 default art (code)
- 2026-11-05 Edit modal width → `modal-lg` (800 px) (code default)
- 2026-11-05 System badge text in the grid → upper-case system id (`GB`/`GBC`), full name as visually hidden text; full name in list and search (code default)
- 2026-11-05 Rating hits `409 JobRunning` (global non-waiting lock)? → retry once after 1 s, then the job toast and revert (user, from review R1)
- 2026-11-05 Rating response → full game detail, because the rewrite changes ids (reviewer R2)
- 2026-11-05 Card tooltips → the title tooltip goes on the stretched title link; no tooltip on the badge (reviewer R3)
- 2026-11-05 Card after a rating change under the Favourites filter or Rating sort → stays until the list next loads (code default, reviewer R10)
- 2026-11-05 Shortcut while another modal is open → does nothing (reviewer R4)
- 2026-11-05 `favourite` query values → exactly `true`/`false`, otherwise `400 ValidationFailed` (reviewer R7)
- 2026-11-05 Relevance ranking → collapsed-whitespace `qn` against either title column (reviewer R8)
- 2026-11-05 Clear filters → only `favourite` counts in `hasFilters`, not `sort=rating` (reviewer R9)
- 2026-11-05 Read-only rating → "Rated N of 5" / "Favourite", nothing when unset (reviewer R11)
- 2026-11-05 Focus after search → restored only on cancel; navigation uses SvelteKit's focus handling (reviewer R12)
- 2026-11-05 Search while `mustChangePassword` → button hidden, shortcuts off (reviewer R13)
- 2026-11-05 E2E state → the rating test clears the rating and favourite it set before it ends, so later steps see the seed state (reviewer)
- 2026-11-05 Merge rule for rating/favourite → target's rating unless null, favourite OR (code default)

## Kickoff prompt
```text
/spec implement 2026-11-05-ui-search-ratings.md
```
Starts the spec, or resumes it at the first unticked step.
