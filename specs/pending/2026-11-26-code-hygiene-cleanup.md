---
title: Code hygiene cleanup
status: ready # draft | ready | in-progress | completed
created: 2026-11-26
---

# Code hygiene cleanup

## Summary
A behaviour-neutral cleanup of the generated code base so humans and agents can find, read and change code surgically: one C# type per file, wrapped long parameter lists, shared helpers in place of ~23 duplication hotspots, C# files kept near 500 lines, and an SPA reorganised into typed folders (`types/`, `enums/`, `icons/`, categorised components, one modal per file on a shared `BaseModal`) with a form-control kit and a `Button` component. A concise style guide (`docs/style-guide.md`) records the rules, and guard tests stop new violations from the first step on.

## Out of scope
- Any behaviour change: API responses, ProblemDetails codes, URLs, user-visible text, DB schema, job semantics and e2e flows stay as they are (G1).
- New features, dependency upgrades, a visual restyle (the kit renders today's Bootstrap markup), Bootstrap replacement.
- The 500-line guideline for test files (Decisions), vendored code (`static/emulator/vendor`), `.d.ts` files, `node_modules`, `bin/`, `obj/`.
- Call sites with 4+ arguments (the wrapping rule covers declarations only).

## Behaviour

General
- **G1** Pure refactor. Every existing test and e2e assertion passes unchanged except where a step names the test it moves or rewrites; moving a test file with its source is not a change. Where duplicates disagree on edge cases, the shared helper takes the differing choice as a parameter (e.g. `dir` null meaning asc vs column default, `Trim` vs `TrimEnd(' ')`, invalid page handling), so outputs stay identical.
- **G2** Each step ends with: its targeted tests green, `npm run lint` + `npm run check` when the SPA changed, `pwsh scripts/codemap.ps1` run, and its fixed files deleted from the guard baseline (S3).
- **G3** Docs that name a moved file or symbol (`docs/recipes.md`, `docs/development.md` § Web app structure, `docs/adding-a-system.md` step 5, `CLAUDE.md`, `AGENTS.md`, `CONTRIBUTING.md`) are updated in the step that moves it.

Style guide
- **S1** `docs/style-guide.md` (new) holds the text in Appendix A verbatim (later steps may only fix names it references). `CLAUDE.md` and `AGENTS.md` gain a `## Code style` section with one line: ``Follow `docs/style-guide.md` for every code change; the `CodeStyleTests` guard enforces its structural rules.`` `CONTRIBUTING.md` gains a `## Style rule` section with the same pointer.

Guard tests
- **S2** `tests/RnRomManager.Api.Tests/CodeStyle/CodeStyleTests.cs` (new) runs `CodeStyleScanner` (new, same folder, pure functions over file text so it is unit-testable) over the repo and checks these rules. Scanned roots: `src/**/*.cs`, `tests/**/*.cs`, `src/RnRomManager.Web/src/**/*.{ts,svelte}`; excluded: `bin/`, `obj/`, `node_modules/`, `.svelte-kit/`, `build/`, `static/`, `*.d.ts`. Comment lines (trimmed start `//`, `///`, `*`, `/*`) are ignored.
  - `CS1` A `.cs` file declares more than one top-level type (class, record, struct, interface, enum, delegate), or any nested type. A declaration is a line matching optional modifiers then `class|record|struct|interface|enum|delegate` and a capitalised name (for `delegate`, the name after the return type); it is nested when indented. Exception: `Foo` and `Foo<T>` (same name, differing generic arity) may share a file. `partial` parts of one type in several files are allowed.
  - `CS2` A declaration (type with primary constructor, positional record, constructor, method, local function, delegate) with 4+ parameters where any line of its parameter list holds two or more parameters (so both one-line lists and lists that wrap only some parameters are flagged). A line starts a declaration when the first `(` directly follows the declared identifier (optionally with generic args) and comes before any `=` or `=>`, the line starts with a modifier, a type keyword, or a return type followed by the identifier, and its first token is not one of `return`, `await`, `throw`, `new`, `yield`, `else`, `case`, `using`, `if`, `while`, `for`, `foreach`, `switch`, `lock`, `var`. The list is read to its matching `)`, across lines. Top-level commas are counted outside `<>`, `()`, `[]`, string literals and char literals.
  - `SV1` A `.svelte` file outside `lib/components/modals/` contains `<BaseModal`, `class="modal`, or a `<ConfirmModal` tag that is not self-closing, reading from `<ConfirmModal` to the tag's closing `>` across lines (today's `ConfirmModal` with a body is the base modal, so those sites are in the baseline from step 2 and keep the same rule after the step 29 rename).
  - `SV2` A `.ts`/`.svelte` file outside `lib/types/` and `lib/enums/` has a line starting `export interface` or `export type` (excluding `*.test.ts` and `lib/testing/`). Component-local (non-exported) types such as `Props` are fine.
  - `SV3` A file in `lib/types/` or `lib/enums/` other than `index.ts` exports more or fewer than one type, or its name differs from the file name.
  - `SV4` A `.svelte` file outside `lib/components/forms/` contains `<input`, `<select`, `<textarea` or `<button`.
  - `SV5` A `.ts`/`.svelte` file contains `from '../`, `import('../`, `vi.mock('../` or `import '../` (use `#lib/…`; `./` within one folder is fine).
  - `SV6` A `.svelte` file has a `style="…"` attribute whose value contains no `{` (static inline style; dynamic values such as progress widths stay).
- **S3** Baseline: `tests/RnRomManager.Api.Tests/CodeStyle/code-style-baseline.txt` (new), one `<rule> <repo-relative path>` per line, sorted ordinally, `#` comments allowed. The test fails with two lists: **new violations** (found, not in the baseline: "fix them, never add them to the baseline") and **stale entries** (in the baseline, now clean: "delete these lines"). The baseline is created once in step 2 from the scanner's output and afterwards may only shrink. The one exception: a commit that moves or renames a file still in violation renames its baseline lines to the new path (same rule) in that commit. When it is empty it stays as an empty file with its header comment. The 500-line size guideline is not checked.
- **S4** `tests/RnRomManager.TestData/RepoPaths.cs` (new) `RepoPaths.Root()` finds the folder holding `RnRomManager.slnx` (logic moved from the four `RepoRoot` copies in `BrowserCoreExtensionsTests`, `LicenseInfoTests`, `SupportedSystemsTableTests` and `SystemEmulatorsCatalogTests`, which then call it).

