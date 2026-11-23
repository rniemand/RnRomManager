---
title: IGDB Metadata Lookup
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-23
---

# IGDB Metadata Lookup

## Summary
Admins can look a game up on IGDB from the **Edit Metadata** modal (a new **Lookup Info** tab), pick one of up to five matches and copy its title, year, developers, publishers, genres and description into the form. The chosen IGDB game id is stored in `info.json` (schema v8, `igdbId`). The Game page shows a banner for unlinked games, and a new Admin report, **Missing IGDB IDs**, lists them so they can be worked through. IGDB ("api-x" in the request) is reused because the app already has a working IGDB client (`IgdbClient`, `IgdbTokenCache`, `IgdbPlatforms`) with a platform id for every registered system, so one resolver covers all systems and no resolver field is needed.

## Out of scope
- Any other metadata source, and a `source` / resolver field in `info.json` (D: IGDB covers every system).
- Automatic or bulk matching (a job that links games without an Admin choosing).
- Fetching a game by its stored id; the lookup always searches by title (the linked match is only badged).
- A "Next missing IGDB ID" shortcut or hotkey like Missing Metadata's.
- Showing the IGDB id on the Game page's information table (it's visible through **View info.json**).
- Images: **Suggest Images** stays as it is.

## Behaviour

### info.json and the database
- **J1** `info.json` schema version **8** adds a game field `igdbId` (int?): `null` = not linked, `0` = an Admin marked the game **Not on IGDB**, a positive number = the IGDB game id. Every write includes the key. `InfoJsonReader.CurrentVersion` becomes `8`.
- **J2** `InfoJsonUpgraderV7` (new, `FromVersion => 7`) sets `document["igdbId"] = null`, as `InfoJsonUpgraderV1` adds `rating`. Older files are upgraded in memory and rewritten in v8 on their next normal write (no bulk rewrite). An app that only knows v7 treats a v8 file as `InfoJsonNewerSchema` (existing behaviour).
- **J3** `InfoJsonGame.IgdbId` is a plain `int? IgdbId { get; init; }` (not `required`, no `JsonIgnore`), so existing `new InfoJsonGame { … }` sites need no change and new games (ingest, **Move release** to a new game) start at `null`. `InfoJsonValidator` rejects a negative `igdbId` with the message `igdbId must be null, 0 or positive` (the validator's lowercase, no-full-stop style, next to its `rating must be null or 1-5` check).
- **J4** Migration `0027_game_igdb_id.sql` (new): `ALTER TABLE games ADD igdb_id INT NULL;`. Existing rows stay `NULL`, which is correct: no existing `info.json` has an id. The mirror (`GameRow.IgdbId`, set from `Info.IgdbId` in `LibraryRows`, the `GameRepository.Select` and the `INSERT INTO games` in `LibraryRepositories.cs`) keeps it in step from then on.
- **J5** **Merge** treats `igdbId` like the other metadata it merges: the target keeps its own id, and when that is `null` it takes the source's (`IgdbId = into.IgdbId ?? from.IgdbId`, next to `ReleaseYear = into.ReleaseYear ?? from.ReleaseYear` in `LibraryEditor`). A source marked Not on IGDB (`0`) fills a `null` target the same way. **Move release** to an existing game keeps that game's id.

### Saving the id
- **P1** `PATCH /api/games/{id}` (Admin, existing) accepts an optional `igdbId`: `null`, `0` or an integer 1–2147483647. Anything else → `400 ValidationFailed` keyed `igdbId`: `Must be null, 0 or a positive whole number.` Left out = unchanged (as `releaseYear`). `GamePatch` gets `SetIgdbId` / `IgdbId`, applied in `LibraryEditor.Patched`, so embedded and external games (`PatchExternalAsync`) both work. Job lock, `409 JobRunning` and ZIP rewrite are as for every metadata edit.
- **P2** `ChangedFields` adds `igdbId`, so the `GameUpdated` audit row lists it when it changed.
- **P3** The game detail (`GET /api/games/{id}` and every route returning it) adds `igdbId` (int?) and `igdbConfigured` (bool), for every signed-in user. Every route that returns the detail goes through `DetailAsync`. It computes the flag as `SourcesOptions.IgdbConfigured(ctx.RequestServices.GetRequiredService<IOptions<SourcesOptions>>().Value)` (the same rule `IgdbClient.Configured` uses) and passes it to `ToDetail`, its only caller. Reading the options directly keeps step 2 independent of step 3's interface, and a test substitute of `IImageSuggestions` / `IGameInfoLookup` doesn't change it.

### Lookup API
- **L1** `GET /api/games/{id}/igdb-matches?q=` (Admin) returns `{ matches: [{ igdbId, name, releaseYear, developers, publishers, genres, description, coverUrl, url }] }`, at most 5, in IGDB's relevance order. When `q` is left out it defaults to the game's display title cut to its first 100 characters. When `q` is present it is trimmed and must be 1–100 characters; a blank `q` (`q=` or whitespace) counts as present and fails. A `q` that `IgdbClient.SearchTitle` empties (for example only `"` or `\`) also fails. Both give `400 ValidationFailed` keyed `q`: `Must be 1–100 characters.` Read-only: no job lock, no audit row.
- **L2** Errors, in order: `400` for `q`; `404` for an unknown game; `409 IgdbNotConfigured` when `IGameInfoLookup.Configured` is false, title `Metadata lookup needs Sources:IgdbClientId and Sources:IgdbClientSecret.` (nothing is sent); `502 IgdbFailed` with the same titles `IgdbClient` already uses (`Unreachable`, `Refused`, `Busy`, `IGDB returned HTTP <code>.`, `Unreadable`). The code constants are the existing `LibraryEndpoints.IgdbNotConfigured` / `IgdbFailed`.
- **L3** The search is the image suggestions' search (`IgdbClient.SearchTitle`, the system's `IgdbPlatforms` id, then once more on every platform when that finds nothing; same token cache, 401 retry and status mapping) with these fields: `name,url,summary,first_release_date,release_dates.y,release_dates.platform,genres.name,involved_companies.developer,involved_companies.publisher,involved_companies.company.name,cover.image_id`. Shared code: `SearchAsync(title, platform, fields, ct)` returns the response's root `JsonElement` (cloned), throwing `IgdbFailure(Unreadable)` when the body isn't JSON or isn't an array. `ParseGames` (images) and a new `ParseMatches` (L4) both read that element. `SearchBody(title, platform)` keeps its signature and output for images. A new overload, `SearchBody(title, platform, fields, excludeVersions)`, builds the lookup body. With the platform it produces `search "<title>"; fields <fields>; where version_parent = null & platforms = (<p>); limit 5;`, and for the every-platform fallback `search "<title>"; fields <fields>; where version_parent = null; limit 5;`. `version_parent = null` drops editions such as "Gold Edition", following IGDB's documented "exclude versions" example. Per IGDB's v4 docs (checked 2026-11-23): every listed field exists on `games` / `release_dates` / `involved_companies` / `companies` / `genres` / `covers`, with these types:
  - `first_release_date` is a Unix timestamp in seconds.
  - `release_dates[].y` is an int and may be absent (TBD dates).
  - `release_dates[].platform` comes back as a plain platform id, since it isn't expanded further.
  - `involved_companies[].developer` / `.publisher` are booleans.
  - Expanded refs come back as nested objects.
  - `id` is always returned.
- **L4** Mapping per IGDB game:
  - `igdbId` = `id`; a game without a positive `id` or with an empty `name` is skipped.
  - `releaseYear` = the smallest `release_dates[].y` whose `platform` is the system's IGDB platform, else the UTC year of `first_release_date` (Unix seconds); `null` when neither exists or the year is outside 1970–2100 (the PATCH rule).
  - `developers` / `publishers` = `involved_companies[].company.name` where `developer` / `publisher` is true, trimmed, empty and >255-character names dropped, distinct (ordinal), in IGDB order, at most 20. `genres` = `genres[].name`, same rules.
  - `description` = `summary` trimmed, cut to 4000 characters; `null` when empty.
  - `coverUrl` = `https://images.igdb.com/igdb/image/upload/t_cover_small/<image_id>.jpg` when `cover.image_id` matches the existing alphanumeric check, else `null` (CSP already allows `images.igdb.com`).
  - `url` = IGDB's `url` when it starts with `https://www.igdb.com/`, else `null`.

### Edit Metadata modal (Admin)
- **M1** The **Edit Metadata** modal (`GameAdminModal`, `kind: 'edit'`) gets two Bootstrap `nav-tabs` at the top of the body: **Details** (the existing form, unchanged, selected by default) and **Lookup Info**. Use the markup of the game page's Releases tabs (`role="tablist"`, buttons with `role="tab"` / `aria-selected` / `aria-controls`, panels `role="tabpanel"`). The existing `<form>` (with its Enter-to-save `onkeydown`) wraps only the **Details** panel. The **Lookup Info** panel sits outside it, so nothing in it can trigger a save. The modal's **Edit Metadata** button saves from either tab (the whole form, including `igdbId`). Switching tabs keeps every value. When a save stops on client-side errors or a `400 ValidationFailed`, the modal switches to **Details** so the errors are visible.
- **M2** `AdminAction`'s edit case becomes `{ kind: 'edit'; lookup?: boolean }`. With `lookup: true` the modal opens on **Lookup Info** and runs the search at once, unless `game.igdbConfigured` is false (then M3's alert shows and no request is sent). Choosing the **Lookup Info** tab by hand doesn't search; the Admin clicks **Lookup Info**.
- **M3** **Lookup Info** tab, top to bottom (everything in `IgdbLookup.svelte`; every button is `type="button"`):
  - When `game.igdbConfigured` is false, the panel shows only an `alert-info`, without a request: `Metadata lookup needs an IGDB Client ID and secret. Set Sources:IgdbClientId and Sources:IgdbClientSecret and restart.` (the same wording pattern as Suggest Images). The same alert also shows on a `409 IgdbNotConfigured`.
  - Its own small `<form>` (separate from the Details form, not nested) holds a search input, label `Search IGDB for`, prefilled with the game's display title cut to its first 100 characters, `maxlength="100"`. It also holds a **Lookup Info** submit button (`btn-primary`, `<Icon name="search" />`), disabled while the trimmed input is empty. Submitting the form (Enter or the button) runs the search. Help text: `Searches IGDB on <system name>, then on every platform when that finds nothing.`
  - While searching: the button is disabled and the results area shows `Searching IGDB…` (`aria-live="polite"`). A new search aborts the previous one; closing the modal aborts it too.
  - Other errors: `ErrorAlert` with **Retry**. No matches: `No IGDB matches for “<q>”.`
  - Matches: a `list-group` of up to 5 buttons (`aria-pressed` for the selected one), each with the cover (`coverUrl`, 45×64, `alt=""`, omitted when null), the name, the year in brackets when set, the first developer, and a `Linked` badge (`text-bg-success`) when `igdbId` equals the form's current id.
  - Selecting a match shows a compare table below the list, inside a `table-responsive` wrapper (caption `Fields to copy from IGDB`, visually hidden). Its columns are a checkbox, **Field**, **Current** and **IGDB**, with one row each for Title, Release year, Developers, Publishers, Genres and Description. Lists are joined with `, `, the description is cut to 200 characters with `…`, and an empty value shows `—`. Each checkbox has the `aria-label` `Copy <Field>`. A row's checkbox is disabled when the IGDB value is empty or equals the current form value. Otherwise it starts ticked when the current form value is empty, except Title, which never starts ticked (renaming also renames the ZIP). "Equals" is defined as follows:
    - Title and Description compare trimmed and case-sensitive.
    - The year compares the trimmed form text with the IGDB number as a string.
    - Each list compares `toList(form.x)` with the IGDB array, ordinal and in order.
    - "Empty" is the same trimmed or `toList` value being empty. A link **View on IGDB** (`target="_blank" rel="noopener noreferrer"`) shows when `url` is set.
  - Under the table, **Use this match** (`btn-primary btn-sm`): copies the ticked fields into the form (lists one per line, as the form expects), sets the form's `igdbId` to the match's, switches to **Details** and shows the note below. With nothing ticked it only links the id.
  - At the bottom of the tab, **Not on IGDB** (`btn-outline-secondary btn-sm`): sets the form's `igdbId` to `0` and switches to **Details**.
- **M4** **Details** tab:
  - Before the `Press Enter to save…` hint, a `form-text` line says `IGDB: linked (#<id>)`, `IGDB: marked as not on IGDB` or `IGDB: not linked`. When the id isn't null, an **Unlink** link-button (`type="button"`) after it sets the id to `null`.
  - After **Use this match** or **Not on IGDB**, an `alert-info` at the top of Details says `IGDB values applied. Save to keep them.` It clears when the modal reopens.
  - Nothing is saved until the modal's **Edit Metadata** button.
  - In `GameAdminModal.svelte`, `showFieldErrors`'s field list gains `igdbId`. Its error shows as `invalid-feedback`-styled text under the IGDB line.
- **M5** `gameEdit.ts`: `GameEditForm` and `GamePatch` gain `igdbId: number | null`. `toForm` copies it from `GameDetail`, and `toPatch` always sends it. Two new pure helpers are exported and tested: `defaultCopyFields(form, match)` (M3's starting ticks and disabled rows, using M3's comparison) and `applyMatch(form, match, fields)` (returns the new form).

### Game page banner
- **N1** For an Admin, when `game.igdbConfigured` and `game.igdbId === null`, an `alert alert-info d-flex flex-wrap align-items-center gap-2 mb-3` (`role="status"`) sits directly below the **Game information** card (the `G1` key/value table): `This game isn't linked to IGDB.` and a **Find on IGDB** button (`btn btn-sm btn-outline-primary ms-auto`, `<Icon name="search" />`) that sets `action = { kind: 'edit', lookup: true }`. Hidden for Site users, for linked games, for games marked **Not on IGDB**, and when IGDB isn't configured. It goes away as soon as a save returns the updated detail.

### Missing IGDB IDs report (Admin)
- **R1** `GET /api/reports/missing-igdb-ids?system=&status=&q=&sort=&dir=&page=&pageSize=` (Admin): `status` is `missing` (default, `igdb_id IS NULL`) or `not-on-igdb` (`igdb_id = 0`); `system`, `q` (≤ 100 characters, a `LIKE` of the display title as Missing Metadata does), `sort` (`name` default, `system`, `year`), `dir`, `page`, `pageSize` validate exactly as `/missing-metadata` does, plus `status`: `Must be missing or not-on-igdb.` Returns the shared paging DTO with items `{ id, displayTitle, system, systemName, releaseYear }`.
- **R2** Tab **Missing IGDB IDs** in `reportTabs`, Admin-only, directly after **Missing Metadata** (before Activity), icon key `report-missing-igdb` → `bi bi-link-45deg`. The route `/reports/missing-igdb-ids` is added to the layout's `adminOnly` list (a Site user gets `NotAllowed`).
- **R3** The page copies `missing-metadata/+page.svelte`: filters **System**, **Show** (`Not linked` = `missing`, `Not on IGDB` = `not-on-igdb`) and **Title** (300 ms debounce), **Clear**; columns **Game** (link to `/games/<id>`), **System**, **Year** (`—` when null), all sortable; state in the URL (`?system=&status=&q=&sort=&dir=&page=`). Empty states: `Every game is linked to IGDB.` (status missing, no filters), `No games are marked as not on IGDB.` (status not-on-igdb, no filters), `No games match these filters.` otherwise. A note above the table when IGDB isn't configured is not needed (the report is DB-only).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Library/InfoJsonGame.cs` | `int? IgdbId` (J3) |
| Core | `src/RnRomManager.Core/Library/InfoJsonUpgraderV7.cs` (new) | v7→v8 adds `igdbId: null` (J2) |
| Core | `src/RnRomManager.Core/Library/InfoJsonReader.cs` | `CurrentVersion = 8` |
| Core | `src/RnRomManager.Core/Library/InfoJsonValidator.cs` | negative `igdbId` rule (J3) |
| Core | `src/RnRomManager.Core/Library/LibraryRows.cs` | `GameRow.IgdbId`, mapped from `Info.IgdbId` (J4) |
| Data | `src/RnRomManager.Data/Migrations/0027_game_igdb_id.sql` (new) | `games.igdb_id INT NULL` (J4) |
| Data | `src/RnRomManager.Data/Repositories/LibraryRepositories.cs` | `igdb_id AS IgdbId` in `GameRepository.Select`; `igdb_id` in the `INSERT INTO games` |
| Api | `src/RnRomManager.Api/AppHost.cs` | register `InfoJsonUpgraderV7` |
| Core | `src/RnRomManager.Core/Library/GamePatch.cs`, `LibraryEditor.cs` (`Patched`, `ChangedFields`, the merge's field fill) | `SetIgdbId` / `IgdbId` (P1, P2); merge fill (J5) |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | `ParsePatch` reads `igdbId`; `ToDetail` adds `igdbId`, `igdbConfigured` (P3); `GET /{id}/igdb-matches` (L1, L2) |
| Core | `src/RnRomManager.Core/Media/IgdbClient.cs` | `IGameInfoLookup` (new interface: `Configured`, `LookupAsync(title, systemId, ct)` → `GameInfoLookupResult(IReadOnlyList<GameInfoMatch>? Matches, string? FailedTitle)`), implemented by `IgdbClient` sharing the token and status code; a `SearchBody` overload with a field list; `SearchAsync` returns the cloned root `JsonElement`; `ParseMatches` (new) (L3, L4) |
| Api | `src/RnRomManager.Api/Http/HttpClients.cs` | `services.AddTransient<IGameInfoLookup>(sp => sp.GetRequiredService<IgdbClient>())` |
| Core | `src/RnRomManager.Core/Reports/IReportRepository.cs` | `ListMissingIgdbIdsAsync(systemId, MissingIgdbStatus status, q, MissingMetadataSort sort, descending, page, pageSize, ct)`, `MissingIgdbStatus { Missing, NotOnIgdb }`, `MissingIgdbItem(Id, DisplayTitle, SystemId, ReleaseYear)` (R1) |
| Data | `src/RnRomManager.Data/Repositories/ReportRepository.cs` | the query, copying `ListMissingMetadataAsync` |
| Api | `src/RnRomManager.Api/Endpoints/ReportEndpoints.cs` | `GET /missing-igdb-ids`, `AdminOnly` (R1) |
| Web | `src/RnRomManager.Web/src/lib/api/types.ts` | `GameDetail.igdbId`, `GameDetail.igdbConfigured`, `IgdbMatch`, `MissingIgdbItem` |
| Web | `src/RnRomManager.Web/src/lib/gameEdit.ts` | M5 |
| Web | `src/RnRomManager.Web/src/lib/components/IgdbLookup.svelte` (new) | the Lookup Info tab panel (M3); props `game`, `form`, `onapply(form)`, `autostart` |
| Web | `src/RnRomManager.Web/src/lib/components/GameAdminModal.svelte` | tabs, `lookup` flag, IGDB line, note (M1, M2, M4) |
| Web | `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte` | banner (N1) |
| Web | `src/RnRomManager.Web/src/lib/reports.ts`, `lib/components/icons.ts`, `routes/reports/+layout.svelte`, `routes/reports/missing-igdb-ids/+page.svelte` (new) | R2, R3 |
| Docs | `docs/zip-format.md` | Heading becomes `schema version 8`. The Game table's `schemaVersion` note becomes `8`, and it gains an `igdbId` row (int?, ✓: "`null` not linked, `0` Not on IGDB, else the IGDB game id; always written"). The Validation sentence adds "`igdbId` null or ≥ 0". The Versions table gets row `\| 8 \| 0.4.0 \| Adds igdbId (int?) to the game: the linked IGDB game id, 0 for Not on IGDB, null otherwise. The v7→v8 upgrader adds igdbId: null; migration 0027 adds games.igdb_id, filled on each game's next mirror. A v8 file is InfoJsonNewerSchema (read-only) for an app that only knows v7. \|` (field names in backticks) |
| Docs | `docs/features.md` | Game detail (banner, Lookup Info). Reports: every list in line ~305's paragraph gains Missing IGDB IDs (the tab order, the "first seven… Admins only" sentence, the per-tab icon list and the "every report's System filter" list), plus a **Missing IGDB IDs** bullet. Global search (line ~28: "Missing Metadata, Missing IGDB IDs and Activity for Admins only"). Report APIs (line ~491's Admin-only list gains `/missing-igdb-ids`). Edit the library through the API (`igdbId`, `igdb-matches`, detail fields) |
| Docs | `docs/configuration.md` | the `Sources:IgdbClientId` row (line ~22) becomes "for **Suggest Images** on the Game page's Images tab and **Lookup Info** in Edit Metadata" |
| Docs | `docs/development.md` | "Reports and curation helpers" (line ~143): the report list gains Missing IGDB IDs (`ReportRepository.ListMissingIgdbIdsAsync`) |
| Docs | `docs/codemap.md` | regenerated each step |

API contract changes are additive (new optional PATCH field, new detail fields, new routes). No config keys. No README systems-table change (no storage, format, DAT or browser-play change per system).

## Risks
- **Schema bump**: a v8 ZIP is read-only for an older app build (`InfoJsonNewerSchema`). Same as every past bump; only matters when rolling back.
- **DB restore**: restoring a backup taken before migration 0027 leaves `igdb_id` `NULL` until a game's ZIP is mirrored again, so a few linked games can show in the report until then. The ZIP stays the source of truth; no data is lost.
- **IGDB quota** (4 requests/s, at most 8 open requests): lookups are one user click each (≤ 2 searches), so this isn't reachable in normal use; `429` already maps to `IGDB is busy; try again in a moment.`
- **Description overwrite**: IGDB summaries are English and long; Description is never pre-ticked when the current one is set (M3), so an Admin's own text isn't replaced by accident.
- **Security**: `q` goes through `SearchTitle` (strips `"` and `\`), like Suggest Images; `url` and `coverUrl` are only passed through when they match IGDB's hosts (L4), so the SPA never renders an arbitrary link.

## Steps
- [x] **1. info.json v8 and the DB mirror**: J1–J5. Files: `InfoJsonGame.cs`, `InfoJsonUpgraderV7.cs` (new), `InfoJsonReader.cs`, `InfoJsonValidator.cs`, `LibraryRows.cs`, `0027_game_igdb_id.sql` (new), `LibraryRepositories.cs`, `AppHost.cs`, `docs/zip-format.md`. Tests: `InfoJsonTests` (v7 file upgrades to `igdbId: null`; round-trip of `null`, `0`, `1234`; negative rejected; the reader helper's upgrader list gains `InfoJsonUpgraderV7`, and line 67/809's `SchemaVersion = 7` becomes 8), `LibraryZipTests` and `LibraryZipCompressionTests` (upgrader lists), `LibraryMirrorTests` (`igdb_id` mirrored), `SchemaMigrationTests` (migration list gains `0027_game_igdb_id`). Done when: a v7 ZIP reads with `IgdbId == null` and is written back as v8 with `"igdbId": null`; `games.igdb_id` mirrors the file; the listed tests pass; zip-format.md documents v8.
- [x] **2. Save the id and return it**: P1–P3, and J5's merge fill. Files: `GamePatch.cs`, `LibraryEditor.cs`, `LibraryEndpoints.cs` (`ParsePatch`, `ToDetail`), `docs/features.md` (Edit the library through the API). Tests:
  - `GamePatchParsingTests`: null, 0, positive, negative, string, fraction, too big.
  - `LibraryEditTests`: PATCH sets and clears `igdbId`; the ZIP and the DB are both updated; `GameUpdated` details list `igdbId`; the detail has `igdbId` and `igdbConfigured: false` (the test host sets no IGDB keys).
  - `ExternalLibraryTests.TitleEdit.cs` (new test `PatchIgdbIdOnExternalGame`): the ZIP's `info.json` `igdbId` and `games.igdb_id` both update for an external game.
  - `LibraryMoveMergeTests`: a merge keeps the target's id; a `null` target takes the source's id (or `0`); moving a release to a new game gives it `igdbId: null`.

  Done when: those pass and a PATCH without `igdbId` leaves it unchanged.
- [x] **3. IGDB metadata lookup in Core**: L3, L4. Files: `IgdbClient.cs`, `HttpClients.cs`. Tests: `IgdbClientTests` (`SearchBody` overload output for both L3 bodies, including `version_parent = null`; the images body unchanged; fallback to every platform; a release date without `y` ignored; each L4 mapping rule incl. platform year vs `first_release_date`, out-of-range year, developer/publisher split, 20-item cap, 4000-char description, bad `image_id`, foreign `url`; failures map to the existing titles), `HttpClientsTests` (`IGameInfoLookup` resolves). Done when: those pass and the Suggest Images tests in `IgdbClientTests` still pass unchanged.
- [x] **4. Lookup endpoint**: L1, L2. Files: `LibraryEndpoints.cs`, `docs/features.md` (Edit the library through the API), `docs/configuration.md` (the `Sources:IgdbClientId` row). Tests: `GameInfoLookupEndpointsTests` (new, copying `ImageSuggestionEndpointsTests`' setup: `Substitute.For<IGameInfoLookup>()` registered through `ApiFactory.TestServices`, `Configured` true by default; cases: default `q` (and cut to 100 characters), `q` validation, 404, 409, 502, matches passthrough), `EndpointRoleMatrixTests` (row `GET /api/games/{id:guid}/igdb-matches`, Admin). Done when: those pass.
- [x] **5. Missing IGDB IDs report API**: R1. Files: `IReportRepository.cs`, `ReportRepository.cs`, `ReportEndpoints.cs`, `docs/features.md` (Report APIs). Tests: `MissingIgdbIdsQueryTests` (new: `missing` vs `not-on-igdb`, system and title filters, each sort, paging), `ReportEndpointsTests` (validation incl. `status`, Admin-only, DTO shape), `EndpointRoleMatrixTests` (row `GET /api/reports/missing-igdb-ids`, Admin), `docs/features.md` (Report APIs line ~491). Done when: those pass.
- [x] **6. Edit Metadata tabs and the id in the form**: M1, M2 (the `lookup` flag selects the tab; the Lookup Info panel stays empty until step 7), M4, M5. Files: `types.ts` (`GameDetail.igdbId`, `igdbConfigured`, `IgdbMatch`), `gameEdit.ts`, `GameAdminModal.svelte`. Every `GameDetail`-typed fixture gets `igdbId: null, igdbConfigured: false`. Those are in `routes/games/game-page.test.ts` (`function game`), `game-bundle.test.ts`, `game-collections.test.ts`, `game-images.test.ts`, `release-note.test.ts`, `lib/components/GameSaves.test.ts` and `lib/components/ZipBrowser.test.ts` (the files that name `GameDetail`); `npm run check` finds any others. `routes/games/game-admin.test.ts`'s untyped `const game = (romIds) => ({…})` (line ~74) also gains `igdbId: null, igdbConfigured: false`, since the new modal tests read them. No existing test asserts the exact `PATCH /api/games/g1` body, so sending `igdbId` breaks none. Tests:
  - `lib/gameEdit.test.ts`: the `form()` fixture gains `igdbId: null`; the "builds the PATCH body" expectation gains `igdbId: null`; new cases for `toForm`/`toPatch` carrying `igdbId`, `defaultCopyFields` (each M3 comparison rule) and `applyMatch`.
  - `routes/games/game-admin.test.ts`: tabs present and labelled; `lookup: true` selects Lookup Info; Save sends `igdbId`; the IGDB line and Unlink; a client-side error while on Lookup Info switches to Details. The existing Edit Metadata tests pass unchanged, since Details is the default tab.

  Done when: those pass with `npm run lint` and `npm run check`.
- [x] **7. Lookup Info search and matches**: M2 (auto-search), M3 up to and including the match list (selecting a match only marks it `aria-pressed`), **Not on IGDB**, and M4's `IGDB values applied…` note. Files: `IgdbLookup.svelte` (new), `GameAdminModal.svelte` (mount it in the Lookup Info panel; the note). Tests:
  - `lib/components/IgdbLookup.test.ts` (new): the not-configured alert without a request, also with `autostart`; the prefill cut to 100 characters; the button disabled when blank; Enter searches; a new search aborts the old one; the loading text; a 409 alert; an error with Retry; the empty text; the list with the Linked badge; Not on IGDB.
  - `routes/games/game-admin.test.ts`: `lookup: true` searches at once; Enter in the search input doesn't save; Not on IGDB switches to Details with the note and `IGDB: marked as not on IGDB`.

  Done when: those pass with lint and check.
- [x] **8. Compare table and Use this match**: the rest of M3 (compare table, View on IGDB, Use this match). Files: `IgdbLookup.svelte`, `docs/features.md` (Game detail: Lookup Info, all of M1–M4). Tests:
  - `lib/components/IgdbLookup.test.ts`: the table rows, starting ticks and disabled rows (via `defaultCopyFields`); the View on IGDB link only when `url` is set; Use this match with nothing ticked only links.
  - `routes/games/game-admin.test.ts`: Use this match fills Details and shows the note; Enter on a compare checkbox doesn't save; Save then sends the copied fields and `igdbId`.

  Done when: those pass with lint and check.
- [x] **9. Game page banner**: N1. Files: `routes/games/[id]/+page.svelte`, `docs/features.md` (Game detail). Tests: `routes/games/game-page.test.ts` (banner shown/hidden per N1; Find on IGDB opens the modal on Lookup Info). Done when: those pass with lint and check.
- [ ] **10. Report tab and list**: R2, plus R3's list without the System/Title filters and sorting: the **Show** filter, the table, paging, `?status=&page=` in the URL, and the empty states. Files: `reports.ts`, `icons.ts`, `routes/reports/+layout.svelte`, `routes/reports/missing-igdb-ids/+page.svelte` (new), `docs/features.md` (Reports line ~305, the new bullet, and Global search line ~28), `docs/development.md` (Reports and curation helpers). Tests:
  - `lib/reports.test.ts`: the Admin tab order now ends Missing Metadata, Missing IGDB IDs, Activity; the existing "adds Missing Metadata and Activity, last" expectation is updated.
  - `routes/reports/reports-pages.test.ts`: a new `describe('Missing IGDB IDs')` covering the Show filter, paging, empty states and Site user NotAllowed; the tab-icon table at line ~1147 gains `['Missing IGDB IDs', 'bi bi-link-45deg']`.
  - `lib/components/GlobalSearch.test.ts`: the Admin Reports-group expectations around line 630 gain Missing IGDB IDs, and the Site-user case doesn't list it.

  Done when: those pass with lint and check.
- [ ] **11. Report filters and sorting**: the rest of R3: **System** and **Title** filters, **Clear**, sortable columns, and the full `?system=&status=&q=&sort=&dir=&page=` URL state. Files: `routes/reports/missing-igdb-ids/+page.svelte`. Tests: `routes/reports/reports-pages.test.ts` (filters, the 300 ms title debounce, Clear, each sort, URL state round trip, `No games match these filters.`). Done when: those pass with lint and check.
- [ ] **12. e2e**: `tests/e2e/specs/mvp.spec.ts`.
  - In the existing Edit Metadata flow (~line 2453): assert the **Details** and **Lookup Info** tabs; click Lookup Info and see the not-configured alert straight away (e2e has no IGDB keys, so no search is needed); go back to Details and save as before.
  - A new block after the Missing Metadata one (~line 3042): open `/reports/missing-igdb-ids`, check the tab is current and a seeded game links to its page, and run `expectAccessible`.

  Done when: `pwsh scripts/test-e2e.ps1` passes.

## Decisions
- 2026-11-23 Which metadata API, and how is the id stored? → IGDB (already integrated, covers every system); `info.json` v8 `igdbId` int?, no resolver field (user)
- 2026-11-23 How do a match's values reach the form? → Compare table with a checkbox per field, pre-ticked where the current value is empty, Title never pre-ticked; nothing saves until Save (user)
- 2026-11-23 Who sees the banner and report? → Admins only; banner also hidden when IGDB isn't configured (user)
- 2026-11-23 Games that will never be on IGDB? → **Not on IGDB** stores `igdbId: 0`; banner hides; the report shows them only under Show → Not on IGDB (user)
- 2026-11-23 `required` or optional C# property? → Optional `int?`, always written, so no existing construction site changes (code)
- 2026-11-23 Review round 1:
  - Lookup panel outside the Details `<form>`.
  - Errors switch to Details.
  - Steps 6 and 7 split into four.
  - `GameDetail` fixtures named.
  - `q` capped at 100 and blank rejected; a manual tab switch doesn't search, and the not-configured alert comes from `igdbConfigured`.
  - Compare rules pinned; `showFieldErrors` moved to M4; role-matrix keys fixed; extra doc spots named; Versions row text pinned; external test named; tab markup and `table-responsive` named; `SearchAsync` sharing pinned.

  (reviewer)
- 2026-11-23 Exclude IGDB editions from the lookup search (`version_parent = null`)? → Yes; Suggest Images unchanged (user)
- 2026-11-23 Deep review: IGDB v4 field names and types checked against the docs (all exist); `igdbConfigured` wiring via `DetailAsync`; `GameDetail` fixture list corrected (adds `GameSaves`/`ZipBrowser`, drops `game-admin`/`game-title`); `GlobalSearch.test.ts` pinned; the Lookup panel and report page steps each split in two for the ~300-line rule (now 12 steps) (reviewer)
- 2026-11-23 Deep review 2: Merge fills a `null` target `igdbId` from the source like its other empty fields; the validator message follows house style; the applied note moves to step 7 with Not on IGDB; step 6's panel is empty (code)
- 2026-11-23 Deep review 3: `igdbConfigured` comes from `SourcesOptions.IgdbConfigured` so step 2 doesn't need step 3's interface; the 409 uses `IGameInfoLookup.Configured`; the test setup for both is pinned (code)
- 2026-11-23 Deep review 4: `game-admin.test.ts`'s untyped fixture gains the two fields; confirmed one `games` insert path and no exact PATCH-body assertion (code)
- 2026-11-23 Deep review 5: features.md Reports paragraph has four lists to update, not two; the other doc lines and callers were checked (code)
- 2026-11-23 Deep review 6: `configuration.md` (IGDB keys now also used by Lookup Info) and `development.md` (report list) added to the docs (code)
- 2026-11-23 Review round 2: the `IgdbClient` Changes row now matches L3; `lookup: true` doesn't search when IGDB isn't configured (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-23-igdb-metadata-lookup.md
```
Starts the spec, or resumes it at the first unticked step.
