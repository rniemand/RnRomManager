---
title: Game page edits
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-23
---

# Game page edits

## Summary
Four small Admin quality-of-life changes on the Game page: adding the first real box art to an **external** game no longer starts an `Integrity (Game)` job (which re-hashes every external file, slow for disc images) but resolves the `DefaultBoxArt` issue directly; the game title becomes click-to-edit; the Edit metadata modal is renamed **Edit Metadata** and submits on Enter; and the Suggest Images dialog closes once a picked image is added.

## Out of scope
- Saves: they never start a game check today (only `POST /api/games/{id}/images` and `/images/from-url` do, through `StartGameCheckAsync`), so nothing changes for them.
- The game check for embedded (non-external) games stays as it is (B14/B15 of earlier specs).
- No new API route; the inline title edit uses the existing `PATCH /api/games/{id}`.
- Other modals' Enter handling.

## Behaviour

### External game check (G)
- **G1** In `LibraryEndpoints.StartGameCheckAsync`, when the edit replaced the default cover (`LibraryEditResult.ReplacedDefaultCover`) and the game's system is external (`ISystemRegistry.ById(row.SystemId) is { EmbedRomsInZip: false }`, as `LibraryEditor.IsExternal` decides), no `Integrity (Game)` job is started. Instead the endpoint takes the job lock with `JobLock.TryAcquire()` and, while holding it, resolves that ZIP's open `DefaultBoxArt` issue with the new `IIntegrityIssueRepository.ResolveOpenAsync(row.ZipPath, IntegrityIssueType.DefaultBoxArt, <TimeProvider UTC now>, CancellationToken.None)`.
- **G2** Lock busy (`TryAcquire()` returns null): nothing is resolved; log Information `"Resolving DefaultBoxArt of {ZipPath} skipped: another job is running"` on the existing `RnRomManager.Api.Endpoints.GameCheck` logger. The issue is resolved by the next Quick or Deep run, as for a skipped game check today.
- **G3** Any exception is caught and logged as the existing `"Game check of game {GameId} could not start"` warning; the response is the game detail whatever happens (unchanged contract).
- **G4** `ResolveOpenAsync` (new) runs one `UPDATE integrity_issues SET status = 'Resolved', resolved_at = @resolvedAt WHERE status = 'Open' AND zip_path = @zipPath AND type = @type AND entry_name IS NULL` (a `DefaultBoxArt` finding has no entry name, so it's stored as `NULL`; `ListOpenAsync` can't match it). Returns the number of rows resolved. No audit row (the game check never wrote one either).
- **G5** Embedded games: unchanged — the `Integrity (Game)` job still runs.