C# structure
- **C1** One type per file, file named after the type, in the folder and namespace it lives in today (no namespace changes, so no `using` churn). Nested types become top-level types in their own file; a nested type that was `private` becomes `internal` (a `file`-scoped type is not used). Its name stays unless it clashes in the namespace, in which case it is prefixed with the former owner's name (`IntegrityJob.ExternalChecks` → `IntegrityExternalChecks` if `ExternalChecks` exists). Owner members the moved type used while nested become `internal` (never `public`), and qualified `Outer.Inner` references are updated. Applies to `src/` and `tests/`.
- **C2** 4+ parameters in any declaration: each parameter on its own line indented one level (4 spaces) past the declaration, the closing `)` stays on the last parameter's line followed by the rest of the declaration, as `IngestJob` already does (`    ILogger<IngestJob> logger) : IJob`). Applies to `src/` and `tests/`.
- **C3** Files over 500 lines in `src/` (after C1) are split by extracting cohesive collaborator classes with their own tests (G1), registered in `AppHost` where they need DI. `partial` is used only where a step says so. A file ≤ 550 lines may stay when no clean seam exists; the step says which.

C# shared helpers (DRY, behaviour kept per G1)
- **D1** `src/RnRomManager.Core/Systems/EmbeddedResource.cs` (new) `EmbeddedResource.Load(Assembly assembly, string name)` replaces the 14 `src/RnRomManager.Systems.*/EmbeddedResources.cs` copies (deleted) and the inline `LoadResource` in `ColecoVisionHandler`, `TurboGrafx16Handler`, `NesHandler`, `Nintendo64Handler`, `SnesHandler`, `SwitchHandler`. `BoxArt.Lazy(Type owner, string name)` (new, same folder) returns the `Lazy<ReadOnlyMemory<byte>>` the ~44 handlers build by hand. Missing resource: same exception type and message as today.
- **D2** `src/RnRomManager.Core/Systems/HeaderText.cs` (new) `HeaderText.ReadAscii(ReadOnlySpan<byte> bytes, bool trimStart)` (ASCII to the first NUL, non-printable → `?`, trim, empty → null) replaces the copies in `GameBoyHeaderReader`, `GameBoyAdvanceHeaderReader`, `NeoGeoPocketHeaderReader`, `Nintendo64HeaderReader`, `NintendoDsHeaderReader`, `Nintendo3DsHeaderReader`, `SnesHeaderReader`. `MegaDriveHeader` (collapses spaces), `NintendoDiscHeader` and `LynxHeader` (Latin-1, no `?` substitution) keep their own.
- **D3** API: `src/RnRomManager.Api/Endpoints/JobStart.cs` (new) `JobStart.StartManualAsync(JobRunner runner, IJob job, HttpContext ctx, CancellationToken ct)` returns `202 { jobId }` or `JobStart.Running(ctx)` (409 `JobRunning`, moved from `DatEndpoints.JobRunning`); replaces the ~10 hand-written tails and `AdminEndpoints`' local helper. `src/RnRomManager.Api/Endpoints/EndpointUser.cs` (new) holds `Id(ClaimsPrincipal)` (from `DatEndpoints.UserId`) and `RequiredId(HttpContext)` (from `CollectionEndpoints.UserId`). `Problems.Validation(HttpContext ctx, string field, string message)` (new overload) replaces single-entry dictionaries. `src/RnRomManager.Api/Endpoints/SortQuery.cs` (new) `SortQuery.ParseDir(string? dir, Dictionary<string, string[]> errors, bool? whenMissing)` keeps the existing "Must be asc or desc." message (case-insensitive, returns `bool?` descending); `whenMissing` reproduces each caller's current null handling. It replaces the 4 `ReportEndpoints` sites and `DiscoveryEndpoints`; `LibraryEndpoints` (case-sensitive, keeps the string) is left as is. `docs/recipes.md` § API endpoint names `JobStart.Running(ctx)` instead of `DatEndpoints.JobRunning(ctx)`.
- **D4** Data: `src/RnRomManager.Data/Repositories/PagedQuery.cs` (new) `QueryPageAsync<T>(this DbConnection connection, string countSql, string pageSql, object args, int page, int pageSize, CancellationToken ct)` replaces the 14 count-then-page bodies; `SqlOrder.Dir(bool descending)` (new, same folder) replaces `descending ? "DESC" : ""`; `ReportRepository` gets one private `MissingMetadataOrder(sort, desc)` for its two identical switches.
- **D5** Tests: `tests/RnRomManager.TestData/HandlerAsserts.cs` (new) `AssertBoxArtPng(ISystemHandler handler, int size)`, `AssertNoHeader(ISystemHandler handler, byte[] rom)`, `AssertNormalizeIsIdentity(ISystemHandler handler, byte[] rom)` replace the copies in the `*HandlerTests` files; `TestHandlers.Fake(string id, bool embedRomsInZip = true)` (new, TestData) returns an NSubstitute `ISystemHandler` with `Id` and `EmbedRomsInZip` set, and callers add any other `.Returns(...)` themselves; it replaces the `Substitute.For<ISystemHandler>()` + `Id`/`EmbedRomsInZip` set-ups; the 6 tests that make temp folders by hand use `TempDir`. `RnRomManager.TestData.csproj` gains `NUnit` and `NSubstitute` package references (versions from `Directory.Packages.props`); `RnRomManager.Systems.Microsoft.Tests` and `RnRomManager.Systems.Sony.Tests` gain a project reference to TestData.

