---
title: DLC support
status: ready # draft | ready | in-progress | completed
created: 2026-11-20
---

# DLC support

## Summary
Switch DLC is already ingested as a release of its base game flagged `DLC` (titledb T4, `TitleKind.Dlc`) and stored by the system's storage rule (Switch: beside the ZIP, E1), exactly like updates. It is still mixed into the Game page's Releases tab, invisible in the library list and unsearchable. This spec gives DLC its own **DLC** tab on the Game page (as game-updates B9–B10 did for updates), a DLC badge in the library list, excludes DLC from the Multiple Releases report's release count, and makes DLC **and update** names searchable from Global Search and the Library search (one shared query), labelled like today's Developer/Publisher/Genre matches. It is generic: any system whose identification flags a release `DLC` gets it; no handler property, no storage or ingest change.

## Out of scope
- Any ingest, Review, storage or info.json change. DLC already follows each system's ZIP-embedding / beside-ZIP rule (`ISystemHandler.EmbedRomsInZip`, ROM Zip Config) and is already identified by titledb; nothing here changes where or how it is stored.
- A version field or version ordering for DLC (their `[v<N>]` tags aren't dotted versions; B3 orders by name).
- A Review "this file is DLC" toggle (DLC is identified by title id only, as today).
- Separate DLC/update rows or a DLC section in Global Search results (B8: they return their game's row).
- A DLC column or sort in the Multiple Releases report, or DLC counts in the Storage report (`SystemSize`/`GameSize` keep `UpdateCount` only).
- Changing the Library list's Releases badge (`romCount` still counts every release, as today).
- FULLTEXT search (the existing `LIKE` shortcut in `LibraryRepository.Where` stays).
- Downloading all DLC at once / bundling DLC into the game ZIP download.

## Behaviour

### Game page
- **B1 DLC.** A *DLC* release is a rom whose `flags` contains `DLC` (ordinal). New `isDlc` in `src/lib/releases.ts`. A rom flagged both `Update` and `DLC` (not produced by ingest; only a hand-edited info.json) counts as an update only, so every rom lands in exactly one tab.
- **B2 Tabs.** Tabs become `Releases (<n>)`, `Updates (<m>)`, `DLC (<d>)`, `Images (<k>)`, `Saves (<s>)`, `Zip Contents`. The DLC tab is shown only when `d > 0`; `?tab=dlc` on a game without DLC shows Releases (as an unknown tab does, game-updates B9). `n` counts roms that are neither update nor DLC. Icon: a new `dlc: 'bi bi-puzzle'` entry in `src/lib/components/icons.ts` (next to `update`).
- **B3 DLC order.** Pure function `compareDlc` in `src/lib/releases.ts`: `fileName` compared with `localeCompare(…, undefined, { sensitivity: 'base', numeric: true })`, ties by ordinal `fileName`. (The file name starts with the DLC's titledb name, so this is "by name".)
- **B4 One panel.** As game-updates B10: the existing `#releases-panel` renders the Releases, Updates or DLC list (`shownRoms`); its `aria-labelledby` is the active tab's id; the DLC tab button's `aria-controls` is `releases-panel`; the visually-hidden heading reads `DLC`; **Expand all** works on the DLC tab. Each card is the existing release card (download, play when the system plays, note, External badge, `rom-manager` actions); no version badge for DLC.
- **B5 Header download and empty text.** The header **Download ROM** lists the releases (neither update nor DLC); a game with none offers its updates, and with no updates either, its DLC. The Releases tab of a game with no releases says "No releases other than updates and DLC." (replaces "No releases other than updates.").
- **B6 Unchanged.** Primary release ranking (`PrimaryRelease`, already ranks DLC as an add-on), downloads, play, integrity, collections, bundles and storage treat DLC exactly as today.

### Library list
- **B7 DLC badge.** `GameListItem` gains `int DlcCount` (roms whose `flags` JSON contains `"DLC"` and not `"Update"`, per B1; same subquery shape as `UpdateCount`); the list API item gains `dlcCount`. `GameResults.svelte` gets a `dlcBadge(game, extra)` snippet (new), a copy of `updatesBadge` with the `dlc` icon, `game.dlcCount` and tooltip / visually-hidden text `` `${formatCount(n)} DLC` `` (same text for 1 and many), shown only when `dlcCount > 0`; it is rendered right after each of the two `{@render updatesBadge(…)}` calls with the same `extra` (`''` in the grid's badge row, `'ms-1'` in the list's title cell). Discovery and collection lists use `GameResults`, so they get it too.

### Search (Global Search and Library)
- **B8 Matching.** In `LibraryRepository.Where`, each search word's clause gains `OR EXISTS (SELECT 1 FROM roms r WHERE r.game_id = g.id AND (JSON_CONTAINS(r.flags, '"DLC"') OR JSON_CONTAINS(r.flags, '"Update"')) AND r.file_name LIKE @w<i>)` (`roms.file_name` takes the table default `utf8mb4_unicode_ci` from `0003_roms.sql`, so it compares like titles; no `COLLATE` needed). Every word must still match somewhere in the game (AND across words, as today). This applies to every `/api/games?q=` caller: Global Search, the Library page, Discovery, collections, and the game pickers `GameTypeahead.svelte` (move/merge/attach) and `GameAdminModal.svelte` (intended: one shared query).
- **B9 Relevance.** `RelevanceScore` adds per word `+15` when the word is in a DLC/update file name of the game (same EXISTS), below developer/publisher (20), above genre (10). Exact-title (100) and title-prefix (60) are unchanged.
- **B10 Match label.** `SearchMatch.Find` gains a parameter `IReadOnlyList<AddOnName> addOns` (`record AddOnName(string Field, string Name)`, `Field` is `dlc` or `update`). Unchanged first rule: null when any word is in the title/display title. Then developer, publisher, genre (unchanged order), then the first add-on (DLC before updates, each by ordinal file name) whose **name** contains a word → `SearchMatch("dlc" | "update", name)`. The name is `ReleaseName.Of(fileName)`: new `Core/Naming/ReleaseName.Of`: drop the extension (`Path.GetFileNameWithoutExtension`), then repeatedly remove a trailing `\s*\[[^\]]*\]` group, then trim (parentheses are kept: they can be part of the DLC's name, and the fallback name `<base> (DLC)` keeps its marker); an empty result falls back to the stem. `Orbit Expedition - Star Pack [0100000000003002][v0].nsp` → `Orbit Expedition - Star Pack`; `DREDGE [0100ABCD12340800][v0][1.0.1].nsp` → `DREDGE`. Matching uses the existing `Contains` (case- and accent-insensitive). The words are matched against the full file name in SQL but the stripped name in `Find`; a word that hits only a tag (e.g. a title id) therefore filters the game in but gives no label (null), which is acceptable.
- **B11 Add-on names in the list query.** `ListAsync` selects the add-on file names only when `words.Count > 0`: `(SELECT JSON_ARRAYAGG(JSON_OBJECT('f', IF(JSON_CONTAINS(r.flags, '"Update"'), 'update', 'dlc'), 'n', r.file_name)) FROM roms r WHERE r.game_id = g.id AND (JSON_CONTAINS(r.flags, '"DLC"') OR JSON_CONTAINS(r.flags, '"Update"'))) AS AddOns` (else `NULL AS AddOns`), parsed in C# with `System.Text.Json` (a game with no add-ons gives SQL `NULL` → an empty list), then sorted per B10 before `Find`. A rom flagged both counts as `update` (B1).
- **B12 API.** `matchedOn.field` can now be `developer`, `publisher`, `genre`, `dlc` or `update` (`types.ts` union widened). No other contract change.
- **B13 Global Search UI.** `matchLabels` gains `dlc: 'DLC'`, `update: 'Update'`, so the row shows e.g. `DLC: Orbit Expedition - Star Pack`. Selecting a game row (click or Enter) whose `matchedOn.field` is `dlc` opens `/games/<id>?tab=dlc`; `update` opens `?tab=updates`; any other opens the game page as today. Implementation: `choose(entry, tab?: 'dlc' | 'updates')` appends `?tab=<tab>` when given; the game row's `onclick` and the input's Enter handler (today `else void choose(entry)`) pass the tab from `matchedOn.field` for game entries; `play()` (the PSP path) keeps calling `choose({ kind: 'game', item: game })` without it, so quick play always opens the plain game page, as today.

### Reports
- **B14 Multiple Releases.** `ReportRepository`'s shared `GameReleases` query counts roms flagged neither `Update` nor `DLC` as `ReleaseCount`, so a base game with one DLC is no longer a "multiple releases" game. Both users of that query change: the report list and `MaxReleaseCountAsync` (the `maxReleases` that fills the page's Minimum releases dropdown). `IReportRepository` and `MultipleReleaseItem` docs updated; the `UpdateCount` column is unchanged.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Naming/ReleaseName.cs` (new) | B10 name from file name. |
| Core | `src/RnRomManager.Core/Library/SearchMatch.cs` | B10 add-on fields and `AddOnName` record. |
| Core | `src/RnRomManager.Core/Library/GameListItem.cs` | B7 `int DlcCount = 0` appended as the **last** parameter (after `SaveCount`), so the existing optional parameters keep their positions; the only constructor call is `LibraryRepository.ListAsync`. |
| Core | `src/RnRomManager.Core/Reports/IReportRepository.cs` | B14 doc comments. |
| Data | `src/RnRomManager.Data/Repositories/LibraryRepositories.cs` | B7 `DlcCount` subquery, B8 where clause, B9 relevance, B11 add-on names. |
| Data | `src/RnRomManager.Data/Repositories/ReportRepository.cs` | B14 release count excludes `DLC`. |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | B7 `dlcCount` in the list item; B12 no code change beyond passing the field. |
| Web | `src/RnRomManager.Web/src/lib/releases.ts` | B1 `isDlc`, B3 `compareDlc`. |
| Web | `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte` | B2, B4, B5 DLC tab. |
| Web | `src/RnRomManager.Web/src/lib/components/icons.ts` | B2 `dlc` icon. |
| Web | `src/RnRomManager.Web/src/lib/components/GameResults.svelte` | B7 badge. |
| Web | `src/RnRomManager.Web/src/lib/components/GlobalSearch.svelte` | B13 labels and tab navigation. |
| Web | `src/RnRomManager.Web/src/lib/api/types.ts` | B7 `dlcCount`, B12 `matchedOn.field` union. |
| Docs | `docs/features.md` | Global Search (≈ line 26: DLC/update names match, the `DLC:`/`Update:` label, opening the tab), Library search box (≈ 68), library list badge (≈ 63), Game page tabs (≈ 125–129), titledb matching (≈ 210: DLC shown on its own tab), Multiple Releases (≈ 298: releases are ROMs flagged neither `Update` nor `DLC`). |
| Docs | `docs/adding-a-system.md` | ≈ line 70: the DLC tab rule beside the Updates tab rule. |
| E2E | `tests/e2e/specs/mvp.spec.ts` | ≈ 289: the Switch game no longer counts its DLC as a release in Multiple Releases; ≈ 884: DLC on its own tab. |

No migration, config key or info.json change. The `/api/games` item gains `dlcCount` (additive) and `matchedOn.field` gains two values (B12); the Multiple Releases report's counts change for games with DLC (B14). README unchanged (no storage, format, DAT or play change).

## Risks
- **Search performance.** B8/B9 add a correlated `EXISTS` over `roms` per word to a query that already full-scans (`LIKE '%w%'`). `roms.game_id` is indexed, so the cost is per game row; acceptable at the library sizes the existing shortcut comment accepts. Extend that `shortcut:` comment to mention the add-on names.
- **Search behaviour change everywhere.** Library, Discovery and collection searches also match add-on names (decided). Update names repeat the game title, so they rarely add hits; DLC names can add games the user didn't expect, but the label (Global Search) explains them.
- **Report change.** Games with only base + DLC drop out of Multiple Releases (decided). `ReportEndpointsTests`/`MultipleReleasesQueryTests` fixtures that rely on DLC counting change in step 2; the e2e expectations (≈ 289) change in step 5 and run in Finish.

## Steps
- [ ] **1. Search: add-on names.** `ReleaseName`, `SearchMatch` add-on fields, `LibraryRepository` B8/B9/B11, B12 contract. Files: `Core/Naming/ReleaseName.cs`, `Core/Library/SearchMatch.cs`, `Data/Repositories/LibraryRepositories.cs`. Tests: new `ReleaseNameTests` (examples in B10, no tags, only tags → stem, `Orbit Expedition (DLC) [id][v0].xci` → `Orbit Expedition (DLC)`), `SearchMatchTests` (its `Find` helper passes the new `addOns` argument; dlc/update label, title still wins, developer before dlc, DLC before update, word only in a tag → null), `GameQueryTests` (integration: a word only in a DLC name finds the game; only in an update name finds it; two words split between title and DLC name find it; a word in a base release's file name doesn't; relevance: title hit ranks above DLC hit; `matchedOn` is `dlc` with the stripped name), `LibraryEndpointsTests` (`matchedOn.field == "dlc"` in the JSON). Done when: the listed tests pass, including a `GameQueryTests` case that a list without a search still returns `MatchedOn` null for a game with DLC; `shortcut:` comment updated.
- [ ] **2. Counts: DLC badge data and report.** B7 backend (`DlcCount`, `dlcCount`), B14. Files: `Core/Library/GameListItem.cs`, `Data/Repositories/LibraryRepositories.cs`, `Api/Endpoints/LibraryEndpoints.cs`, `Data/Repositories/ReportRepository.cs`, `Core/Reports/IReportRepository.cs`. Tests: `GameQueryTests` (`DlcCount` for a game with base + update + DLC is 1, `UpdateCount` 1; a rom flagged both counts in `UpdateCount` only), `LibraryEndpointsTests` (`dlcCount` in the list JSON), `MultipleReleasesQueryTests` and `ReportEndpointsTests` (base + DLC → not listed with min 2 and `maxReleases` 1; base + base + DLC → `ReleaseCount` 2). Done when: those tests pass and every `GameListItem` construction compiles with the new field.
- [ ] **3. Game page DLC tab.** B1–B5. Files: `src/lib/releases.ts`, `src/routes/games/[id]/+page.svelte`, `src/lib/components/icons.ts`. Tests: `releases.test.ts` (`isDlc`; `compareDlc` name order, numeric `Pack 2` before `Pack 10`, case-insensitive, tie by ordinal), `game-page.test.ts` (DLC tab shown only with DLC, count, `?tab=dlc` opens it, `?tab=dlc` without DLC → Releases, Releases count excludes DLC and updates, aria wiring, header download falls back releases → updates → DLC, empty-Releases text, a rom flagged both shows only under Updates; existing tests updated: the empty-Releases text assertion (≈ line 1100) to the B5 text, and the tab-icon list (≈ 1260) gains `['DLC (1)', 'bi bi-puzzle']` where its fixture has a DLC). Done when: `npm run lint`, `npm run check` and those vitest files pass.
- [ ] **4. List badge and Global Search labels.** B7 UI, B12 types, B13. Files: `src/lib/api/types.ts`, `src/lib/components/GameResults.svelte`, `src/lib/components/GlobalSearch.svelte`. Tests: `GameResults.test.ts` (badge only when `dlcCount > 0`, singular/plural text, grid and list), fixtures typed `GameListItem` gain `dlcCount: 0` (`GameResults.test.ts`, `DiscoveryGames.test.ts`, `SystemGames.test.ts`, `GlobalSearch.test.ts`) so `npm run check` passes, and `DiscoveryGames.test.ts` / `SystemGames.test.ts` run with the others; `GlobalSearch.test.ts` (`DLC: <name>` and `Update: <name>` labels; Enter/click on a `dlc` match goes to `?tab=dlc`, `update` to `?tab=updates`, a developer match to the plain game page). Done when: lint, check and those vitest files pass.
- [ ] **5. Docs and e2e.** Doc rule for B1–B14; e2e expectations. Files: `docs/features.md`, `docs/adding-a-system.md`, `tests/e2e/specs/mvp.spec.ts`. Tests: e2e only (runs in Finish): Multiple Releases (≈ 289): Orbit Expedition (base + DLC) drops out, but Orbit Blocks (World, Japan, Rev 1 in `IngestSeeder`) still qualifies, so assert the table shows the `Orbit Blocks` link and no `Orbit Expedition` link, and update the comment; G7 (≈ 884–893): `Releases (1)` selected, `DLC (1)` tab present, `External` count 1 on Releases, the header shows one **Download ROM** button with no dropdown (the base release file, byte for byte), and the DLC tab lists the DLC card; a Global Search for `Bonus Tracks` (the seed DLC is `Orbit Expedition - Bonus Tracks [0100000000011001][v0].nsp`) shows the Orbit Expedition row with `DLC: Orbit Expedition - Bonus Tracks`, and Enter opens `/games/<id>?tab=dlc` with the DLC tab selected. Done when: every behaviour above is described in its doc and the e2e file is updated.

## Decisions
- 2026-11-20 How should DLC appear on the Game page? → its own DLC tab, like Updates; DLC leaves Releases and its count (user).
- 2026-11-20 DLC order? → by name (file name, which starts with the titledb name); no version field (user).
- 2026-11-20 Global Search result shape? → the game's row with a `DLC: <name>` label, opening the DLC tab (user).
- 2026-11-20 Make updates searchable too? → yes, labelled `Update: <name>`, opening the Updates tab (user).
- 2026-11-20 Library list? → add a DLC badge; Releases badge unchanged (user).
- 2026-11-20 Multiple Releases report? → exclude DLC from the release count (user).
- 2026-11-20 Library search too? → yes, one shared `/api/games` query (user).
- 2026-11-20 Game pickers (move/merge/attach, admin modal) also match DLC/update names? → yes, they share `/api/games?q=` (follows the user's "same query" answer; reviewer R5).
- 2026-11-20 Quick play from a DLC/update match? → still opens the plain game page; only row selection opens the tab (reviewer R4).
- 2026-11-20 A rom flagged both `Update` and `DLC`? → counts and lists as an update only (reviewer R2).
- 2026-11-20 Spec review fact fixes: `DlcCount` appended last in `GameListItem`; Orbit Blocks keeps the e2e Multiple Releases table non-empty; `choose(entry, tab?)` and `dlcBadge` placement pinned; doc line refs added (code).
- 2026-11-20 Second review pass: `ReleaseName` strips only trailing `[…]` tags, not `(…)`; existing SPA tests/fixtures the steps must update named (code).
- 2026-11-20 Storage / ingest change needed? → none: DLC already follows each system's embed/beside-ZIP rule and titledb identification (code).

## Kickoff prompt
```text
/spec implement 2026-11-20-dlc-support.md
```
Starts the spec, or resumes it at the first unticked step.
