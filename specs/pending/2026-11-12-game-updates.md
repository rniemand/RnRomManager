---
title: Game updates
status: ready # draft | ready | in-progress | completed
created: 2026-11-12
---

# Game updates

## Summary
Switch updates (e.g. `DREDGE[UPD][1.0.1].nsp`) are already ingested as releases of their base game, flagged `Update`, stored as external files and pointed at from `info.json` (external-roms-switch T4–T6, E1–E4). They have no version, so two updates of a game can't be ordered (both are named `… [v0].nsp`, the second gets ` (2)`), a `[UPD]` file without a title ID ends up as a *base* release when resolved in Review, and the Game page mixes updates with releases. This spec adds a release `version` (info.json v5, DB column), uses the `[UPD]` marker, and gives the Game page an **Updates** tab listing a game's updates newest first, each with its download link. It is generic: any system whose identification flags a release `Update` gets the tab; no handler property.

## Out of scope
- A DLC tab (DLC stays in Releases).
- Editing a release's version (Admin metadata edit, Review manual action).
- Identifying a `[UPD]` file with no title ID by matching its name against titledb automatically (it goes to Review; B8).
- Versions for releases not flagged `Update` (the field stays null for them).
- "Download all updates" / bundling updates into the game ZIP download.
- Reading the version from inside an NSP/XCI (needs keys; the version comes from the file name only).

## Behaviour