### Inline title edit (T)
- **T1** Admin only. For non-Admins the `<h1 class="h3 mb-0">{game.displayTitle}</h1>` in `routes/games/[id]/+page.svelte` is unchanged.
- **T2** For Admins the heading's content is a `<button type="button" class="btn btn-link p-0 text-reset text-decoration-none title-edit">` (new scoped class `title-edit`: `font: inherit`) whose text is `game.displayTitle` followed by `<Icon name="edit" />` with class `ms-2 text-body-secondary small`. The icon is shown only on hover or keyboard focus (`opacity: 0` → `1`; always visible on touch devices via `@media (hover: none)`). The button has `use:tooltip={'Click to edit the title'}` (Bootstrap sets the button's `aria-describedby` while the tooltip shows) and no other description, so the heading's accessible name stays `game.displayTitle` (the e2e `openGame` helper finds the game by that heading name).
- **T3** Clicking the button replaces the heading with a `<form>` holding one `<input class="form-control form-control-lg" aria-label="Title" maxlength="255">` prefilled with the raw `game.title` (not `displayTitle`: a trailing ", The" stays where the user typed it, as in Edit Metadata). The input is focused with its text selected.
- **T4** **Enter** or **blur** saves; **Escape** cancels (restores the heading, no request). A trimmed value equal to `game.title` exits edit mode without a request. Each edit is handled once: a `handled` flag set by Enter, Escape or the first blur makes any later blur (from the input being disabled or removed) a no-op, so Enter-then-blur sends one PATCH and Escape-then-blur sends none. The flag resets whenever edit mode stays open: on reopening, after a client validation failure (T5) and after a failed save (T7).
- **T5** Client validation uses `titleError` from `src/lib/gameEdit.ts`. Invalid: the input gets `is-invalid` with the message in an `invalid-feedback` div below it, and edit mode stays open (also on blur).
- **T6** Save sends `PATCH /api/games/{id}` with body `{ "title": "<trimmed>" }` only (the endpoint's `ParsePatch` takes partial bodies). While it runs the input is `disabled`. Success: `GameTitle` shows the toast `Saved.` (success, same as Edit Metadata), calls `onsaved(detail)` and the heading returns. Renaming also renames the ZIP, as in Edit Metadata.
- **T7** Errors: `ValidationFailed` with `errors.title` → shown as in T5, edit mode stays; job lock busy → `toasts.jobRunning(e)` (its existing toast) and edit mode closes; 404 → edit mode closes and `onstale()` is called; anything else (e.g. `409 NameTaken`) → danger toast with `e.title` (or `Something went wrong.` for a non-API error), edit mode stays with the typed value.
- **T8** Implemented as `src/lib/components/GameTitle.svelte` (new), props `game: GameDetail`, `onsaved: (detail: GameDetail) => void`, `onstale: () => void`; the page renders it for Admins in place of the `<h1>` as `<GameTitle {game} onsaved={setGame} onstale={() => onAdminDone({ reload: true, message: changedMessage, warning: true })} />` (`setGame`, `onAdminDone` and `changedMessage` already exist in the page; `setGame` guards against a route change, as `RatingControl` uses it).

### Edit Metadata (M)
- **M1** "Edit metadata" becomes **Edit Metadata** everywhere it's user-visible: the modal title in `GameAdminModal.svelte` (`edit: 'Edit Metadata'`, so the confirm button label also reads "Edit Metadata"), the Manage menu item and the Quick edit tooltip in `routes/games/[id]/+page.svelte`. Code comments mentioning it are updated too.
- **M2** In the Edit Metadata form, **Enter** in an `<input>` (Title, Release year) submits; in a `<textarea>` (Developers, Publishers, Genres, Description) Enter keeps inserting a newline and **Ctrl+Enter** or **⌘+Enter** submits. Implemented as an `onkeydown` on the existing `<form>`: on Enter with `!e.isComposing`, if the target is an `INPUT`, or a `TEXTAREA` with `ctrlKey || metaKey`, call `e.preventDefault()` and `void confirm()` (which already ignores a second call while `busy`).
- **M3** A new `<div class="col-12 form-text">` after the Description field reads `Press Enter to save (Ctrl+Enter in a multi-line field).`

### Suggest Images (S)
- **S1** In `GameImages.svelte` `addSuggestion`, a successful add (after `onchanged` and the `Image added from IGDB.` toast) sets `suggestOpen = false` when `seq === loadSeq` (the same opening and type). An add that finishes after the dialog was closed or reopened still refreshes and toasts but never closes the reopened dialog.
- **S2** On error the dialog stays open with the existing toasts (unchanged).
- **S3** The per-tile **Added** state is removed (`added` state, the `Added` badge and the `Added …` tile label): with S1 a tile can't be seen after a successful add. This replaces the earlier S13 "stays open, marked Added" rule.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Integrity/IIntegrityIssueRepository.cs` | `Task<int> ResolveOpenAsync(string zipPath, string type, DateTime resolvedAt, CancellationToken)` (new), G4 |
| Data | `src/RnRomManager.Data/Repositories/IntegrityIssueRepository.cs` | Implement `ResolveOpenAsync` (G4) |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | `StartGameCheckAsync`: G1–G3; the two image routes pass `ISystemRegistry`, `JobLock`, `IIntegrityIssueRepository`, `TimeProvider`; doc comment updated |
| Web | `src/RnRomManager.Web/src/lib/components/GameTitle.svelte` (new) | T2–T8 |
| Web | `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte` | Render `GameTitle` for Admins (T1, T8); "Edit Metadata" menu item and tooltip (M1) |
| Web | `src/RnRomManager.Web/src/lib/components/GameAdminModal.svelte` | M1 title; M2 `onkeydown`; M3 hint |
| Web | `src/RnRomManager.Web/src/lib/components/GameImages.svelte` | S1, S3 |
| Docs | `docs/features.md` | Header/Quick edit (line ~130), Suggest Images (~164), Edit Metadata (~178), Game checks (~333) |
| E2E | `tests/e2e/specs/mvp.spec.ts` | "Edit metadata" → "Edit Metadata" (lines ~2133, 2436, 2440); inline title edit flow |

No migration, config key or API contract change.

## Risks
- **Race with a running integrity job (G1):** resolving only while holding `JobLock` keeps a Quick/Deep run from re-opening the issue from a stale scan, the same guarantee the game check had.
- **Inline rename renames the ZIP:** same as Edit Metadata; blur-saves (T4) could rename by accident, mitigated by "unchanged value → no request" and Escape.
- **Existing tests:** `game-admin.test.ts` queries `/Edit metadata/` (lines 148, 166, 232, 499, 506, 508, 519, 532) and `mvp.spec.ts` (2133, 2436, 2440); `game-images.test.ts` asserts the dialog stays open and the Added badge (859–889, 891–912, 930). All change as listed in the steps.

## Steps
- [ ] **1. Skip the game check for external games**: G1–G5. Files: `IIntegrityIssueRepository.cs`, `IntegrityIssueRepository.cs`, `LibraryEndpoints.cs`, any test fake implementing `IIntegrityIssueRepository` (grep). Tests: `IntegrityIssueRepositoryTests` (new, `tests/RnRomManager.Data.Tests/Database/`, set up as `IntegrityJobTests` does: resolves only the open `DefaultBoxArt` row with `entry_name IS NULL` of that ZIP, leaves other types/ZIPs/resolved rows alone, returns the count); `ExternalLibraryTests` (new partial `ExternalLibraryTests.GameCheck.cs`: before the upload insert, through `IIntegrityIssueRepository.InsertAsync`, an Open `DefaultBoxArt` row (`entry_name` NULL) and an Open `MissingBoxArt` row on the external game's ZIP; the first real box art then starts no `Integrity`/`Game` job, `DefaultBoxArt` is Resolved and `MissingBoxArt` stays Open). G2 (lock busy) is covered in `ImageEndpointsTests` (new test `ExternalGame_BusyLock_ResolvesNothing_G2`), whose `TestServices` also registers `ExternalTestHandler` (`SystemHandlerRegistration.AddSystemHandler<ExternalTestHandler>`, as `ExternalLibraryTests` does) and a test `IGameRepository` decorator (new, private in that file). While a `_lockBeforeResolve` flag is set and `_held` is null, every `GetAsync` calls `JobLock.TryAcquire()` and stores a non-null handle in `_held` (the editor's own `GetAsync` inside `AddImageAsync` runs while it holds the lock, so that try returns null; `StartGameCheckAsync`'s later call succeeds); `TearDown` (which already disposes the B14 hook's `_held`) also sets `_held = null`, and `SetUp` clears `_lockBeforeResolve` next to `_lockBeforeGameCheck`, so test order doesn't matter. The test seeds an external game as `ExternalLibraryTests` does; it asserts 200 with the detail and the `DefaultBoxArt` row still Open; existing `ImageEndpointsTests` and `ImageUrlEndpointsTests` B14 tests still pass (embedded games). Done when: the targeted classes pass; no `jobs` row of mode `Game` for the external case.
- [ ] **2. Edit Metadata rename and Enter to submit**: M1–M3. Files: `GameAdminModal.svelte`, `routes/games/[id]/+page.svelte` (menu item, tooltip, comment), `lib/components/ConfirmModal.svelte` and `GlobalSearch.svelte` comments. Tests: `src/routes/games/game-admin.test.ts` (update every `Edit metadata` query to `Edit Metadata`; new: Enter in Title submits the PATCH; Enter in Genres doesn't; Ctrl+Enter in Genres submits; the hint text is shown). Done when: lint, check and that test file pass.
- [ ] **3. Suggest Images closes on success**: S1–S3. Files: `GameImages.svelte`. Tests: `src/routes/games/game-images.test.ts` — rewrite "a tile adds itself … the dialog stays open" to assert the dialog closes after success (request body, disabled-while-adding and toast assertions kept); delete "Added marks reset on a type change and on reopening"; in "an add that finishes after close and reopen…" replace the Added assertion with "the reopened dialog is still open"; "a duplicate is toasted…" also asserts the dialog stays open. Done when: lint, check and that test file pass.
- [ ] **4. Inline title edit**: T1–T8. Files: `GameTitle.svelte` (new), `routes/games/[id]/+page.svelte`. Tests: `src/routes/games/game-title.test.ts` (new): non-Admin sees a plain heading; Admin click shows the input with the raw title selected; Enter and blur send `PATCH` `{ title }` and show the new display title + `Saved.` toast; Enter then blur sends one PATCH; Escape (then blur) sends none; unchanged value sends nothing; invalid title shows `Use 1–255 characters.` and keeps edit mode; `ValidationFailed` and `NameTaken` handling; job-running and 404 paths. Done when: lint, check and the new test file pass.
- [ ] **5. Docs and e2e**: `docs/features.md` sections listed in Changes; `tests/e2e/specs/mvp.spec.ts` label updates plus, in "Admin: edit metadata, move a release…", right after `openGame("Orbit Blocks")`, an inline rename to `Orbit Blocks Deluxe` (click the heading button, fill, Enter, assert heading `Orbit Blocks Deluxe` and toast `Saved.`) and back to `Orbit Blocks` the same way (blur this time), so every later assertion on the Orbit Blocks names is unchanged and saving Edit Metadata with Enter in Release year instead of clicking. Done when: `pwsh scripts/test-e2e.ps1` passes in Finish.

## Decisions
- 2026-11-23 External game check replacement → skip the job and resolve `DefaultBoxArt` directly (user)
- 2026-11-23 Inline title edit on blur → blur saves; Escape cancels (user)
- 2026-11-23 Enter in Edit Metadata → Enter in inputs; Ctrl/⌘+Enter in textareas (user)
- 2026-11-23 Suggest Images auto-close → close on successful add; stay open on error; drop the Added badge (user)
- 2026-11-23 Saves start no game check today → nothing to change for saves (code)
- 2026-11-23 Review round 1: Bootstrap tooltip owns `aria-describedby` (hidden help span dropped); blur guarded by a `handled` flag; G2 test hook named; issue seeding pinned; page wiring and toast owner pinned; e2e rename goes back to the original title (reviewer)
- 2026-11-23 Review rounds 2–3: G2 decorator retries until it gets the lock, registers `ExternalTestHandler`, and `_held` is nulled in `TearDown`; the `handled` flag also resets after a T5 validation failure (reviewer)
- 2026-11-23 "Edit Metadata" capitalisation → applied to the modal, menu item and tooltip for consistency (code)

## Kickoff prompt
```text
/spec implement 2026-11-23-game-page-edits.md
```
Starts the spec, or resumes it at the first unticked step.
