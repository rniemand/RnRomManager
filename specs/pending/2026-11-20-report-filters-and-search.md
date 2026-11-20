---
title: Play progress, Emulators button, report search and system filters
status: ready
created: 2026-11-20
---

# Play progress, Emulators button, report search and system filters

## Summary
Polish for every signed-in user: the browser player always shows a progress bar while it downloads a ROM or disc (the server now says how big the download is), the game page gets an **Emulators** button for its system (and the modal title becomes "… Emulators"), Global Search also finds reports, and six reports use the Games page's System dropdown instead of a plain `<select>`. For Admins, the review queue's Attach tab can also attach a file to an existing game as a new **Release** (tagged from its file name, marked Unverified).

## Out of scope
- Progress for the BIOS and library-save downloads in the player, and for EmulatorJS's own core download.
- Per-system counts in the report System dropdowns (the Games page shows counts; reports don't).
- Library Size and Activity reports (they have no System select).
- Changing the "No games match" text in Global Search.
- Exact `Content-Length` for the on-the-fly cue/bin ZIP (an estimate header is sent instead, P2).
- Attach as **Disc** on systems without file sets: it stays a disc-system option, as today (review B16).
- Changing the `AmbiguousGroup` attach (`flag: "None"`).

## Behaviour

### Player download progress (P)
- **P1** `GET /api/games/{id}/roms/{romId}/download` for a ROM stored inside the library ZIP (`LibraryDownload.OpenReleaseAsync`'s `OpenEntry` branch, `ranges: false`) sends `Content-Length: <rom.SizeBytes>`. Implemented once in `LibraryDownload.Send`: when `!attachment.Ranges`, set `ctx.Response.ContentLength = attachment.Length` before returning `Results.Stream(...)`. This also gives the save download (`SaveEndpoints.cs:134`, `save.SizeBytes`) a `Content-Length`. Range-enabled attachments (external files, set files) are unchanged: `Results.Stream` already sends their length. If the entry turns out shorter than the recorded size, Kestrel aborts the response (today it ends as a truncated `200`); the player then shows its existing "Couldn't load the ROM." error.
- **P2** `GET /api/games/{id}/roms/{romId}/play-disc` for a cue set (the ZIP built on the fly in `PlayDisc.DiscResult`) adds the header `X-Content-Bytes: <cue.Length + Σ entries[i].Length>` (the sum of the cue's and tracks' lengths, in bytes, invariant culture) before the first write. It still sends no `Content-Length`. A standalone image keeps its `Content-Length` and gets no `X-Content-Bytes`.
- **P3** In `static/emulator/play.js`, `readContent(response)` uses `Content-Length` as the total if it is usable (`Content-Encoding` absent or `identity`, and a safe positive integer, as today); otherwise `X-Content-Bytes` if it is a safe positive integer (whatever the encoding); otherwise there is no total. With a total: the bar shows with `max = total`, `value = min(bytes, total)`, and the label `#downloaded` shows the percent `floor(min(bytes, total) * 100 / total)` (so it never passes 100%). Without a total: the bar still shows, **indeterminate** (`progress.removeAttribute('value')`), and the label shows the byte count as today. `show()` hides both again, as today. Applies to ROM downloads and disc downloads (`loadDisc`, which already calls `readContent`).

### Emulators button on the game page (E)
- **E1** `SystemEmulatorsModal`'s title becomes `{system.displayName} Emulators` (capital E). The body texts don't change.
- **E2** The game page (`routes/games/[id]/+page.svelte`) action row gets `<button type="button" class="btn btn-sm btn-outline-secondary"><Icon name="emulators" /> Emulators</button>` right after the **Collections** button (before **Next missing metadata** and **Manage**), for every signed-in user and every system. It opens `SystemEmulatorsModal` for the game's system; a system with no known emulators shows the modal's existing "There are no known emulators for …" alert. Closing the modal returns focus to the button, the way `SystemGames.svelte` does (`wasOpen` effect).
- **E3** `SystemEmulatorsModal`'s `system` prop type becomes `Pick<SystemInfo, 'id' | 'displayName'>` (it only reads those two). The game page passes `{ id: game.system, displayName: game.systemName }`; `SystemGames.svelte` keeps passing its `SystemInfo`.

### Reports in Global Search (S)
- **S1** The report tab list moves out of `routes/reports/+layout.svelte` into `src/lib/reports.ts` (new): `export function reportTabs(isAdmin: boolean): ReportTab[]` with `ReportTab = { href: string; label: string; icon: string }`, the same entries, order, labels, icons and `resolve(...)` hrefs as today (Missing Metadata and Activity only when `isAdmin`). The layout renders `reportTabs(auth.isAdmin)`; its `adminOnly` route check is unchanged.
- **S2** A report matches when its label, lower-cased, contains every whitespace-separated word of the trimmed, lower-cased query. It uses the same 2-character minimum and runs in `search()` (so report results appear together with the API results, after the 250 ms debounce). All matching reports show (at most 8 exist). Site users never get Missing Metadata or Activity. The report matches are state cleared wherever `collections` and `games` are cleared today (`oninput`, `choose`, `reset`), with `active` reset to 0, so no stale chip shows while the next query loads or below 2 characters.
- **S3** Matches show as a **Reports** group, **first** in the list (Reports → Collections → Games), with a header row `Reports` like the Collections one, and chips like the collection chips (`btn btn-sm btn-outline-secondary d-inline-flex align-items-center gap-1`, `<Icon name={tab.icon} />` then the label), each an `option` of the same listbox. Up/Down walk Reports, then Collections, then Games; option ids stay `global-search-results-<index>` over the combined list.
- **S4** Enter or a click on a report chip closes the search (as `choose` does) and navigates to the report's `href`. Shift+Enter on a report does nothing.
- **S5** The input's visible label and placeholder become `Search games, collections and reports`. "No games match" shows only when all three groups are empty (no text change). When both API calls fail, the error shows and report matches still show.

### System filter on reports (F)
- **F1** The Games page's system option list moves into `src/lib/systemFilter.ts` (new): `export function systemFilterOptions(systems: SystemInfo[], selected: string | null): FilterOption[]` — systems with `romCount > 0` plus the selected one, mapped to `{ value: s.id, label: s.displayName, badge: { system: s.id, name: s.shortName }, search: [s.shortName] }` (exactly `routes/+page.svelte`'s `systemOptions` today). `routes/+page.svelte` uses it; its behaviour doesn't change.
- **F2** Newly Added, Missing Images, Duplicates, Multiple Releases, Games With Hacks and Missing Metadata replace their `<label>` + `<select>` System control with, in the same grid column:
  `<FilterDropdown label="System" options={systemFilterOptions(systems, current || null)} value={current || null} all={{ label: 'All systems', sum: false }} filterPlaceholder="Filter systems…" onselect={(v) => <the page's existing change logic>(v ?? '')} />` (no `counts`), where `current` is the page's system value (`''` = all). What a choice does is unchanged: URL state and page reset to 1 where the page does that today (Newly Added `choose`, Multiple Releases / Games With Hacks / Missing Metadata `show({...view, system, page: 1})`), component state and page reset in Missing Images and Duplicates. Missing Metadata's **Clear** still resets it.
  The column keeps no separate `<label>`: the toggle's own text ("System" / "System: Game Boy") is the caption, and its `aria-label` names it. The row's `align-items-end` keeps the button bottom-aligned with labelled siblings (Minimum releases, Missing, Title); the `btn-sm` toggle being shorter than a `form-select` is accepted.
- **F3** The `<select>` ids `newly-added-system`, `missing-system`, `duplicates-system`, `multiple-releases-system`, `hacks-system`, `missing-metadata-system` go away. The toggle button is named `System: All` / `System: <displayName>` (FilterDropdown's `aria-label`), filled (`btn-secondary`) while a system is chosen.

### Attach as a new release (A)
- **A1** `POST /api/review/{id}/resolve` (Admin, unchanged access) accepts `action: "Attach"` with `flag: "Release"` (new) and a `targetGameId`. `ReviewEndpoints.Validate`'s flag check becomes `"Hack" or "Translated" or "None" or "Disc" or "Release"`, with the message `Must be Hack, Translated, None, Disc or Release.` A `disc` with `Release` stays `400` (`A disc number needs Manual or Attach as a disc.`), as for Hack/Translated today.
- **A2** In `ReviewResolver`, `Release` goes through the existing `Attach` branch (target lookup, same-system check, `OpenTarget`, title = the game's title), with these differences from Hack/Translated: the regions, languages, revision and flags come from `NoIntroName.Parse(<name stem>)` (as the `None` branch parses a name), where `<name stem>` is the original file name without extension, or, for a set item (`set` from `ReviewSets.FindAsync` is not null), the stem of the set member whose `IngestFile.Name` ends in `.cue` (case-insensitive), whichever member was opened. Parsed flags matching `^(Disc|Track) \d+$` (case-insensitive) are dropped; another disc is Attach as Disc's job. Then the flag `Unverified` is appended (as the Dat action's manual pick does, ReviewResolver ~472), so `PrimaryReleaseRanker` keeps DAT-verified releases primary; it is not added twice when the parsed flags already have it. The file name is the sanitized original stem plus ` (Unverified)` (not added twice when the stem already contains `(Unverified)`), plus the same extension rule (`external || system.KeepsSourceExtension ? originalExtension : system.RomExtensions[0]`); a set keeps its members' source names as today (B14). Everything after (name checks, `NameTaken` / `Duplicate` 409s, unmatched sets, external storage, primary release recompute) is the shared code path, unchanged. The release is identified as Manual, as other Attach releases are today.
- **A3** The Attach tab (`routes/review/[id]/+page.svelte`) lists `Release`, `Hack`, `Translated` (plus `Disc` on a disc system), in that order, with **Release** preselected for every item (`attachFlag` default and the per-item reset at line ~148 become `'Release'`). The radio label is `Release`. The hint text becomes `Attach to a game as a new release, a hack or a translation.` and, on a disc system, `Attach to a game as a new release, a hack, a translation or another disc.` The success toast stays `Attached to <title>.`

## Changes
| Area | File(s) | Change |
|---|---|---|
| Api | `src/RnRomManager.Api/Endpoints/LibraryDownload.cs` | P1: `Send` sets `ContentLength` for non-range attachments. |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | P2: `X-Content-Bytes` on the cue ZIP response. |
| Web | `src/RnRomManager.Web/static/emulator/play.js` | P3: total from `X-Content-Bytes` fallback; indeterminate bar without a total; percent capped. |
| Web | `src/RnRomManager.Web/src/lib/components/SystemEmulatorsModal.svelte` | E1 title, E3 prop type. |
| Web | `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte` | E2 button, modal and focus return. |
| Web | `src/RnRomManager.Web/src/lib/reports.ts` (new), `src/routes/reports/+layout.svelte` | S1 shared tab list. |
| Web | `src/RnRomManager.Web/src/lib/components/GlobalSearch.svelte` | S2–S5. |
| Web | `src/RnRomManager.Web/src/lib/systemFilter.ts` (new), `src/routes/+page.svelte` | F1. |
| Web | `src/routes/reports/{newly-added,missing-images,duplicates,multiple-releases,hacks,missing-metadata}/+page.svelte` | F2. |
| Api | `src/RnRomManager.Api/Endpoints/ReviewEndpoints.cs` | A1 flag validation. |
| Core | `src/RnRomManager.Core/Ingest/ReviewResolver.cs` | A2 `Release` in the Attach branch. |
| Web | `src/RnRomManager.Web/src/routes/review/[id]/+page.svelte` | A3 radios, default, hint. |
| Docs | `docs/features.md` | Review queue (:267 Attach bullet; :372 resolve API flags) for A1–A3. |
| Docs | `docs/features.md` | Global search (:26), Systems (:108, modal title), Game detail (:126 action row; :138 Play progress), Reports (:298, System dropdown), Browse the library through the API (:410 `Content-Length`, :413 `X-Content-Bytes`). |
| Docs | `docs/development.md` | Browser emulators (:122): `readContent` total sources and the indeterminate bar. |

No migrations, settings or DB changes. API contract: two response headers added (P1, P2); bodies unchanged.

## Risks
- **Content-Length mismatch (P1):** a DB size that doesn't match the ZIP entry now aborts the response instead of sending a short `200`. That's an Integrity problem already; failing loudly is better than a truncated ROM. Saves gain the same check.
- **`Results.Stream` and a preset `ContentLength`:** the result must not clear the header for a non-seekable stream. Step 1's tests prove it on the real pipeline.
- **Tests that pin today's behaviour** (listed in each step) change: the modal title in three places, the report `<select>` tests, the player's "unknown length hides the bar" test.

## Steps
- [ ] **1. Download size headers**: P1, P2, and docs `features.md` :410/:413. Files: `LibraryDownload.cs`, `PlayDisc.cs`, `docs/features.md`. Tests: `LibraryEndpointsTests` (`LibraryEndpointsTests.Download.cs`: a ROM inside the ZIP has `Content.Headers.ContentLength == rom size`; `LibraryEndpointsTests.PlayDisc.cs`: a cue disc has `X-Content-Bytes` equal to cue + tracks bytes and no `Content-Length`, a standalone image has no `X-Content-Bytes`), `SaveEndpointsTests` (a save download has `Content-Length` equal to its size). Done when: those tests pass with Docker, the existing download/play-disc/save tests still pass.
- [ ] **2. Player progress bar always on**: P3, and docs `features.md` :138 ("The player shows a progress bar…" sentence: say it's a percentage when the size is known, an animated bar otherwise) and `development.md` Browser emulators. Files: `static/emulator/play.js`, docs. Tests: `src/lib/emulatorPlay.test.ts` — change the `'error'` case of "reports bytes received…" (no length: bar now **shown** and indeterminate, `progress.hasAttribute('value')` false, byte-count label); add a case with only `X-Content-Bytes: 4` (bar `max` 4, 50% after 2 bytes) and one where more bytes than `X-Content-Bytes` arrive (label stays `100%`). Done when: `npx vitest run src/lib/emulatorPlay.test.ts`, `npm run lint`, `npm run check` pass.
- [ ] **3. Emulators button on the game page**: E1–E3, docs `features.md` :108 and Game detail. Files: `SystemEmulatorsModal.svelte`, `routes/games/[id]/+page.svelte`, docs. Tests: `SystemEmulatorsModal.test.ts` (title `Game Boy Emulators`, line 32), `SystemGames.test.ts` (line 213 heading name), `routes/games/game-page.test.ts` (new case: the Emulators button sits after Collections, opens the `<system> Emulators` modal, focus returns to it on close). Done when: those vitest files, lint and check pass.
- [ ] **4. Reports in Global Search**: S1–S5, docs `features.md` Global search. Files: `src/lib/reports.ts` (new), `routes/reports/+layout.svelte`, `GlobalSearch.svelte`, docs. Tests: `src/lib/reports.test.ts` (new: Site vs Admin list, order), `GlobalSearch.test.ts` (new `describe('reports group (S)')`: "dup" shows a Duplicates chip first, Enter navigates to `/reports/duplicates`; a Site user searching "missing metadata" gets no Missing Metadata chip; arrow order Reports → Collections → Games; label/placeholder text; the existing `describe('play from search (B27)')` cases (~lines 517–580) type `'game'`, which now also matches "Games With Hacks" and puts a report chip at option 0 — switch their query to `'tetris'` so the playable game stays option 0), `routes/reports/reports-pages.test.ts` (tabs still render in order). Done when: those vitest files, lint and check pass.
- [ ] **5. System dropdown on six reports**: F1–F3, docs `features.md` Reports (each "System filter" mention says it's the Games page's System dropdown). Files: `src/lib/systemFilter.ts` (new), `routes/+page.svelte`, the six report pages, docs. Tests: `src/lib/systemFilter.test.ts` (new: hides systems without ROMs, keeps the selected one, badge/search fields), `routes/reports/reports-pages.test.ts` (rewrite every select-based case — Missing Images ~400–407, Multiple Releases ~529–548, Newly Added ~776–790 (drop the `newly-added-system` id check), Games With Hacks ~844–847 — to open the `System: All` button and click a menu row, as `routes/library-select.test.ts` does; add one such case for Duplicates and one for Missing Metadata asserting the request/URL; add `shortName` and `logoUrl: null` to the `systems` fixture (~lines 43–46); the `bootstrap` mock gains `Dropdown` if missing), `routes/library-select.test.ts` (unchanged, must pass). Done when: no `-system"` select ids remain in those pages, the vitest files, lint and check pass.
- [ ] **6. Attach as a new release**: A1–A3, docs `features.md` :267 and :372. Files: `ReviewEndpoints.cs`, `ReviewResolver.cs`, `routes/review/[id]/+page.svelte`, docs. Tests: `ReviewRequestValidationTests` (line 45 message becomes `Must be Hack, Translated, None, Disc or Release.`; `Release` valid; `Release` + `disc` is 400), `ReviewResolverTests` (new: `Release` on `Game (Europe) (Rev 1).gb` attaches to the target with regions Europe, revision `Rev 1`, flags `[Unverified]`, file name `Game (Europe) (Rev 1) (Unverified).gb`; a `(World)` Release attached to a game whose DAT-verified primary is `(Europe)` does not become primary; a disc set opened from its track `Game (Europe) (Disc 2) (Track 1).bin` takes its tags from the cue and gets no `Disc`/`Track` flags; a name clash returns `NameTaken`), `ReviewEndpointsTests` (one `Release` resolve end to end), `routes/review/review-page.test.ts` (line 404 hint text; Release is first and checked; submitting sends `flag: 'Release'`; Disc still only on a disc system). Done when: those .NET classes (Docker for the endpoint class) and the vitest file, lint and check pass.
- [ ] **7. End-to-end**: `tests/e2e/specs/mvp.spec.ts` — line 1952 expects `Game Boy Emulators`; the game-detail flow clicks **Emulators** and sees `<system> Emulators`; the global search flow (~1654) types "duplic" and Enter opens `/reports/duplicates`; the Games With Hacks flow (~3041) picks a system from the `System: All` dropdown and the URL gets `?system=`. Done when: `pwsh scripts/test-e2e.ps1` passes in Finish.

## Decisions
- 2026-11-20 Progress when the size is unknown → server sends the size: `Content-Length` for in-ZIP ROMs, an `X-Content-Bytes` estimate for the cue ZIP (user)
- 2026-11-20 Emulators button placement → game page action row, after Collections (user)
- 2026-11-20 Reports in Global Search → chips, first group (user)
- 2026-11-20 Report System dropdowns → same FilterDropdown, no counts (user)
- 2026-11-20 Report System column caption → none; the toggle text is the caption, bottom-aligned, `btn-sm` height accepted (reviewer)
- 2026-11-20 Review pass 1 fact fixes: `'game'` search tests collide with "Games With Hacks"; full list of report select tests; fixture `shortName`; report matches cleared with the other groups; `X-Content-Bytes` precedence; development.md Browser emulators is at :122 (reviewer)
- 2026-11-20 Attach as a new release: tags from the file name, Release first and default, file keeps its own name (user)
- 2026-11-20 May an attached Release become primary? → no: it gets `Unverified` (flag and file name) so verified releases stay primary (user)
- 2026-11-20 Release on a disc set → parse the cue's name, drop `Disc <n>`/`Track <n>` groups (user)
- 2026-11-20 E2E for Attach as Release → none; the seed's only unmatched item is used by the suggestion flow, so vitest and .NET tests cover A (code)
- 2026-11-20 A new `Release` flag instead of reusing `None`, which only resolves `AmbiguousGroup` items from their DAT identity (code)
- 2026-11-20 A total-less response (old server, compression) → indeterminate bar plus byte count, so the bar always shows (code)

## Kickoff prompt
```text
/spec implement 2026-11-20-report-filters-and-search.md
```
Starts the spec, or resumes it at the first unticked step.
