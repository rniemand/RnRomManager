---
title: ROM Zip Config and Jobs Restyle
status: in-progress
created: 2026-11-19
---

# ROM Zip Config and Jobs Restyle

## Summary
UI-only polish for Admins. The Jobs page shows the running job in a table styled like **Recent jobs**. **Admin → ROM Zip Config** becomes quicker to scan: storage shown as icons with tooltips, compression levels as coloured badges, a **Folder** column with each system's short code (where to drop ROMs), system badges linking to the Library filtered to that system, each action button in its own column, grouped headers, an intro line, and a restyled ZIP compression levels legend.

## Out of scope
- Any API, data or job change: every value shown already comes from `GET /api/admin/rom-storage` (`RomStorageRow`) and `GET /api/jobs/current` (`CurrentJob`).
- The confirmation modals (texts, labels, flows) on ROM Zip Config: unchanged.
- The **Recent jobs** table and the job detail page (`/jobs/[id]`): unchanged.
- Showing the configured absolute ingest/library paths: the Folder column shows the conventional relative paths only.

## Behaviour

### Jobs page (`/jobs`)
- **J1** The **Running** section (heading `h2.h5` "Running" unchanged) becomes `div.card` > `div.table-responsive` > `table.table.table-sm.table-striped.table-hover.align-middle.mb-0` with `thead.table-dark`, exactly the classes of the **Recent jobs** table. It replaces today's `div.card.card-body`; the `mb-4` spacing moves to the outer `div.card`.
- **J2** Columns, in order: **Job**, **Progress**, **Started**, then two columns whose headers are visually hidden: "Details" and "Cancel" (`<th scope="col"><span class="visually-hidden">…</span></th>`, as the Recent jobs Details column does).
- **J3** The running job's row:
  - Job: `jobLabel(type)` plus ` (<mode>)` when `mode` is set (as today).
  - Progress (`style="min-width: 14rem"` on the cell): today's progress bar (same `role="progressbar"`, `aria-label="Job progress"`, `aria-valuenow`), below it the `small text-body-secondary text-break` line `"{current} / {total}"` plus ` · {currentItem}` when set (still `aria-live="polite"`), and `<JobStepProgress step={running.step} />` below that.
  - Started (`<td class="text-nowrap">`, house rule for dates): `formatDateTime(running.startedAt)`.
  - Details: `<a href={resolve('/jobs/[id]', { id })}>Details</a>`.
  - Cancel (`class="text-end"`): today's `btn btn-sm btn-outline-danger` **Cancel** button when `isCancellable(type)`; an empty cell otherwise.
- **J4** No running job: the same table (header included) with one row, `<td colspan="5" class="text-body-secondary">No job running.</td>`. Text unchanged.

