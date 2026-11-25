---
title: Release Timeline and UI polish
status: ready
created: 2026-11-25
---

# Release Timeline and UI polish

## Summary
Adds a **Timeline** page (games linked to IGDB, counted per release year, zoomable, with a paged games table), a **Developers** collections tab, IGDB-id matching in the Duplicates report, a game count on Admin → ROM Zip Config, and a batch of UI fixes: Edit Metadata's Lookup Info actions move to the modal footer and tick every field, the game page gets a top button row (Google, Emulators, View on IGDB), clickable metadata, a two-line description, a highlighted cover card, and four reports gain a total count line. For every signed-in user except where a rule says Admin.

## Out of scope
- Games without an IGDB link (`igdb_id` null or 0) or without a release year on the Timeline.
- Mouse-wheel zoom or drag-to-pan on the Timeline (buttons only).
- Starter art for developers (text banner fallback only).
- Storing IGDB URLs or slugs; the link is resolved on demand (T7).
- Cross-system duplicate matching.

## Behaviour

### Timeline (T)
- **T1** Navbar: a **Timeline** link (icon `timeline`) between **Collections** and **Reports**, for every signed-in user, active on `/timeline`. Page `/timeline` with `<PageTitle icon="timeline" text="Timeline" />` and `<title>Timeline · RnRomManager</title>`. New `icons.ts` entries: `timeline: 'bi bi-calendar-range'`, `'zoom-in': 'bi bi-zoom-in'`, `'zoom-out': 'bi bi-zoom-out'`, `earlier: 'bi bi-chevron-left'`, `later: 'bi bi-chevron-right'`.
- **T2** Scope: games with `igdb_id > 0` and `release_year IS NOT NULL`. Everything below (counts, table) uses only these games.
- **T3** URL state `?system=&from=&to=&page=` (`lib/timeline.ts`, new). The **data range** is the min and max year of `GET /api/timeline?system=` for the current system. `from`/`to` are whole years; missing or non-numeric → the data range's bound; outside the data range → clamped to it; `from` > `to` → swapped. `page` ≥ 1, default 1. An unknown `system` → treated as absent. Page 1, the full data range and no system are omitted from the URL. Every change (system, zoom, pan, tick, Show all, page) navigates with `goto(…, { reset: false })` and so adds a history entry, as the Library (`routes/+page.svelte` `go`) does. Changing system resets `from`/`to` to the new system's full range and `page` to 1; changing range resets `page` to 1.
- **T4** Controls, in one row above the strip: the reports' System `FilterDropdown` (as the Duplicates report uses it), then a button group: **Zoom in** (icon `zoom-in`, `aria-label="Zoom in"`), **Zoom out** (`zoom-out`, `aria-label="Zoom out"`), **Earlier** (`earlier`, `aria-label="Earlier"`), **Later** (`later`, `aria-label="Later"`), **Show all** (text). With `span = to - from + 1` and data range `[min, max]`:
  - Zoom in: `n = max(1, ceil(span / 2))`, `from' = from + floor((span - n) / 2)`, `to' = from' + n - 1`. 1990–1999 → 1992–1996.
  - Zoom out: `n = min(max - min + 1, span * 2)`, `from' = from - floor((n - span) / 2)`, then shifted into the range (`from' = max(min, min(from', max - n + 1))`), `to' = from' + n - 1`. 1992–1996 in 1980–2020 → 1990–1999; 1980–1984 → 1980–1989.
  - Earlier / Later: `step = max(1, floor(span / 2))`; the window moves by `step` keeping its span, shifted back inside the range. 1982–1991 in 1980–2020, Earlier → 1980–1989.
  - Show all: the full data range.
  - Disabled: Zoom in when `from == to`; Zoom out and Show all at the full range; Earlier when `from == min`; Later when `to == max`. A line beside them reads `<from>–<to>` (or `<year>` when equal).