SPA structure
- **W1** Imports use `#lib/…` (with `.js` for TS modules, as today) or `./` within a folder; no `../` (SV5).
- **W2** Types: every exported type moves to `src/RnRomManager.Web/src/lib/types/<Name>.ts` (one per file, file named after the type), re-exported by `lib/types/index.ts` (`export type { X } from './X.js';`). `lib/api/types.ts` is deleted (its 63 interfaces and the `RemovedSystem` alias go to `lib/types/`; its unions `Role`, `SaveKind`, `JobStatus`, `ReviewReason`, `BundleTicketState` go to `lib/enums/`); imports become `#lib/types/index.js` / `#lib/enums/index.js`. String-literal unions (e.g. `SortDir`, `LibraryView`, `ThemeChoice`, `PauseReason`, `RunStatus`, `DiscoveryKind`, `EmulatorOs`, `AddResult`) go to `lib/enums/<Name>.ts` with `lib/enums/index.ts`, staying union types (no TS `enum`). `SortDir` is defined once (was in `discovery.ts` and `libraryQuery.ts`). Where `discovery.ts` defines `LogoKind` and `RememberedTab` as the same union, both names stay as separate files.
- **W3** Icons: `lib/components/icons.ts` → `lib/icons/icons.ts`, `lib/components/Icon.svelte` → `lib/icons/Icon.svelte`.
- **W4** Components move into `lib/components/<folder>/` (tests move with them):
  - `modals/`: `BaseModal`, `ConfirmModal`, `AboutModal`, `CollectionModal`, `CollectionPickerModal`, `EditReleaseModal`, `EmulatorModal`, `GameAdminModal`, `GameCollectionsModal`, `HotkeysModal`, `InfoJsonModal`, `LogoModal`, `ReleaseNoteModal`, `ReviewDeleteModal`, `SystemEmulatorsModal`, `GlobalSearch` (keeps its name and own modal markup; owns `releaseModal` as today), and every modal extracted by W6.
  - `layout/`: `Navbar`, `AppFooter`, `AccordionItem`, `PageTitle`, `RomToolsTabs`, `ToastHost`, `NavTabs` (new).
  - `forms/`: the W7 kit plus `FilterDropdown`, `RatingControl`, `GameTypeahead`, `CollectionChooser`, `CopyButton`.
  - `common/`: `AdminOnly`, `NotAllowed`, `EmptyState`, `ErrorAlert`, `Loading`, `FileSize`, `Pagination`, `SystemBadge`, `JsonTree`, `JobStepProgress`, `SortHeader` (new), `DataTable` (new), `ViewToggle` (new).
  - `games/`: `GameImages`, `GameSaves`, `GameResults`, `GameTitle`, `IgdbLookup`, `ZipBrowser` and the W9 extractions.
  - `discovery/`: `DiscoveryGames`, `DiscoveryGroups`, `GroupLogo`.
  - `systems/`: `SystemList`, `SystemGames`.
  - `bundles/`: `BundleDownload`, `SavedBundleQueues`.
  - `ingest/`: `IngestFileTable`.
  - `review/`: the W9 review tab extractions.
- **W5** Modals: today's `ConfirmModal.svelte` is renamed `modals/BaseModal.svelte` with identical props and behaviour (M1 options, `releaseModal`, `beforeclose`, `footer`, `size`, `scrollable`, `onshown`). A new `modals/ConfirmModal.svelte` wraps it with props `open` (bindable), `title`, `message: string`, `confirmLabel = 'Confirm'`, `confirmClass = 'btn-primary'`, `busy = false`, `onconfirm`, and renders `message` in one `<p class="mb-0">`. Only `ConfirmModal` may be used inline in pages and components; every other modal is a `modals/<Name>Modal.svelte` built on `BaseModal`.
- **W6** The inline modals become files (name → source). A site whose body today is a single `<p>` of text (interpolations allowed) uses inline `ConfirmModal` with the same title, message, label and class instead of a file:
  - `PullCoverImagesModal` (`routes/admin/actions`), `RestoreBackupModal` (`admin/backups`), `StorageMigrationModal`, `ZipCompressionModal`, `RepackZipsModal` (`admin/rom-storage`), `CreateUserModal`, `ResetPasswordModal` (`admin/users`).
  - `UploadImageModal`, `SuggestImagesModal`, `ViewImageModal`, `DeleteImageModal` (`GameImages`), `SaveFileModal` (`GameSaves` mode dialog), `LoadBatterySaveModal` (one file for the identical dialogs in `GameSaves` and `GameResults`), `RomHeaderModal` (`routes/games/[id]`).
  - `ClearBundleModal` (`bundle-creator`), `DeleteCollectionModal` (`collections/[id]`), `PermanentDeleteModal` (`deleted`), `IngestDeleteModal` (`ingestion`), `MergeDuplicatesModal` (`reports/duplicates`), `DeleteReviewItemsModal` (`review`), `DismissReviewModal` (`review/[id]`), `BundleQueueActionModal` (`SavedBundleQueues`).
  Each keeps its title, text, buttons, ids, labels and behaviour; state that only the dialog uses moves into it, results go back through callback props (`onconfirm`, `onsaved`…), and `open` stays bindable.

SPA kit and shared pieces
- **W7** Form kit in `lib/components/forms/` (new), rendering today's Bootstrap markup and classes. Each control spreads `...rest` (every other attribute and event handler: `oninput`, `onkeydown`, `onchange`, `onfocus`, `onblur`, `role`, `aria-*`, `autofocus`, `maxlength`, `pattern`, `id`, `name`, …) onto its native element, appends a `class` prop to its own classes, supports `bind:value`/`bind:checked` (and `indeterminate` on `Checkbox`), and exposes the element as a bindable `ref` prop (`bind:ref`) for focus and `bind:this` uses. New components written once the kit exists (step 33 on) use it from birth:
  - `FormField` (`label`, `for`, optional `help`, `error`, `hiddenLabel` for `visually-hidden` labels, children).
  - `TextInput` (`type: 'text' | 'password' | 'url' | 'email' | 'date' | 'time'`, `size?: 'sm' | 'lg'`), `SearchInput` (`type="search"`, optional `debounceMs` calling `onsearch` via `lib/debounce.ts`, clear button only where a site has one today), `NumberInput` (`min`, `max`, `step`), `FileInput` (`accept`, `multiple`, `onfiles`), `Textarea` (`rows`), `Select` (`options: {value, label, disabled?}[]` or children `<option>`s), `Checkbox` and `Switch` (`form-check` / `form-switch`, `label`), `RadioButtons` (`options`, `bind:value`, `variant: 'buttons' | 'list'` = `btn-group` + `btn-check` or stacked `form-check`, `size`).
  - `Button` (`variant` = Bootstrap colour, `outline`, `size`, `icon` (Icon name), `busy` (adds `spinner-border spinner-border-sm` with `aria-hidden="true"` and disables), `type = 'button'`, `href` renders an `<a class="btn">`, children, `...rest`). With `variant` omitted it renders no `btn` classes at all, only the `class` prop, for non-`.btn` buttons (`btn-close`, `nav-link`, `dropdown-item`, `accordion-button`, `navbar-toggler`, `list-group-item`). Every `<button>` and `a.btn` in `.svelte` files outside `forms/` goes through it (SV4).