### ROM Zip Config systems table (`/admin/rom-storage`)
- **R1** An intro paragraph, first inside the loaded (`{:else}`) branch, above the action error and **Resume moves** (not shown while loading or on a load error): `<p class="text-body-secondary">Where each system keeps its ROMs and how its game ZIPs are compressed. Changing either rewrites every game of that system in a long-running job.</p>`.
- **R2** Table classes unchanged (house rule T1–T4, enforced by `src/lib/table-style.test.ts`): `table table-sm table-striped table-hover align-middle mb-0` inside `div.card` > `div.table-responsive`, header `thead.table-dark`. The restyle comes from the grouped headers, icons, badges and centred columns.
- **R3** Two header rows. Row 1 (group headers, `scope="colgroup"`, centred with `text-center` except the first): a 2-column group whose text is visually hidden "System"; **ROM storage** over 3 columns; **ZIP compression** over 2 columns. Row 2 (`scope="col"`), in order: **System**, **Folder**, **Default**, **Configured**, **Change**, **Level**, **Repack**. Column order is therefore System · Folder · Default · Configured · Change · Level · Repack (the storage button moves next to its group; see Decisions).
- **R4** System: `SystemBadge` (unchanged props) wrapped in `<a class="text-decoration-none" href="{resolve('/')}?system={encodeURIComponent(row.id)}">` with `use:tooltip={`Show ${row.displayName} in the Library`}`. The page gets the same `<!-- eslint-disable svelte/no-navigation-without-resolve -- resolved paths plus a ?system= query -->` comment as `routes/reports/library-size/+page.svelte`. The link is shown for every system, with or without ROMs (the Library keeps a URL-selected system in its filter even without ROMs).
- **R5** Folder: `<code use:tooltip={`Put ROMs in ingest/${row.id}; stored in roms/${row.id}`} tabindex="0">{row.id}</code>`. The short code is the system id, which is both the ingest folder (`ingest/<id>`, `StorageFolders.EnsureSystemFolders`) and the library folder (`roms/<id>/…`).
- **R6** Default and Configured (both `text-center`): the storage text is replaced by an icon. Embedded (`true`): icon `storage-in-zip` (`bi bi-file-zip`), accessible label "Stored in ZIP", tooltip "Stored in ZIP: ROMs are inside each game's ZIP". External (`false`): icon `storage-beside-zip` (`bi bi-files`), label "Beside ZIP", tooltip "Beside ZIP: ROMs are separate files next to each game's ZIP". Markup: `<span class="d-inline-block fs-5" tabindex="0" use:tooltip={tip}><Icon name={…} label={…} /></span>` so the tooltip also opens on keyboard focus. The `storage()` helper becomes a lookup returning `{ icon, label, tip }`, rendered by one `{#snippet storageIcon(embed: boolean)}`.
- **R7** Configured keeps the pending badge after the icon, unchanged: `badge text-bg-warning ms-1`, "N game(s) still to move".
- **R8** Change (`text-end`): today's `btn btn-sm btn-outline-primary` **Store in ZIP** / **Store beside ZIP** button (same text, same `onclick`, same `disabled={blocked}`) for configurable rows; empty cell otherwise.
- **R9** Level: for `compressionConfigurable` rows, a badge `<span class="badge {levelBadge}">{label}</span>` where Store → `text-bg-secondary` "Store", Med → `text-bg-primary` "Medium", High → `text-bg-success` "High" (one `levels` array entry each gains a `badge` class), followed by an icon-only Edit button: `<button class="btn btn-sm btn-outline-primary ms-2" aria-label="Edit ZIP compression of {displayName}" use:tooltip={'Edit compression level'} disabled={blocked}><Icon name="edit" /></button>` (same `onclick`). For other rows: `<span class="text-body-secondary" aria-hidden="true">—</span><span class="visually-hidden">Not applicable: ROMs are beside the ZIP</span>` (replaces "— ROMs beside ZIP").
- **R10** Repack (`text-end`): today's **Repack again** button (same classes minus `ms-2`, same `aria-label="Repack {displayName} ZIPs again"`, `disabled`, `onclick`) for `compressionConfigurable` rows; empty cell otherwise.
- **R11** All buttons stay disabled while any job runs or an action is busy (`blocked`), as today.

### ZIP compression levels legend
- **L1** Heading "ZIP compression levels" (`h2.h6 mt-4`) and the footnote "These numbers are typical estimates, not guarantees." unchanged; table classes as R2 (single header row: **Level**, **Typical size**, **Speed**, **Use for**).
- **L2** Level cells (`th scope="row"`) show the same badges as R9 ("Store", "Medium", "High"). The Medium row adds `<span class="badge rounded-pill text-bg-light border ms-1">Default</span>` after its badge, replacing "Medium (default)".
- **L3** Speed cells start with a decorative icon before today's text: Store `speed-fast` (`bi bi-lightning-charge`), Medium `speed-medium` (`bi bi-lightning`), High `speed-slow` (`bi bi-hourglass-split`), each `<Icon name=… class="me-1" />` (no label: the text says it). Size and Use for texts unchanged.