- **T5** The strip: one tick per year in `[from, to]`, zero-count years included, in a horizontally scrollable row (`overflow-x: auto`). Each tick is a button showing the year and, under it, its count via `formatCount` (`0` muted). Accessible name `<year>: <formatCount(n)> games` (`1 game` singular); `aria-pressed="true"` on the tick when `from == to == year`, else `false`. Clicking a tick sets `from = to = year`. The pressed tick is `btn-primary`, others `btn-outline-secondary`.
- **T6** Table: the games in `[from, to]` (and system), sorted by release year ascending, then title, then id (G1), 50 per page, through `GameResults` with `view="list"` (title link to `/games/<id>`, system, year, quick play as in the Library) plus an `action` snippet: **View on IGDB** (an `<a>` with icon `external`, `target="_blank" rel="noopener noreferrer"`, `aria-label="View <title> on IGDB"`, href `/api/igdb/games/<igdbId>/page`), shown only when the timeline response says `igdbConfigured`. `Pagination` below the table; above it a count line `formatCount(total)` + ` game`/` games` (Missing Metadata's markup). No linked game with a year at all: `EmptyState` "No games linked to IGDB have a release year yet." and no strip or table. A range with no games: the strip shows and the table area shows `EmptyState` "No games in this period.".
- **T7** `GET /api/igdb/games/{igdbId:int}/page` (any signed-in user): `igdbId` < 1 → `404`. IGDB not configured → `409 IgdbNotConfigured` (same title as `igdb-matches`). Otherwise `GamePageUrlAsync` asks IGDB (`fields url; where id = <id>; limit 1;`, through the existing token and 401-retry path). `GamePageResult(Url, FailedTitle)`: `Url` set → `302` to it; both null (no game, a game with no `url`, or a `url` not starting with `https://www.igdb.com/`) → `404`; `FailedTitle` set → `502 IgdbFailed` with that title. Results with `Url` set and both-null results are cached in memory per process (`IgdbPageUrlCache`, cleared when it holds 10,000 entries); failures aren't cached. No audit row.
- **T8** `GET /api/timeline?system=` (any signed-in user) → `{igdbConfigured, total, years: [{year, count}]}`: years with ≥ 1 game in scope, ascending; `total` is their sum. Unknown `system` → `400 ValidationFailed` (`errors.system`: "Choose a registered system.").
- **T9** `GET /api/timeline/games?system=&from=&to=&page=&pageSize=` (any signed-in user) → the Library's paged list shape (`{items, page, pageSize, total}`, each item the `/api/games` item, which gains `igdbId`), scope T2, `from`/`to` inclusive and optional. `from` or `to` outside 1–9999 → `400` keyed by that field, "Must be 1–9999."; `from` > `to` → `400 errors.from`: "Must not be after to.". `page`, `pageSize` and `system` validate as `/api/games`; default pageSize 50. Sorted as T6.

### Game list sort (G)
- **G1** `GameSort.Year` orders `g.release_year IS NULL, g.release_year {dir}, g.title, g.id` (title and id always ascending, as Rating does), everywhere it's used (Library, collections, Timeline).

### Duplicates (D)
- **D1** `GET /api/reports/duplicates` takes `match=title|igdb` (case-insensitive, default `title`; anything else → `400 ValidationFailed`, `errors.match`: "Must be title or igdb."). `igdb` groups are 2+ games in the same system sharing one `igdb_id > 0`, sorted by system then the group's title. Each group gets `igdbId` (null for title groups). For an IGDB group `baseTitle` is the smallest `display_title` of its games (display only).
- **D2** Dismissals are kept per match type: new migration `0028_duplicate_dismissal_match` adds `match_kind VARCHAR(8) NOT NULL DEFAULT 'title'` to `duplicate_dismissals` and makes the primary key `(match_kind, system_id, base_title, games_key)`. Existing rows become title dismissals. An IGDB dismissal stores `base_title` = the IGDB id in decimal.
- **D3** `POST /api/reports/duplicates/dismiss` body `{system, gamesKey, baseTitle}` (title, unchanged) or `{system, gamesKey, igdbId}` (IGDB). Both or neither of `baseTitle`/`igdbId` → `400 errors.igdbId`: "Give baseTitle or igdbId, not both."; `igdbId` not a whole number ≥ 1 → `400 errors.igdbId`: "Must be a positive whole number.". Merge is unchanged.
- **D4** Duplicates page: a **Match by** `<select class="form-select form-select-sm w-auto">` (label "Match by", before the System filter) with **IGDB ID** (default) and **Title**, held in page state like the System filter; changing it reloads page 1. IGDB group headings show the base title then a `badge text-bg-secondary` "IGDB <id>". Empty text for IGDB: "No games share an IGDB ID." (Title keeps "No duplicate games found.").

### Report totals (R)
- **R1** Missing Images, Multiple Releases and Games With Hacks show a count line above their table when `total > 0`, using Missing Metadata's markup (`<p class="small text-body-secondary mb-2">`, `formatCount`): "N games" / "1 game". Duplicates shows "N groups" / "1 group" below the filters, above the first group card.

### ROM Zip Config (Z)
- **Z1** `GET /api/admin/rom-storage` items gain `gameCount` (games of that system, from `ListSystemStatsAsync`; 0 when none). The table gets a **Games** column after **Folder** (`text-end`, `formatCount`), inside the unlabelled System colgroup (its `colspan` becomes 3).

### Edit Metadata → Lookup Info (M)
- **M1** **Use this match** and **Use this match and save** (`btn btn-success`) and **Not on IGDB** (`btn btn-outline-warning`), all the footer's default size, move from the tab body into the modal footer through `ConfirmModal`'s `footer` snippet (left of Cancel/Save), only while the Lookup Info tab is shown. The match buttons show only when a match is selected; Not on IGDB shows unless the lookup reported IGDB not configured (as today). Behaviour of each is unchanged. "View on IGDB" stays in the body.
  Interface: `IgdbLookup` gains `$bindable` props `chosen: IgdbMatch | null` (the selected match, written by the component) and `notConfigured: boolean`, and exports `useMatch(save: boolean): void` and `notOnIgdb(): void` (`export function`). `GameAdminModal` binds both props and holds the instance with `bind:this`.
- **M2** Selecting a match ticks every enabled row, Title included (`defaultCopyFields`: `checked = !disabled`). Disabled rows (empty or equal IGDB value) stay unticked and disabled.

### Game page (P)
- **P1** The top row (`<p>` with **Back to Library**) becomes a `d-flex flex-wrap gap-2 mb-3` row: Back to Library, then **Google**, then **Emulators**, then **View on IGDB** (`btn btn-sm btn-outline-secondary`, icon `external`, `target="_blank" rel="noopener noreferrer"`, href T7) shown only when `game.igdbConfigured && game.igdbId > 0`. Google and Emulators keep their classes, icons and behaviour (Emulators keeps `bind:this={emulatorsButton}` for focus return), render only once the game has loaded, and are removed from the action row, which becomes: Download ZIP, Download ROM, Collections, Next…, Manage.
- **P2** Metadata table: each developer, publisher and genre is its own link, comma-separated, to `/collections?tab=<developers|publishers|genres>&name=<value>` (built with `URLSearchParams`). Year links to `/timeline?from=<y>&to=<y>` only when the game has a year and `igdbId > 0` (else plain text, since T2 excludes it). `—` stays plain text.
- **P3** Collections resolves `?name=`: it calls `GET /api/discovery/{kind}/key?name=<value>` and replaces the URL (`goto(…, { replace: true, reset: false })`, as `goDiscovery` does) with `?tab=<kind>&key=<key>`. A 404 replaces it with `?tab=<kind>&find=<value>` (cut to the existing `find` limit).
- **P4** `GET /api/discovery/{kind}/key?name=` (any signed-in user; `kind` publishers, developers or genres, else 404): `name` 1–255 characters after trimming (else `400 errors.name`: "Must be 1–255 characters."); returns `{key}` of the group whose normalized name equals the normalized `name` (normalization done by MariaDB as in `DiscoverySql`), or 404 when no game has it.
- **P5** Description: `<p id="game-description">` clamped to 2 lines (`-webkit-line-clamp: 2`, `white-space: pre-line` kept). When the full text is taller than the clamp (measured after render and on resize), a `btn btn-link btn-sm p-0` toggle **Show more** / **Show less** follows it, with `aria-expanded` and `aria-controls="game-description"`. It collapses again when navigating to another game.
- **P6** Images tab: the cover's card gets `border-primary border-2` and its body `bg-primary-subtle`; the "Cover" badge stays.

### Collections → Developers (C)
- **C1** Tabs become: User collections, Publishers, **Developers**, Genres, Systems (`?tab=developers`). It behaves exactly as Publishers (list with counts and search, selection, paging, games grid, URL state), grouping `games.developers`. Text: list aria-label "Developers", pager label "Developer pages", noun "developer" in the empty, no-match (`No developers match “…”.`) and "choose a developer" messages, and LogoModal's noun "developer". Every `publishers ? … : genre…` two-way ternary (`DiscoveryGroups.svelte`, `DiscoveryGames.svelte`, `LogoModal.svelte`, `routes/collections/+page.svelte`) becomes a per-kind lookup.
- **C2** `DiscoveryKind.Developers` (route `developers`), `LogoKind.Developers` (stored `developers`), audit actions `DeveloperLogoReplaced` / `DeveloperLogoRemoved` (in `AuditActions.All`); Admin **Change logo** works as for publishers. No starter art (`starterByKind.developers` is an empty map): shared logo, else the text banner.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Data | `src/RnRomManager.Data/Migrations/0028_duplicate_dismissal_match.sql` (new) | D2; existing rows keep working as title dismissals. |
| Core | `Core/Library/GameQuery.cs`, `GameListItem.cs`, `ILibraryRepositories.cs` | `YearFrom`, `YearTo`, `IgdbLinked` on `GameQuery`; `IgdbId` on `GameListItem` (default null); `ListReleaseYearCountsAsync(string? systemId, ct)` (new) on `IGameRepository`. |
| Data | `Data/Repositories/LibraryRepositories.cs` | Filters, G1 ORDER BY, year counts query, `igdb_id` in list rows. |
| Core/Data | `Core/Reports/IReportRepository.cs`, `Data/Repositories/ReportRepository.cs` | `DuplicateMatch` enum (new, `Title`/`Igdb`) on `ListDuplicatesAsync`/`DismissDuplicateAsync`; `IgdbId` on `DuplicateGroup`. |
| Core/Data/Api | `Core/Discovery/DiscoveryKind.cs`, `DiscoveryLogo.cs`, `IDiscoveryRepository.cs`; `Data/Repositories/DiscoverySql.cs`, `DiscoveryRepository.cs`; `Api/Endpoints/DiscoveryEndpoints.cs`, `DiscoveryLogoEndpoints.cs`; `Core/Audit/AuditActions.cs` | C2; `FindKeyAsync(kind, name, ct)` (new) for P4. |
| Core/Api | `Core/Media/IgdbClient.cs`, `Api/Http/HttpClients.cs` | `IGameInfoLookup.GamePageUrlAsync(int igdbId, ct)` returning `GamePageResult(string? Url, string? FailedTitle)` (new); `IgdbPageUrlCache` (new singleton, registered beside `IgdbTokenCache` in `HttpClients.cs`). |
| Api | `Api/Endpoints/TimelineEndpoints.cs` (new), `AppHost.cs` | T7–T9. |
| Api | `Api/Endpoints/LibraryEndpoints.cs`, `ReportEndpoints.cs`, `AdminEndpoints.cs` | `igdbId` in `ToListItemDto`; D1/D3; Z1. |
| Web | `lib/api/types.ts`, `lib/components/icons.ts`, `lib/components/Navbar.svelte`, `lib/timeline.ts` (new), `routes/timeline/+page.svelte` (new) | T1–T6. |
| Web | `lib/discovery.ts`, `routes/collections/+page.svelte`, `lib/components/DiscoveryGroups.svelte`, `DiscoveryGames.svelte`, `LogoModal.svelte` | C1, P3. |
| Web | `routes/reports/{missing-images,duplicates,multiple-releases,hacks}/+page.svelte` | R1, D4. |
| Web | `routes/admin/rom-storage/+page.svelte` | Z1. |
| Web | `lib/components/IgdbLookup.svelte`, `GameAdminModal.svelte`, `lib/gameEdit.ts` | M1, M2. |
| Web | `routes/games/[id]/+page.svelte`, `lib/components/GameImages.svelte` | P1, P2, P5, P6. |
| Docs | `docs/features.md`: Navigation, Publishers and genres (Developers), Game detail, Reports, Report APIs, Publishers and genres through the API (developers kind, `key?name=`), File action auditing (developer logo actions), ROM Zip Config, new "Timeline" section; `docs/development.md`: Collections discovery (developers logo kind) | Doc rule. |

API contract changes are additive except: the Year sort order within a year (G1), and duplicate dismissals' key (D2, migrated in place).

## Risks
- **IGDB rate limits (T7):** any signed-in user can trigger one IGDB call per uncached id. Hits and not-found results are cached, so only distinct new ids cost a call; looping over many ids shares the rate limit with Admin lookups. Accepted (signed-in users only).
- **Duplicates PK change (D2):** a single `ALTER` on a small table; covered by `SchemaMigrationTests`.
- **Timeline query cost:** counts are one `GROUP BY release_year` over `games` (no index on `igdb_id`); fine at library sizes of ~100k rows. Not cached.
- **Developers index build:** same cost as publishers; shares the 60 s cache.

## Steps
- [ ] **1. Game query filters and year counts**: G1; `GameQuery.YearFrom/YearTo/IgdbLinked`; `GameListItem.IgdbId`; `ListReleaseYearCountsAsync`. Files: `Core/Library/GameQuery.cs`, `GameListItem.cs`, `ILibraryRepositories.cs`, `Data/Repositories/LibraryRepositories.cs`. Tests: `GameQueryTests` (filters, inclusive bounds, igdb 0/null excluded, year→title tie in both directions, counts per system). Done when: tests pass; Library Year sort ties by title.
- [ ] **2. Timeline and IGDB page APIs**: T7–T9, `igdbId` in list DTO. Files: `TimelineEndpoints.cs` (new), `AppHost.cs`, `LibraryEndpoints.cs`, `IgdbClient.cs`, `HttpClients.cs`. Tests: `TimelineEndpointsTests` (new: every T7–T9 status, cache hit skips IGDB), `IgdbClientTests` (page URL body; non-igdb URL and missing url → both null; failure title), `EndpointRoleMatrixTests` (3 rows), `LibraryEndpointsTests` (`igdbId` present). Done when: tests pass.
- [ ] **3. Duplicates by IGDB id**: D1–D3, migration. Files: migration 0028, `IReportRepository.cs`, `ReportRepository.cs`, `ReportEndpoints.cs`. Tests: `DuplicateQueryTests` (igdb groups, 0/null ignored, dismissal per match), `SchemaMigrationTests`, `ReportEndpointsTests` (match validation, both dismiss bodies and their errors). Done when: title behaviour unchanged by default.
- [ ] **4. Developers discovery and name lookup**: C2 (API side), P4. Files: discovery core/data/api files, `AuditActions.cs`. Tests: `DiscoveryRepositoryTests` (developers groups, `FindKeyAsync` case/space-insensitive), `DiscoveryEndpointsTests` (`developers`, `key?name=` 200/404/400), `DiscoveryLogoEndpointsTests` (developer logo audited), `EndpointRoleMatrixTests`. Done when: `/api/discovery/developers` works like publishers.
- [ ] **5. ROM Zip Config count and report totals**: Z1, R1. Files: `AdminEndpoints.cs`, `routes/admin/rom-storage/+page.svelte`, the four report pages, `types.ts`. Tests: `RomStorageEndpointsTests` (`gameCount`), `routes/admin/rom-storage-page.test.ts`, `routes/reports/reports-pages.test.ts` (count lines, singular). Done when: Games column and four count lines render.
- [ ] **6. Duplicates page Match by**: D4. Files: `routes/reports/duplicates/+page.svelte`, `types.ts`. Tests: `reports-pages.test.ts` (the `Duplicates (RP9)` list URL becomes `?match=igdb&page=1&pageSize=25`; the empty-state test at ~line 401 expects "No games share an IGDB ID." and a new one switches to Title and expects "No duplicate games found."; dismiss sends `igdbId` for IGDB groups). Done when: switching to Title reproduces today's list.
- [ ] **7. Collections Developers tab and name links**: C1, P3. Files: `lib/discovery.ts`, `routes/collections/+page.svelte`, `DiscoveryGroups.svelte`, `DiscoveryGames.svelte`, `LogoModal.svelte`. Tests: `lib/discovery.test.ts`, `routes/collections/collections-page.test.ts`, `lib/components/DiscoveryGroups.test.ts`, `DiscoveryGames.test.ts`, `LogoModal.test.ts` (developer strings). Done when: `?tab=developers` and `?name=` resolve as specified.
- [ ] **8. Lookup Info footer and auto-select**: M1, M2. Files: `IgdbLookup.svelte`, `GameAdminModal.svelte`, `lib/gameEdit.ts`. Tests: `lib/gameEdit.test.ts` (all enabled ticked, title included); `lib/components/IgdbLookup.test.ts`: tests that click Use this match / Use this match and save / Not on IGDB call `component.useMatch(save)` / `component.notOnIgdb()` from `render()`'s instance instead and assert `chosen`/`notConfigured` via bound props; `routes/games/game-admin.test.ts` (new: the three buttons inside `.modal-footer` with their classes, only on Lookup Info, and clicking them applies/saves). Done when: buttons only in the footer.
- [ ] **9. Game page top row and links**: P1, P2. Files: `routes/games/[id]/+page.svelte`. Tests: `routes/games/game-page.test.ts` (the "action row … (B3)" test expects Download ZIP, Download ROM, Collections, Manage; the GP2 Google test still finds the link; new top-row order test with View on IGDB shown/hidden; P2 link hrefs, Year link only when linked). Done when: tests pass.
- [ ] **10. Description clamp and cover card**: P5, P6. Files: `routes/games/[id]/+page.svelte`, `GameImages.svelte`. Tests: `game-page.test.ts` (toggle appears when overflowing, aria attributes, resets on game change), `routes/games/game-images.test.ts` (cover card classes). Done when: tests pass.
- [ ] **11. Timeline state library**: T3, T4 math. Files: `lib/timeline.ts` (new). Tests: `lib/timeline.test.ts` (new: parse/clamp/swap, URL omissions, every T4 worked example, disabled states). Done when: tests pass.
- [ ] **12. Timeline page and navbar**: T1, T4–T6. Files: `routes/timeline/+page.svelte` (new), `Navbar.svelte`, `icons.ts`, `types.ts`. Tests: `routes/timeline/timeline-page.test.ts` (new: strip, tick press, table, IGDB link shown/hidden, both empty states), `lib/components/Navbar.test.ts` (link order). Done when: `timeline-page.test.ts` and `Navbar.test.ts` pass; the Timeline link sits between Collections and Reports.
- [ ] **13. Docs and e2e**: the doc sections in Changes; `tests/e2e/specs/mvp.spec.ts`: the Duplicates step at ~line 290 expects "No games share an IGDB ID.", then selects Title and expects "No duplicate games found."; new steps: link one seeded game through the game PATCH API (`igdbId: 1`, a `releaseYear`), then Timeline shows its year tick and the game in the table and passes `expectAccessible`, Developers tab lists a developer; the Emulators steps (~664, ~2064) still find the button by name. Done when: `pwsh scripts/test-all.ps1` and `pwsh scripts/test-e2e.ps1` pass.

## Decisions
- 2026-11-25 How does "View on IGDB" get a URL with only an id stored? → Server redirect via IGDB API, cached; hidden when IGDB isn't configured (user)
- 2026-11-25 What do metadata links open? → The group itself, via a name→key lookup (user)
- 2026-11-25 How do Duplicates use IGDB ids? → A Match by filter, IGDB ID default in the UI, Title kept; dismissals per match type (user)
- 2026-11-25 Timeline zoom model? → Buttons and URL range, no wheel/drag (user)
- 2026-11-25 Developer logos? → Same as publishers/genres, no starter art (user)
- 2026-11-25 Footer button colours? → `btn-success` for both match buttons, `btn-outline-warning` for Not on IGDB (user)
- 2026-11-25 Timeline System filter? → Yes, `?system=` (user)
- 2026-11-25 Year sort ties? → By title, then id, everywhere (user)
- 2026-11-25 Review round 1: Timeline history pushes like the Library (not Collections); zoom math pinned; FilterDropdown has no id; Developers text and components pinned; e2e/vitest empty-state assertions named; T7 result cases pinned and not-found cached; IgdbLookup interface pinned; icons named; data range per system; G1 tie ascending; Year links only for IGDB-linked games; `IgdbTokenCache` lives in `HttpClients.cs`; steps 9 and 10 split (reviewer)
- 2026-11-25 Review round 2: P3 uses `goto(…, { replace: true })`; step 12 Done when made checkable; Timeline e2e links a game via PATCH first (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-25-release-timeline-polish.md
```
Starts the spec, or resumes it at the first unticked step.