- **W8** Shared SPA logic: `lib/sort.ts` (new: `parseDir(value, fallback)`, `flip(dir)`, `nextSort(key, sort, dir, defaultDir)`), `lib/urlState.ts` (new: `pageParam(params, invalid)`, `pushQuery(path, params, defaults)` using `goto(..., { replace: true, reset: false })`), `lib/debounce.ts` (new: `debounced(fn, ms)` with `cancel()`), `lib/loadPage.svelte.ts` (new: `useAbortableGet(url)` → `{ result, error, reload }` with optional `onPastEnd`). Components: `common/SortHeader.svelte` (`columns`, `sort`, `dir`, `onsort`, renders `<th scope="col" aria-sort>` with today's icons), `layout/NavTabs.svelte` (`tabs`, `active`, link mode with `href` or button mode with `onselect`; always `use:scrollActiveTab`), `common/ViewToggle.svelte` (grid/list), `common/DataTable.svelte` (card > `table-responsive` > `table table-sm table-striped table-hover align-middle mb-0` > `thead.table-dark`, `head` and `row` snippets, `empty` message via `EmptyState`). `ErrorAlert` gains `message?: string` and `dismissible` and replaces the 6 raw `alert alert-danger` sites. A vitest `setupFiles` entry `src/lib/testing/setup.ts` (new) holds the shared `$app/paths` identity mock; per-file `vi.mock('$app/paths', …)` copies that match it are removed.
- **W9** Large components: `routes/games/[id]/+page.svelte` (1312) extracts `games/GameHeader.svelte` (the page header and metadata block above the tabs) and `games/ReleaseTable.svelte` (the Releases/Updates/DLC tab body); `routes/review/[id]/+page.svelte` (841) extracts `review/ReviewMatchedTab.svelte`, `review/ReviewSuggestionsTab.svelte`, `review/ReviewAttachTab.svelte`, `review/ReviewManualTab.svelte`. `GameImages` and `GameAdminModal` are re-measured after W6/W7 and split only if still over 500 lines (the step names what).
- **W10** Styling: component-specific rules leave `src/app.css` for the owning component's `<style>` (`:global(...)` only where the rule targets markup the component doesn't render): `.release-header` (two blocks, merged; `ReleaseTable`/`games/[id]`), `.navbar` + dark variant (Navbar), `.modal.global-search` (GlobalSearch), `.game-card` (+ dark, hover) and `.game-grid-xs` (GameResults), `.app-footer`, `.footer-label`, `main.above-footer`, `body:has(.app-footer) .toast-container` (AppFooter, `:global` where needed), `.page-title` (PageTitle), `.clickable-rows` (`reports/library-size`). A rule used by 2+ components stays global: `.note-markdown` (ReleaseNoteModal, games page), `.job-fixed-table` (jobs pages), `.chevron-toggle` (ZipBrowser, games page), `.missing-metadata-title` (the two missing-* report pages). `app.css` keeps those, theme tokens, Bootstrap overrides (dark outline buttons, `.nav-tabs`, `.list-group-item-action.active`), `.min-w-0` and the `text-bg-<system>` colours; any rule not named here stays. Static `style="…"` attributes become scoped classes (SV6).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Docs | `docs/style-guide.md` (new), `CLAUDE.md`, `AGENTS.md`, `CONTRIBUTING.md` | S1 |
| Tests | `tests/RnRomManager.Api.Tests/CodeStyle/*` (new), `tests/RnRomManager.TestData/RepoPaths.cs` (new) | S2–S4 |
| Api / Core / Data / Systems | ~125 multi-type files split, ~220 signatures wrapped | C1, C2 |
| Core / Systems | `Core/Systems/EmbeddedResource.cs`, `BoxArt.cs`, `HeaderText.cs` (new); 14 `EmbeddedResources.cs` deleted | D1, D2 |
| Api | `Endpoints/JobStart.cs`, `Endpoints/EndpointUser.cs`, `Endpoints/SortQuery.cs` (new), `src/RnRomManager.Api/Problems.cs` | D3 |
| Tests | `RnRomManager.TestData.csproj`, `Systems.Microsoft.Tests` / `Systems.Sony.Tests` csproj | D5 package and project references |
| Data | `Repositories/PagedQuery.cs`, `SqlOrder.cs` (new) | D4 |
| Core / Api | `IngestPipeline`, `LibraryEditor`(+`.External`), `LibraryEndpoints`, `IntegrityJob`, `ReviewResolver` + any file still > 550 | C3 |
| Tests | `TestData/HandlerAsserts.cs`, `TestHandlers.cs` (new) | D5 |
| Web | `lib/types/`, `lib/enums/`, `lib/icons/`, `lib/components/<folder>/`, `lib/api/types.ts` (deleted) | W1–W4 |
| Web | `modals/BaseModal.svelte`, `modals/ConfirmModal.svelte`, 22 extracted modals | W5, W6 |
| Web | `components/forms/*` (new), `lib/sort.ts`, `urlState.ts`, `debounce.ts`, `loadPage.svelte.ts`, `testing/setup.ts` (new), `vite.config.ts` | W7, W8 |
| Web | `src/app.css`, components' `<style>` | W10 |
| Docs | `docs/recipes.md`, `docs/development.md` § Web app structure, `docs/adding-a-system.md` | G3 |

No migrations, config keys or API contract changes.