### Accessibility and mobile
- **A1** Every icon that carries meaning has an accessible name (R6) and its tooltip opens on hover and focus (`#lib/tooltip`); decorative icons (L3) are `aria-hidden` (Icon without `label`).
- **A2** Both tables keep `table-responsive`, so narrow screens scroll the table, not the page.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Web | `src/RnRomManager.Web/src/routes/jobs/+page.svelte` | J1–J4: Running section as a table; doc comment cites J1–J4. |
| Web | `src/RnRomManager.Web/src/lib/components/icons.ts` | New names: `storage-in-zip` (`bi bi-file-zip`), `storage-beside-zip` (`bi bi-files`), `speed-fast` (`bi bi-lightning-charge`), `speed-medium` (`bi bi-lightning`), `speed-slow` (`bi bi-hourglass-split`). |
| Web | `src/RnRomManager.Web/src/routes/admin/rom-storage/+page.svelte` | R1–R11, L1–L3. Imports `Icon` and `tooltip` (`#lib/tooltip.js`). Doc comment updated to cite this spec's rules. |
| Tests | `src/RnRomManager.Web/src/routes/jobs/jobs-page.test.ts` | Updated and new cases (Step 1). |
| Tests | `src/RnRomManager.Web/src/routes/admin/rom-storage-page.test.ts` | Updated and new cases (Steps 2–3). |
| E2E | `tests/e2e/specs/mvp.spec.ts` | Storage toggle and ZIP compression tests updated (Step 4). |
| Docs | `docs/features.md` § Jobs (:238) and § ROM Zip Config (:606) | Describe the new layout. |
| Docs | `docs/development.md` Tables (T1, :110) | Add: "a table may add a first header row of `<th scope="colgroup" colspan="N">` group headers above its `scope="col"` row (as ROM Zip Config does)." |

No migrations, config keys or API changes. README systems table unaffected (no storage/format change).

## Risks
- Text-based assertions break where words become icons: listed per step. The e2e storage toggle asserted "Stored in ZIP"/"Beside ZIP" as row text; it switches to the icons' accessible names.
- The house table rule (T1–T4) is kept, so `src/lib/table-style.test.ts` stays green; steps 2–3 run it too.
- Tooltips on non-interactive elements need `tabindex="0"` to be reachable by keyboard (R5, R6); that adds tab stops (two per row), acceptable for an Admin page.