### Version
- **B1 Field.** `info.json` **schema version 5**: each Rom gains `version` (string or null). `InfoJsonRom.Version` (`string?`). Validator: null, or matches `\A[0-9]{1,9}(\.[0-9]{1,9}){1,3}\z` (so at most 39 chars; the DB column is `VARCHAR(40)`), else invalid (`rom '<fileName>' version '<v>' is not a dotted version`). The validator doesn't tie it to the `Update` flag (generic, and a hand-edited file stays readable).
- **B2 Parse.** New `Core/Naming/ReleaseVersion.From(string fileName)`: the first match of `\[([0-9]{1,9}(?:\.[0-9]{1,9}){1,3})\]` (ordinal, ASCII digits only) in the name, group 1; no match → null. `DREDGE[UPD][1.0.1].nsp` → `1.0.1`; `X [v65536].nsp` → null (`v` isn't a digit); `X [2024].nsp` → null (needs a dot); `X [1.2.3.4.5].nsp` → null.
- **B3 When it's set.** Every place that builds a new release from an ingest or Review source sets `Version = Flags contains "Update" (ordinal) ? ReleaseVersion.From(OriginalFileName) : null` through one helper (`ReleaseVersion.For(flags, originalFileName)`): `IngestPipeline` (libretro and titledb matches) and every `ReviewResolver` action that creates a release (`Dat`, `Attach`, manual). Edits that copy an existing release (title edit, move, merge, delete-restore) keep its `Version` unchanged.
- **B4 Upgrade.** `InfoJsonUpgraderV4` (v4 → v5): each object element of `roms` gets `version` = B2 on its `originalFileName` (when a string) if its `flags` is an array containing the string `"Update"`, else `null`. Other shapes are left for the typed read/validator (as `InfoJsonUpgraderV3`). Registered in `AppHost` next to V3. As today the file is rewritten as v5 on its next normal write.
- **B5 Database.** Migration `0019_release_versions.sql`: `ALTER TABLE roms ADD version VARCHAR(40) NULL;` then backfill existing updates with the same rule as B2/B3, exactly (MariaDB string literals need doubled backslashes, and `REGEXP_SUBSTR` returns `''` on no match, hence `NULLIF`):
  ```sql
  UPDATE roms
  SET version = NULLIF(SUBSTRING(REGEXP_SUBSTR(original_file_name, '\\[[0-9]{1,9}(\\.[0-9]{1,9}){1,3}\\]'), 2,
                CHAR_LENGTH(REGEXP_SUBSTR(original_file_name, '\\[[0-9]{1,9}(\\.[0-9]{1,9}){1,3}\\]')) - 2), '')
  WHERE JSON_CONTAINS(flags, '"Update"');
  ```
  A non-match leaves NULL. Needed because DB sync skips ZIPs whose size and mtime are unchanged, so the mirror would never pick the upgraded value up. `RomRow`, `MirrorEntry.ToRows` and the repository insert/select carry it (`DbSync` mirrors through `ToRows`, as for `externalFileName`; no change there). `GET /api/games/{id}` release rows gain `version` (string or null); `src/lib/api/types.ts` `RomDetail.version: string | null`.

### Switch identification
- **B6 Update file name.** For a titledb `Update` match with a version, the rom `fileName` (T5) becomes `<name> [<TitleId>][v<N>][<version>]<ext>`, e.g. `DREDGE [0100ABCD12340800][v0][1.0.1].nsp`, so two updates of one game don't clash. Without a version, and for base/DLC, T5 is unchanged. E1/E2 apply to the result as today (E2 cuts the stem from its end, so a very long name can lose the version tag from the *file name*; the `version` field keeps it).
- **B7 `[UPD]` at ingest.** In T4, when the title id comes from the **file name** (not the header), is a base id (low 13 bits clear) and the file name contains `[UPD]` (ordinal-ignore-case), match `base | 0x800` instead (→ `Update`, base entry required as today). A header `titleId` is authoritative and is never changed by `[UPD]`.
- **B8 Review "update" toggle.** `ReviewResolutionRequest` gains `bool? Update`. With `Update == true`: the action must be `Dat`, the item's chosen handler must use `DatSource.TitleDb`, and the picked `datGameName`'s title id must be a base id; otherwise `400 ValidationFailed` with field `update` (item and source untouched). It then resolves as T9 would for `base | 0x800` (flags `Update`, B3 version from the item's original file name, B6 name). `Update` null/false keeps today's behaviour. Review item page, for the items it already treats as titledb (its existing `item.system === 'switch'` check, G8; not generalised here): a checkbox **This file is an update** above both the Suggestions and the DAT search results (one checkbox, used by every `Use` button), pre-ticked when the item's original file name contains `[UPD]` (case-insensitive); `useDat` sends `update` with its value; the success toast is `Added update of <datGameName>.` when ticked.

### Game page
- **B9 Tabs.** Tabs are `Releases (<n>)`, `Updates (<m>)`, `Images (<k>)`, `Zip Contents`. An *update* is a rom whose `flags` contains `Update`. The Updates tab is shown only when `m > 0`; `?tab=updates` on a game without updates shows Releases (as an unknown tab does today). `n` counts roms that aren't updates.
- **B10 Lists.** One tabpanel, today's `#releases-panel`, renders both tabs with a switched list (no new component); its `aria-labelledby` is `releases-tab` or `updates-tab` for the active tab, and its visually hidden heading reads `Releases` or `Updates`. Releases lists the non-update roms in today's order; Updates lists the updates newest first. Each update card is today's release card (same download button, note, External badge and Admin actions) plus a `Version <version>` badge in its header when `version` is set. "Expand/Collapse all" is shown when the visible list has ≥ 2 cards and acts on that list. The header **Download ROM** button/dropdown (`downloadRoms`) lists only non-update roms (updates are downloaded from their cards); a game with only updates keeps today's behaviour for that button over its updates. A game with only updates shows `No releases other than updates.` on the Releases tab.
- **B11 Update order** (pure function `compareUpdates` in `src/lib/releases.ts`), first difference wins: (1) `version`, compared segment by segment numerically (missing segments = 0); a release with a version before one without; higher first; (2) the number in `revision` `v<N>` (none = 0), higher first; (3) `dateAdded`, newer first; (4) `fileName`, ordinal ascending.
- **B12 Unchanged.** Primary release ranking (T6), the download endpoints (G4, G5), integrity, the library list and its counts, collections and reports treat updates exactly as today.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Naming/ReleaseVersion.cs` (new) | B2 parser + B3 helper |
| Core | `Library/InfoJsonRom.cs`, `InfoJsonValidator.cs`, `InfoJsonReader.cs` (`CurrentVersion = 5`), `InfoJsonUpgraderV4.cs` (new), `InfoJsonSerializer.cs` if fields are listed there | B1, B4 |
| Api | `AppHost.cs` | register `InfoJsonUpgraderV4` |
| Data | `Migrations/0019_release_versions.sql` (new), `Repositories/LibraryRepositories.cs`, `Core/Library/LibraryRows.cs` | B5. Existing rows: NULL except flagged updates, backfilled |
| Api | `Endpoints/LibraryEndpoints.cs` (game detail) | `version` on release rows (additive) |
| Core | `Dats/TitleDb.cs`, `Ingest/IngestPipeline.cs`, `Ingest/ReviewResolver.cs`, `Ingest/ReviewResolutionRequest.cs`, `Api/Endpoints/ReviewEndpoints.cs` | B3, B6, B7, B8 (new optional request field `update`) |
| Web | `src/lib/api/types.ts`, `src/lib/releases.ts` (new), `routes/games/[id]/+page.svelte`, `routes/review/[id]/+page.svelte` | B8 checkbox, B9–B11 |
| TestData / e2e | `tests/RnRomManager.TestData/IngestSeeder.cs`, `tests/RnRomManager.Core.Tests/Ingest/IngestSeederTests.cs`, `tests/RnRomManager.Api.Tests/Integration/SeedIngestTests.cs`, `tests/e2e/specs/mvp.spec.ts` | seeded `[UPD]` update, Updates tab flow |
| Docs | `docs/zip-format.md`, `docs/features.md`, `docs/adding-a-system.md`, `docs/deployment.md` | v5 row + `version`; upgrade note (v5 ZIPs read-only for older apps, migration 0019 backfill); Updates tab and `[UPD]`/Review toggle; how a system gets updates (flag `Update`) |

## Risks
- **Schema bump.** Older app versions can't write v5 ZIPs (they report `InfoJsonNewerSchema`, read-only) — same as every bump; documented in `docs/zip-format.md`. Tests that assert `schemaVersion` 4 are updated in step 1.
- **Backfill drift.** The SQL backfill and B2 must agree; step 2's Testcontainers test runs both on the same names (including non-matching ones) and compares.
- **Existing Switch updates keep their old file names** (`… [v0].nsp`, ` (2)`); only new ingests get B6 names. No rename job (not asked; names are cosmetic, the version field orders them).
- **`[UPD]` false positive** only affects file-name ids that are base ids; a wrong pick in Review can be fixed with the existing Admin move/delete release.

## Steps
- [ ] **1. Release version in info.json v5**: B1, B2, B4 (`ReleaseVersion.From`/`For`, `InfoJsonRom.Version`, validator rule, `CurrentVersion = 5`, `InfoJsonUpgraderV4` + registration). Files: `Core/Naming/ReleaseVersion.cs`, `Core/Library/InfoJson*.cs`, `Api/AppHost.cs`. Tests (Core.Tests, NUnit): `ReleaseVersionTests` (B2 examples incl. null cases, first-match-wins, `For` with/without `Update`); `InfoJsonUpgraderV4Tests` (update with/without tag, non-update, non-string name, non-array flags); validator accepts null/`1.0.1`, rejects `1`, `1.a`, 5 segments; serializer round-trip writes `version`; existing `schemaVersion: 4` assertions updated. Done when: v4 files read as v5 with versions filled; new writes emit `"schemaVersion": 5` and `version`; Core tests pass.
- [ ] **2. Version in the database and API**: B5. Files: `Data/Migrations/0019_release_versions.sql`, `Data/Repositories/LibraryRepositories.cs`, `Core/Library/LibraryRows.cs`, `Api/Endpoints/LibraryEndpoints.cs`, `Web/src/lib/api/types.ts`. Tests: Data.Tests (Testcontainers) — migration backfill sets `1.0.1` for an `Update` row named `X[UPD][1.0.1].nsp`, NULL for a non-update row with the same tag and for an update with no tag, and equals `ReleaseVersion.From` on a table of names; mirror insert/select round-trips `version`; DbSync of a changed ZIP updates it. Api.Tests: game detail includes `version`. Done when: Core.Tests, Data.Tests and Api.Tests pass; `npm run check` passes.
- [ ] **3. Set versions and use `[UPD]` at ingest**: B3 (ingest + existing Review actions), B6, B7. Files: `Core/Dats/TitleDb.cs`, `Core/Ingest/IngestPipeline.cs`, `Core/Ingest/ReviewResolver.cs`. Tests: `TitleDbTests` — B6 name with/without version, base/DLC unchanged; B7 file-name base id + `[UPD]` → Update, header id ignores `[UPD]`, `[upd]` matches; `Api.Tests/Integration/IngestPipelineTests.cs` — an update ingests with `version` and B6 external name, two updates of one game get distinct names; `ReviewResolverExternalTests` — `Attach`/`Dat` of an update source set `version`. Done when: Core.Tests and Api.Tests pass.
- [ ] **4. Review "update" toggle**: B8. Files: `Core/Ingest/ReviewResolutionRequest.cs`, `Core/Ingest/ReviewResolver.cs`, `Api/Endpoints/ReviewEndpoints.cs`, `Web/src/routes/review/[id]/+page.svelte`. Tests: `ReviewResolverExternalTests` — `update: true` stores flags `Update`, version, `base|0x800` id name; each 400 `update` case (non-`Dat` action, libretro system, non-base pick) leaves the item Pending; Api.Tests for the 400 shape; vitest (review page test) — checkbox only for Switch items, shared by Suggestions and search results, pre-ticked for `[UPD]`, request body carries `update`, toast text. Done when: Core, Api and web tests pass; `npm run lint`/`check` pass.
- [ ] **5. Game page Updates tab**: B9–B11. Files: `Web/src/lib/releases.ts` (+ `releases.test.ts`), `Web/src/routes/games/[id]/+page.svelte`, `Web/src/routes/games/game-page.test.ts`. Tests: vitest `compareUpdates` (each B11 tie-break, `1.10.0` > `1.9.0`, `1.0` = `1.0.0` then revision); page tests — tab hidden with no updates, counts split, `?tab=updates` order and version badge, fallback to Releases, only-updates message, download link present on update cards, aria-labelledby follows the tab, header Download ROM lists no updates. Done when: `npm run lint`, `npm run check`, `npm test` pass.
- [ ] **6. Seed data, e2e and docs**: seed `04-orbit-expedition[UPD][1.1.0].nsp` (ticket for the existing Orbit Expedition update id, distinct bytes) in `IngestSeeder`; extend the Switch e2e test: Updates tab shows 2 updates, `Version 1.1.0` first, its download works, Releases tab no longer lists updates; update counts the e2e asserts (External badges, totals), `IngestSeederTests` (Switch file list) and `SeedIngestTests` (rom counts, totals). Docs: `docs/zip-format.md` (v5 row, `version` field), `docs/features.md` (Updates tab, `[UPD]`, Review toggle), `docs/adding-a-system.md` (a system gets the Updates tab by flagging releases `Update`), `docs/deployment.md` (v5 upgrade note, 0019 backfill). Tests: `pwsh scripts/test-all.ps1`, `pwsh scripts/test-e2e.ps1`. Done when: both pass; docs describe B1–B11.

## Decisions
- 2026-11-12 Model updates as a new storage type or as releases? → As releases flagged `Update` (already ingested, stored externally and in info.json by external-roms-switch); this spec adds version, `[UPD]` handling and the tab (code)
- 2026-11-12 How to track the version? → New optional `version` field in info.json v5 + `roms.version` column, set from `[x.y.z]` in the file name (user)
- 2026-11-12 Where do updates appear on the Game page? → Only in the Updates tab, with the same card actions as releases (user)
- 2026-11-12 `[UPD]` file with no title ID? → Review with an "This file is an update" checkbox pre-ticked by `[UPD]`; a file-name base id + `[UPD]` is an update at ingest; no automatic name matching (user)
- 2026-11-12 Gate by a handler property? → No; the tab is driven by the `Update` flag, for any system (user)
- 2026-11-12 Reuse the release card instead of a new component; order newest first (code / default)
- 2026-11-12 Exact backfill SQL with `NULLIF` and doubled backslashes (reviewer R3)
- 2026-11-12 Header Download ROM lists only non-updates, to match "updates only in the Updates tab" (reviewer R4, from the user's tab answer)
- 2026-11-12 Review page keeps its `system === 'switch'` titledb check; one checkbox for Suggestions and search (reviewer R6, code)
- 2026-11-12 One tabpanel with switched aria-labelledby; Expand all threshold on the visible list (reviewer R5)

## Kickoff prompt
```text
/spec implement 2026-11-12-game-updates.md
```
Starts the spec, or resumes it at the first unticked step.