## Risks
- **One very large PR** (user's choice). Mitigation: each step is one reviewable commit; Implement merges `origin/main` at start and finish, and should also merge it before each phase (steps 3, 11, 19, 24, 30) so conflicts stay small; resolve `docs/codemap.md` by regenerating.
- **Silent behaviour drift** while merging duplicates. Mitigation: G1 parameters, no test edits beyond moves/named rewrites, full suite + e2e in Finish.
- **e2e selectors** rely on ids, labels, roles and some classes. The kit forwards all of them and renders identical markup; e2e runs in Finish.
- **Guard false positives** from the line-based scanner (strings, comments). Mitigation: comment lines ignored; `CodeStyleScannerTests` cover edge cases; a real false positive is fixed in the scanner, never by adding to the baseline.
- **Codemap growth** (~350 more C# files) adds context to codemap greps. Accepted: it lists one name per file.
- **DI changes** from C3 extractions: new classes registered in `AppHost`; `ApiFactory`-based integration tests catch missing registrations.

## Steps
Moved or renamed files count as unchanged lines for the ~300-line step size; content edits count.

Phase 0: foundations
- [ ] **1. Style guide**: S1. Files: `docs/style-guide.md` (new, Appendix A), `CLAUDE.md`, `AGENTS.md`, `CONTRIBUTING.md`. Tests: none (docs). Done when: the guide matches Appendix A; the three pointers exist; codemap regenerated.
- [ ] **2. Guard tests + baseline**: S2–S4. Files: `tests/RnRomManager.Api.Tests/CodeStyle/CodeStyleScanner.cs`, `CodeStyleTests.cs`, `code-style-baseline.txt` (new), `tests/RnRomManager.TestData/RepoPaths.cs` (new), `BrowserCoreExtensionsTests`, `LicenseInfoTests`, `SupportedSystemsTableTests`, `SystemEmulatorsCatalogTests`. Tests: `CodeStyleScannerTests` (new: one positive and one negative sample per rule, `Foo`/`Foo<T>` exception, a delegate, comment lines, a 4-param call not flagged, `return`/`await`/`throw new`/`yield return`/`else` calls with 4 args not flagged, a 3-param declaration not flagged, a declaration that wraps only some parameters flagged, generic `Dictionary<string, int>` commas and a `','` char literal not counted, a self-closing `<ConfirmModal … />` spread over several lines not flagged, a multi-line `<ConfirmModal …>` with a body flagged, `vi.mock('../x')` flagged), `CodeStyleTests`, `BrowserCoreExtensionsTests`, `LicenseInfoTests`, `SupportedSystemsTableTests`, `SystemEmulatorsCatalogTests`. Done when: the baseline lists every current violation; `CodeStyleTests` passes; adding a second class to any file makes it fail.

Phase 1: C# structure (C1, C2) — each step splits types, wraps declarations and removes its `CS1`/`CS2` baseline lines
- [ ] **3. Api**: `src/RnRomManager.Api/**` (incl. `Paging` out of `JobEndpoints.cs`, `ImageUrlRequest` out of `LibraryEndpoints.cs`). Tests: `dotnet test tests/RnRomManager.Api.Tests` (build) + `CodeStyleTests`. Done when: no `CS1`/`CS2` baseline line under `src/RnRomManager.Api/`.
- [ ] **4. Core A**: `Core/{Audit,Backups,Bundles,Collections,Dats,Discovery}`. Tests: Core.Tests filtered to those folders' test classes + `CodeStyleTests`. Done when: no baseline line for those folders.
- [ ] **5. Core Ingest**: `Core/Ingest/**` (incl. `IngestPipeline`'s `IngestItemResult`, `IngestErrors`, `IngestFailures`, `ExternalIngestHooks`, and `IngestPipeline`'s 7 nested types `HashOutcome`, `Release`, `ExternalSource`, `ExternalPlacement`, `SetPlacement`, `RepairPlan`, `RomHashes`). Tests: `FullyQualifiedName~RnRomManager.Core.Tests.Ingest` + `CodeStyleTests`. Done when: as above.
- [ ] **6. Core Integrity/Jobs/Library**: `Core/{Integrity,Jobs,Library}`. Tests: those namespaces + `CodeStyleTests`. Done when: as above.
- [ ] **7. Core rest**: every remaining `Core` folder (`Media`, `Naming`, `Reports`, `Storage`, `Users`, …). Tests: those namespaces + `CodeStyleTests`. Done when: no baseline line under `src/RnRomManager.Core/`.
- [ ] **8. Data + Systems**: `src/RnRomManager.Data/**` (`LibraryRepositories.cs` → one file per repository plus `JsonStringListHandler.cs`), all `src/RnRomManager.Systems.*`. Tests: Data.Tests (`&Category!=Integration` if Docker is down, then rerun in Finish), the Systems test projects + `CodeStyleTests`. Done when: no baseline line under `src/`.
- [ ] **9. Tests A**: `tests/RnRomManager.Api.Tests`, `tests/RnRomManager.Core.Tests`. Tests: both projects (build + the moved fixtures' classes) + `CodeStyleTests`. Done when: no baseline line for those projects.
- [ ] **10. Tests B**: `tests/RnRomManager.Data.Tests`, `tests/RnRomManager.Systems.*.Tests`, `tests/RnRomManager.TestData`. Tests: those projects + `CodeStyleTests`. Done when: no `CS1`/`CS2` lines left in the baseline.

Phase 2: C# shared helpers (D1–D5)
- [ ] **11. Embedded resources + box art**: D1. Tests: `EmbeddedResourceTests` (new), `BoxArtTests` (new, Core.Tests/Systems: loads a known resource, missing name throws the same exception as today), every `*HandlerTests` box-art test. Done when: no `EmbeddedResources.cs` and no `LoadResource` in `src/RnRomManager.Systems.*`.
- [ ] **12. Header text**: D2. Tests: `HeaderTextTests` (new: NUL stop, `?` substitution, trimStart true/false, empty → null), `GameBoyHeaderReaderTests`, `GameBoyAdvanceHeaderReaderTests`, `NeoGeoPocketHeaderReaderTests`, `Nintendo64HeaderReaderTests`, `Nintendo3DsHeaderReaderTests`, `SnesHeaderReaderTests`, `NintendoDsHandlerTests` (covers `NintendoDsHeaderReader`). Done when: the 7 readers named in D2 call `HeaderText.ReadAscii`.
- [ ] **13. API helpers**: D3. Files: `Endpoints/JobStart.cs`, `Endpoints/EndpointUser.cs`, `Endpoints/SortQuery.cs` (new), `src/RnRomManager.Api/Problems.cs`, the endpoint files using them, `docs/recipes.md`. Tests: `JobStartTests`, `SortQueryTests` (new), `ProblemsTests` (new), `AdminEndpointsTests`, `DatEndpointsTests`, `IngestEndpointsTests`, `IntegrityEndpointsTests`, `LibraryEndpointsTests`, `ReportEndpointsTests`, `DiscoveryEndpointsTests`, `CollectionEndpointsTests`, `BundleQueueEndpointsTests`, `BundleDownloadEndpointsTests`, `DiscoveryLogoEndpointsTests`, `RomStorageEndpointsTests`, `ZipCompressionEndpointsTests`. Done when: `DatEndpoints.JobRunning`/`UserId` and `CollectionEndpoints.UserId` are gone; no single-entry `new Dictionary<string, string[]>` passed to `Problems.Validation`; the 5 D3 `dir` sites call `SortQuery.ParseDir`.
- [ ] **14. Data helpers**: D4. Tests: `Data.Tests` query classes for each touched repository (`ReportRepository`, `JobRepository`, `AuditRepository`, `CollectionRepository`, `IngestReviewRepository`, `IntegrityIssueRepository`, `GameRepository`) + `PagedQueryTests` (new, integration). Done when: the 14 sites call `QueryPageAsync`.
- [ ] **15. Handler test helpers**: D5 `HandlerAsserts` + the TestData package refs and the Microsoft/Sony project refs. Files: `tests/RnRomManager.TestData/HandlerAsserts.cs` (new), `RnRomManager.TestData.csproj`, the two csproj files, `tests/RnRomManager.Systems.*.Tests/*HandlerTests.cs`. Tests: all Systems test projects. Done when: no handler test reads PNG width/height by hand.
- [ ] **16. Temp dirs**: `TempDir` in `ApiFactory`, `AuditLogTests`, `CoverCacheTests`, `ExternalRomMoveTests`, `FileNameRulesTests`, `StorageFoldersTests`. Tests: those classes (Api integration tests run via `ApiFactory`: `EndpointRoleMatrixTests` as a smoke check). Done when: none of the 6 makes temp folders by hand (`EffectiveSettingsTests` and `OptionsRegistrationTests` only build path strings and stay).
- [ ] **17. Handler fakes, Core.Tests**: D5 `TestHandlers.Fake` (new) adopted in `tests/RnRomManager.Core.Tests`. Tests: every Core.Tests class whose file changed. Done when: `grep "Substitute.For<ISystemHandler>" tests/RnRomManager.Core.Tests` finds nothing that only sets `Id`/`EmbedRomsInZip` by hand.
- [ ] **18. Handler fakes, Api + Data tests**: same in `tests/RnRomManager.Api.Tests` (`BrowserCoreExtensionsTests`, `GameQueryParsingTests`, …) and `tests/RnRomManager.Data.Tests`. Tests: every class whose file changed. Done when: as step 17 for those projects.

Phase 3: C# files over 500 lines (C3)
- [ ] **19. IngestPipeline**: `IngestPipeline`'s constructor, public members (`ProcessAsync`, `ProcessSetAsync`, `StoreSetReleaseAsync`, `RecordArchiveDeleted`) and test seams (`ExternalHooks`, `WrapEmbeddedReadStream`) stay unchanged; it builds new internal collaborators itself (no DI change): `ExternalIngest` (`ProcessExternalAsync` and helpers), `IngestIdentifier` (`IdentifyAsync`, `MatchWbfs`, the release helpers), `SetIngest` (`ProcessSetCoreAsync` … `RepairLoneTrackAsync`) in `Core/Ingest/`. Tests (no edits needed): `IngestPipelineSerialTests`, `IngestPipelineSetReleaseTests`, `IngestExternalPreparationTests`, `BoundedRomReadTests`, `IngestSideFolderTests`, `IngestJobTests`, `ReviewResolveJobTests`, `ReviewResolverTests`, `ReviewResolverExternalTests`, `ExternalLibraryTests`, Api `Integration/IngestPipelineTests`. Done when: every file ≤ 500 lines; those tests pass unchanged.
- [ ] **20. LibraryEditor**: `LibraryEditor`'s constructor and public methods stay unchanged (facade, as in steps 19 and 22); it builds internal collaborators itself (no DI or endpoint change) and its moved public methods become one-line forwards. `ReleaseEditor` (new): `DeleteReleaseAsync`, `SetPrimaryReleaseAsync`, `EditReleaseAsync`, `MoveReleaseAsync`, `MergeAsync`. `ImageEditor` (new): `AddImageAsync`, `DeleteImageAsync`, `SetCoverAsync`, `RetryMediaAsync`. `SaveEditor` (new): the methods of the `LibraryEditor.Saves.cs` partial (deleted). `ExternalLibraryEditor` (new): the methods of `LibraryEditor.External.cs` (deleted). `LibraryEditSupport` (new, internal): shared helpers (`Writer`, `WriteFailure`, `NewGameForAsync`, …). `LibraryEditor` keeps everything else (patch, notes, delete game, title/IGDB edits, collection operations). Tests (no edits needed): `LibraryEditTests`, `LibraryImageEditTests`, `LibraryMoveMergeTests`, `LibraryEditorCollectionTests`, `SaveCarryTests`, `PullCoversEndpointTests`, `IgdbSyncEndpointTests`, `TitleDbIngestTests`, `ExternalLibraryTests`, `LibraryEndpointsTests`, `SaveEndpointsTests`, `ReportEndpointsTests`. Done when: every file ≤ 500 lines; `LibraryEditor` is not `partial`; those tests pass unchanged.
- [ ] **21. LibraryEndpoints**: keep route mapping in `LibraryEndpoints`; extract `LibraryImageEndpoints` (image routes and helpers), `LibraryQueryParsing` (`ParseQuery`, `ParseList`, `ParseCollection`, `Validate`), `LibraryDtos` (`ToListItemDto`, `ToDetail`, `ImageUrl`, `CoverUrl`). Routes and responses unchanged. Source callers updated: `DiscoveryEndpoints`, `TimelineEndpoints`, `ReportEndpoints`. Allowed test rewrites (call target only): `GameQueryParsingTests`, `MoveRequestValidationTests`. Tests: those + `LibraryEndpointsTests`, `EndpointRoleMatrixTests`, `DiscoveryEndpointsTests`, `TimelineEndpointsTests`, `ReportEndpointsTests`. Done when: every file ≤ 500 lines; codemap lists the same routes.
- [ ] **22. IntegrityJob**: extract `ZipIntegrityCheck` (`CheckZipAsync` and helpers), `ExternalIntegrityCheck` (`CheckExternalAsync`, the external-checks cache), `ImageCacheSweep` (`SweepImageCache`, `Remove`, `FileKey`, `Claimants`), built inside `IntegrityJob` (constructor unchanged). Tests: `IntegrityJobTests` + `ZipIntegrityCheckTests`, `ExternalIntegrityCheckTests`, `ImageCacheSweepTests` (new). Done when: every file ≤ 500 lines.
- [ ] **23. Remaining large files**: re-measure `src/**/*.cs`; split `ReviewResolver` and every other file > 550 lines the same way (public surface unchanged, collaborators built inside); files 501–550 may stay (listed in the commit message). Tests: the split classes' existing tests. Done when: no `src` file > 550 lines.

Phase 4: SPA structure (W1–W5); each step removes or renames (S3) its `SV*` baseline lines
- [ ] **24. API types**: W2 for `lib/api/types.ts` (64 → `lib/types/`, 5 unions → `lib/enums/`), imports updated. Tests: `npm run lint`, `npm run check`, `npx vitest run`. Done when: `lib/api/types.ts` gone; `SV3` clean.
- [ ] **25. Other types + enums**: W2 for the 59 exported types in `lib/*.ts`/`lib/stores/*.ts`; `SortDir` de-duplicated. Tests: lint, check, vitest for the touched modules (`discovery.test.ts`, `libraryQuery.test.ts`, `bundleDownload.test.ts`, `systemEmulators.test.ts`, `theme.svelte.test.ts`, …). Done when: no `SV2` baseline line.
- [ ] **26. Icons**: W3; `docs/recipes.md`, `docs/development.md`, `docs/adding-a-system.md` paths. Tests: lint, check, vitest. Done when: no `lib/components/icons.ts`.
- [ ] **27. Component folders**: W4 moves of existing components with their tests (`git mv`), updating only the imports of moved files; baseline paths renamed (S3). Today's `ConfirmModal.svelte` moves to `modals/ConfirmModal.svelte` here unchanged. Tests: lint, check, full `npx vitest run`. Done when: `lib/components` holds only folders.
- [ ] **28. No `../` imports**: W1 rewrite of every `../` import, `import('../…')` and `vi.mock('../…')` across `src/`. Tests: lint, check, full `npx vitest run`. Done when: no `SV5` baseline line.
- [ ] **29. BaseModal + ConfirmModal**: W5; `modals/ConfirmModal.svelte` renamed to `modals/BaseModal.svelte` and every use switched to `BaseModal` (baseline `SV1` lines keep their rule and path); new message-only `modals/ConfirmModal.svelte`; `docs/development.md` (Modals M1, components list) and `docs/recipes.md` (Confirmations) updated. Tests: `modals/BaseModal.test.ts` (renamed from `ConfirmModal.test.ts`), `modals/ConfirmModal.test.ts` (new: title, message, confirm callback, busy). Done when: all modal tests pass.

Phase 5: SPA kit, modals, DRY and styling
- [ ] **30. Shared SPA logic**: W8 `lib/sort.ts`, `lib/urlState.ts`, `lib/debounce.ts`, `lib/loadPage.svelte.ts`, `lib/testing/setup.ts` + `vite.config.ts` `setupFiles`. `pageParam` replaces the inline parsing in `lib/discovery.ts`, `lib/libraryQuery.ts`, `lib/reviewFilter.ts`, `lib/timeline.ts`, `lib/imageResolutions.ts`, `routes/admin/audit/+page.svelte`, `routes/collections/+page.svelte` and `routes/reports/{hacks,missing-metadata,missing-igdb-ids,multiple-releases}/+page.svelte`; `parseDir` replaces the dir checks in `lib/discovery.ts`, `lib/libraryQuery.ts` and those 4 report pages (whose local `type Dir` goes). Tests: `sort.test.ts`, `urlState.test.ts`, `debounce.test.ts`, `loadPage.svelte.test.ts` (new) + the touched modules' tests + `reports-pages.test.ts`. Done when: the listed sites call the helpers.
- [ ] **31. Form kit**: W7 inputs (`FormField` … `RadioButtons`). Tests: `forms/<Name>.test.ts` (new, one per component: markup classes, binding, `...rest` forwarding incl. an event handler, `bind:ref`, label association). Done when: kit tests pass.
- [ ] **32. Button**: W7 `Button`. Tests: `forms/Button.test.ts` (variants, no-variant mode renders only `class`, `busy` spinner and disabled, `href` anchor, default `type="button"`). Done when: tests pass.
- [ ] **33. Extract modals**: W6 in three commits sharing this step's tick (33a admin, 33b games, 33c the rest); new modals use the kit from birth. Tests: each extracted modal gets `<Name>Modal.test.ts` (opens, confirm callback with its data, cancel) + the source page/component tests (`admin-pages.test.ts`, `GameSaves.test.ts`, `GameResults.test.ts`, `reports-pages.test.ts`, …). Done when: no `SV1` baseline line.
- [ ] **34. Shared components**: W8 `SortHeader`, `NavTabs`, `ViewToggle`, `DataTable` (all built on the kit), `ErrorAlert` extension; adopt `SortHeader` in the 4 report pages, `DiscoveryGroups`, `SystemList`; `NavTabs` in the 9 tab strips (`GameAdminModal`, `RomToolsTabs`, `routes/admin/+layout`, `collections/+page`, `games/[id]`, `reports/+layout`, `reports/activity`, `reports/newly-added`, `review/[id]`); `ViewToggle` in `routes/+page.svelte` and `collections/[id]`; `ErrorAlert` in the 6 raw sites. Tests: new components' tests + `reports-pages.test.ts`, `DiscoveryGroups.test.ts`, `SystemList.test.ts`, `RomToolsTabs.test.ts`, `admin-pages.test.ts`. Done when: no raw `nav nav-tabs` outside `NavTabs`.
- [ ] **35. Adopt kit + DataTable: admin**: `routes/admin/**`. Tests: admin page tests. Done when: no `SV4` line under `routes/admin`.
- [ ] **36. Adopt: reports, jobs, integrity**: `routes/{reports,jobs,integrity}/**`. Tests: their page tests. Done when: as above.
- [ ] **37. Adopt: library, collections, timeline, bundles, login**: `routes/{+page,collections,timeline,bundle-creator,login,change-password,…}` + `discovery/`, `systems/`, `bundles/`. Tests: their tests. Done when: as above.
- [ ] **38. Adopt: games, ingest, review, deleted**: `routes/{games,ingestion,review,deleted}/**`, `games/`, `ingest/`. Tests: their tests. Done when: as above.
- [ ] **39. Adopt: modals, layout, common**: the rest. Tests: those components' tests. Done when: no `SV4` baseline line.
- [ ] **40. Large components**: W9. Tests: `GameHeader.test.ts`, `ReleaseTable.test.ts`, the four `Review*Tab.test.ts` (new) + existing game/review page tests. Done when: no `.svelte` file > 500 lines, or the commit names the exception and why.
- [ ] **41. Styling**: W10; `docs/development.md` styling bullet updated. Tests: lint, check, vitest. Done when: no `SV6` baseline line; the baseline holds only its header; e2e (axe dark/light) passes in Finish.

## Decisions
- 2026-11-26 Which C# types does one-per-file cover? → all types (class, record, struct, interface, enum), no nesting; `Foo`/`Foo<T>` may share (user)
- 2026-11-26 Do the C# rules apply to tests? → one-type-per-file and wrapping yes; 500-line guideline no (user)
- 2026-11-26 What does the 4+ parameter rule cover? → every declaration's parameter list; call sites untouched (user)
- 2026-11-26 Enforce automatically? → guard tests (`CodeStyleTests`); 500 lines stays a guideline (user)
- 2026-11-26 Modal layer? → `BaseModal` (renamed `ConfirmModal`) + new message-only `ConfirmModal` allowed inline; every other modal its own file (user)
- 2026-11-26 Component folders? → role + feature folders (user)
- 2026-11-26 Form kit scope? → inputs and a `Button` component adopted everywhere (user)
- 2026-11-26 Enums? → string-literal unions in `lib/enums/`, types in `lib/types/`, icons in `lib/icons/` (user)
- 2026-11-26 Packaging? → one spec, one branch, many steps (user)
- 2026-11-26 Guards with existing violations? → shrinking baseline that also fails on stale entries (user)
- 2026-11-26 Splitting big C# files? → extract collaborator classes; `partial` not the default (user)
- 2026-11-26 Which duplication hotspots? → all 23 found by the sweep (user)
- 2026-11-26 Divergent edge cases when merging duplicates? → keep current behaviour via parameters (user)
- 2026-11-26 Import style for moved SPA code? → `#lib/…` as `docs/development.md` already prescribes (code)
- 2026-11-26 Review fact fixes: guard baseline rules for moves and renamed modals, CS2 multi-line and call detection, kit/Button before modal extraction, D2 drops Latin-1 readers, IngestPipeline/LibraryEditor/IntegrityJob keep their public surface (facades), W10 owners, test class names → applied (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-26-code-hygiene-cleanup.md
```
Starts the spec, or resumes it at the first unticked step.

## Appendix A: docs/style-guide.md

# Style guide

Rules for every change, human or agent. Structural rules are enforced by `CodeStyleTests` (`tests/RnRomManager.Api.Tests/CodeStyle`); never add to its baseline: remove lines you fix, and rename a line's path only when you move that file. Where to make common changes: [recipes.md](recipes.md).

## General
- Smallest complete change; reuse an existing helper or component before writing one (grep `docs/codemap.md`). Duplicate logic twice → extract it.
- Keep files under ~500 lines (guideline; test files exempt). Split by responsibility, not by region.
- Comments explain why, not what. Cite the spec rule a behaviour comes from (`/** B12: … */`).
- No behaviour change hides in a refactor; every behaviour change gets tests and its doc in the same PR ([CONTRIBUTING.md](../CONTRIBUTING.md)).
- Never commit secrets or dev passwords.

## C#
- One type per file (class, record, struct, interface, enum), named after the type; no nested types. Only `Foo` and `Foo<T>` may share a file.
- File-scoped namespace matching the folder; `using`s outside it, `System` first (`.editorconfig`).
- 4+ parameters in a declaration (constructor, primary constructor, record, method, local function): one per line, indented one level, `)` on the last parameter's line:
  `public sealed class IngestJob(` / `    DatCache dats,` / … / `    ILogger<IngestJob> logger) : IJob`
- Classes are `sealed` unless designed for inheritance; DI services use primary constructors.
- Nullable is on and warnings are errors: avoid the `!` operator in new code (test fields `= null!` are fine).
- Async methods end in `Async` and take a `CancellationToken` last.
- Private fields `_camelCase`; constants and static readonly `PascalCase`.
- Interfaces live in Core, Dapper implementations in `Data/Repositories`; use `QueryPageAsync` for paged lists.
- API errors are ProblemDetails via `Problems` (`Validation`, `NotFound`, `Forbidden`, `Create`); a busy job lock is `JobStart.Running(ctx)`; start manual jobs with `JobStart.StartManualAsync`.
- System handlers load resources with `EmbeddedResource`/`BoxArt` and decode header text with `HeaderText`.
- Tests: NUnit `Assert.That` + NSubstitute, `<Class>Tests` in the mirrored folder, `TempDir` for files, `TestHandlers.Fake` for handlers, `[Category("Integration")]` for Testcontainers.

## Svelte / TypeScript
- Svelte 5 runes: `interface Props` + `$props()`, `$state`, `$derived`, `$bindable` for two-way props.
- Prettier: tabs, single quotes, width 100. `npm run lint` and `npm run check` must pass.
- Imports: `#lib/…` (TS modules with `.js`), or `./` within one folder; never `../`.
- Layout of `src/lib`: `types/` and `enums/` (one exported type per file, re-exported by `index.ts`; enums are string-literal unions), `icons/` (`Icon` + `icons.ts`), `stores/` (`*.svelte.ts`), `components/<folder>/`: `modals`, `layout`, `forms`, `common`, `games`, `discovery`, `systems`, `bundles`, `ingest`, `review`. Exported types live nowhere else.
- Components stay under ~500 lines; extract sections into the feature folder.
- Modals: every modal is `components/modals/<Name>Modal.svelte` built on `BaseModal`. Only `ConfirmModal` (title + message) is used inline.
- Forms: use `components/forms` (`FormField`, `TextInput`, `SearchInput`, `NumberInput`, `FileInput`, `Textarea`, `Select`, `Checkbox`, `Switch`, `RadioButtons`, `Button`); no raw `<input>`, `<select>`, `<textarea>` or `<button>` elsewhere. Every control has a label (`hiddenLabel` for visually hidden).
- Reuse: `DataTable` for tables, `SortHeader` for sortable columns, `NavTabs` for tab strips, `Pagination`, `ViewToggle`, `ErrorAlert`, `EmptyState`, `Loading`, `FileSize` for sizes, `formatCount` for numbers, `AdminOnly` for gating, the `toasts` store, `lib/sort.ts`, `lib/urlState.ts`, `lib/debounce.ts`, `useAbortableGet`.
- Styling: Bootstrap utilities first; component styles in the component's `<style>`; `src/app.css` only for theme tokens, Bootstrap overrides and system colours; no static `style="…"` (dynamic values only).
- Icons through `<Icon name>` only; Font Awesome for brands only.
- Tests sit next to their source as `*.test.ts` (rune stores `*.svelte.test.ts`), vitest; shared mocks in `lib/testing/setup.ts`.