## Steps
- [x] **1. Jobs: running job as a table**: J1–J4 and the Jobs paragraph in `docs/features.md` (:240: "shows the running job in a table like the recent jobs (label, progress with current item, start time, a link to its page and **Cancel** when it can be cancelled) or a "No job running." row"). The page's doc comment cites J1–J4. Files: `src/RnRomManager.Web/src/routes/jobs/+page.svelte`, `src/RnRomManager.Web/src/routes/jobs/jobs-page.test.ts`, `docs/features.md`. Tests: `jobs-page.test.ts` — the "No job running" case also asserts the first table's column headers are `['Job', 'Progress', 'Started', 'Details', 'Cancel']` and that "No job running." is inside a `td[colspan="5"]`; the running case also asserts the running row's Started cell is `formatDateTime(current.startedAt)` and its Details link is `/jobs/run` and the Started cell has class `text-nowrap`; a new case asserts a non-cancellable running job (`ReviewResolve`) has no Cancel button. Done when: the running job and the idle state render in a `table-dark`-headed table inside a card; `npm run lint`, `npm run check` and `npx vitest run src/routes/jobs/jobs-page.test.ts` pass.
- [x] **2. ROM Zip Config: systems table**: R1–R11, the five `icons.ts` names (all used in this or the next step; the `speed-*` ones may land here), docs `features.md` § ROM Zip Config first two paragraphs (:608, :610: icons with tooltips instead of the words, the Folder column, the system link, the badge colours, Change/Level/Repack columns, "—" for beside-ZIP rows, the intro line), and the `docs/development.md` T1 colgroup sentence. Files: `src/RnRomManager.Web/src/routes/admin/rom-storage/+page.svelte`, `src/RnRomManager.Web/src/lib/components/icons.ts`, `src/RnRomManager.Web/src/routes/admin/rom-storage-page.test.ts`, `docs/features.md`, `docs/development.md`. Tests in `rom-storage-page.test.ts`, mocking the tooltip module to record texts as `GameResults.test.ts` does: the first case asserts `within(gba).getAllByRole('img', { name: 'Stored in ZIP' })` has length 2 (Default and Configured) and `within(nds).getAllByRole('img', { name: 'Beside ZIP' })` has length 2 (with `rows(false, 2)`), instead of row text, and the second header row's headers (`table.querySelectorAll('thead tr')[1]`'s `th` texts) are `['System', 'Folder', 'Default', 'Configured', 'Change', 'Level', 'Repack']`, the first row's `['System', 'ROM storage', 'ZIP compression']`; the "configurable rows…" case asserts the GBA level badge text "High" has class `text-bg-success`, the Edit button contains `i.bi-pencil` and no text, the Repack again button is in its own cell (not the Level cell), and the NDS Level cell's text is "—Not applicable: ROMs are beside the ZIP" with no buttons; new cases: the badge class per level (Store `text-bg-secondary`, Med `text-bg-primary` "Medium"); the Folder cell shows `gba` in a `code` with tooltip "Put ROMs in ingest/gba; stored in roms/gba"; the system badge's link `href` is `/?system=gba`; the storage tooltips' exact texts; the intro text. Existing button-name queries ("Store in ZIP", "Store beside ZIP", the Edit/Repack aria-labels) stay valid unchanged. Done when: the table matches R1–R11; lint, check and `npx vitest run src/routes/admin/rom-storage-page.test.ts src/lib/table-style.test.ts` pass.
- [x] **3. ROM Zip Config: legend restyle**: L1–L3 and the legend sentence in `features.md` (:610: "A legend table under the table compares them, with the same badges"). Files: `src/RnRomManager.Web/src/routes/admin/rom-storage/+page.svelte`, `src/RnRomManager.Web/src/routes/admin/rom-storage-page.test.ts`, `docs/features.md` (and `icons.ts` if the `speed-*` names didn't land in step 2). Tests: the legend case's expected first cells become `'Store'`, `'MediumDefault'`, `'High'` (cell `textContent`, trimmed), the Speed cells keep their texts (icons are empty `<i>`), and it asserts each Level badge's class (as R9) and the speed icon classes (`bi-lightning-charge`, `bi-lightning`, `bi-hourglass-split`). Done when: the legend matches L1–L3; lint, check and `npx vitest run src/routes/admin/rom-storage-page.test.ts src/lib/table-style.test.ts` pass.
- [x] **4. E2E**: `tests/e2e/specs/mvp.spec.ts`. The storage toggle's final check (`toContainText(configured)`) becomes `await expect(row("Nintendo DS").getByRole("cell").nth(3).getByRole("img", { name: configured })).toBeVisible()` (the Configured cell; `configured` stays "Stored in ZIP" / "Beside ZIP", now the icons' accessible names); the ZIP compression test's `columnheader` check uses name "ZIP compression" (the group header) instead of "ZIP Compression", and `toContainText(label)` keeps working (badge text). Both tests add an axe check once their page has loaded (`expectAccessible` from `./axe`): `await expectAccessible(page, "ROM Zip Config")` after the first `/admin/rom-storage` load, and `await expectAccessible(page, "jobs")` after the first `/jobs` load. Runs only in Finish (`pwsh scripts/test-e2e.ps1`). Done when: both tests, including the two axe checks, pass in the e2e run.

## Decisions
- 2026-11-19 Jobs Running section layout → a table in the Recent jobs style (Job, Progress, Started, Details, Cancel); idle shows a "No job running." row in the same table (user)
- 2026-11-19 Storage icons → `bi-file-zip` for Stored in ZIP, `bi-files` for Beside ZIP, icon only with label and tooltip (user)
- 2026-11-19 Compression badge colours → Store grey (`text-bg-secondary`), Medium blue (`text-bg-primary`), High green (`text-bg-success`); the legend uses the same badges (user)
- 2026-11-19 Folder column → the system id in `<code>` with a tooltip naming `ingest/<id>` and `roms/<id>` (user)
- 2026-11-19 Edit button → stays beside the level badge, as an icon-only pencil; Repack and Store in/beside ZIP get their own columns with visible headers (user)
- 2026-11-19 Restyle → grouped headers, legend with badges and speed icons, an intro line (user)
- 2026-11-19 Roomier rows (dropping `table-sm`/`table-striped`) conflict with house rule T1–T4 → keep the house table classes; no exception (user)
- 2026-11-19 Review round 1: colgroup headers documented in T1; Started `text-nowrap`; intro only when loaded; test queries use `getAllByRole`; axe checks added; Jobs doc comment cites J1–J4 (reviewer)
- 2026-11-19 Column order → the storage button column (Change) sits inside the ROM storage group, before the ZIP compression group, so groups stay contiguous (code)

## Kickoff prompt
```text
/spec implement 2026-11-19-rom-zip-config-jobs-restyle.md
```
Starts the spec, or resumes it at the first unticked step.
