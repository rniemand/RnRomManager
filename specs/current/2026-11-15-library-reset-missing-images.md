---
title: Library Reset placement and missing-image shortcut
status: in-progress
created: 2026-11-15
---

# Library Reset placement and missing-image shortcut

## Summary

Library users can reset filters from the filter row itself, and any signed-in user can step through the Missing Images report with a global shortcut that opens the next game still missing images. Split out of the earlier combined "Library curation and ROM bundles" spec.

## Out of scope

- Changes to who may edit game images.
- New report filters or report columns.
- Collections discovery and Bundle Creator (separate specs).

## Behaviour

- **B1.** Move the existing **Reset** action into the Library's filter row, next to the filter control on wide and narrow layouts. Preserve its current visibility condition, chip display, state clearing, page reset, and URL and preference semantics. Keep both controls focusable and usable at 320 CSS pixels.
- **B2.** Add **Next missing image**, action ID `reports.missingImage.next`, default `Alt+Shift+M`, role null (every signed-in user, as Reports is), to the existing shortcut editor. Apply existing shortcut validation, precedence, disable and reset behaviour. Append the action after existing actions in `Hotkeys.Actions` and `hotkeys.ts`.
  - If an allowed stored override occupies `Alt+Shift+M` and the new action has no override, the new action is returned as unassigned (null) for that user. The older override is preserved.
  - A PUT omitting the new action uses the same fallback. A PUT that explicitly sets the new action to a conflicting binding gets the normal duplicate error.
  - `GET` and `PUT /api/auth/me/hotkeys` return `implicitUnassigned: string[]` alongside the existing `bindings`, listing the IDs suppressed by this rule.
  - The editor omits an implicitly unassigned action from PUT unless its control was edited, so unrelated saves leave the fallback implicit. A deliberate disable still saves null. **Reset all** frees the default by resetting the older binding as well.
- **B3.** Pressing the shortcut fetches a single live result from B4 in the report's unfiltered order (`system_id, display_title, id`), regardless of the displayed report filters.
  - On a game page, the current game is the anchor (its sort position is used even if it has just been fixed), and the first report row strictly after it is selected. When there is no later row, wrap to the first report row other than the current game. Elsewhere, start at the first row.
  - Open the selected game with a normal game link.
  - If the only remaining result is the current game, stay there and show "This game is the only one still missing images." An empty report shows "No more games are missing images." Network or server failure shows a separate error toast.
  - Coalesce overlapping presses and drop stale responses after navigation or session change.
  - Fixing images on the opened game still follows the existing `LibraryEditor` policy; users without it can open and view the game but not change its images.
- **B4.** Signed-in `GET /api/reports/missing-images/next?afterGameId=<guid>` returns `{ id: string | null, currentIsOnly: boolean }`. `afterGameId` is optional; an absent or deleted anchor starts at the first row. Share the report's predicate and SQL ordering, including its `id` tie-break, with `ListMissingImagesAsync`. Implement with a bounded server-side query (anchor lookup plus `LIMIT 1` successor and wrap). An invalid GUID returns 400.

## Changes

| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Reports/IReportRepository.cs` | Add the successor query contract. |
| Core | `src/RnRomManager.Core/Users/Hotkeys.cs` | New action; implicit-unassigned rule in `Merge`/`Apply`. |
| Data | `src/RnRomManager.Data/Repositories/ReportRepository.cs` | Successor query sharing the missing-image predicate and order. |
| Api | `src/RnRomManager.Api/Endpoints/ReportEndpoints.cs` | `GET /api/reports/missing-images/next`. |
| Api | `src/RnRomManager.Api/Endpoints/AuthEndpoints.cs` | `implicitUnassigned` on hotkey GET/PUT (additive contract change). |
| Web | `src/RnRomManager.Web/src/routes/+page.svelte`, `library-page.test.ts` | Move Reset. |
| Web | `src/RnRomManager.Web/src/lib/hotkeys.ts`, `src/lib/stores/hotkeys.svelte.ts`, `src/lib/components/HotkeysModal.svelte`, `src/lib/api/types.ts` | New action, editor fallback handling and dispatch. |
| Docs | `docs/features.md` | Reset placement and the new shortcut. |

No migration: hotkey overrides stay in `users.hotkeys`; existing values are read unchanged.

## Risks

- A user's existing override on `Alt+Shift+M` must not start failing saves. The implicit-unassigned rule handles it; tests cover GET, unrelated PUT and Reset all.
- The report sorts by `display_title` under the column's collation; the successor query must use the same collation in its tuple comparison, or rows can be skipped or repeated.

## Steps

Every step adding a route also updates `tests/RnRomManager.Api.Tests/Integration/EndpointRoleMatrixTests.cs` for the signed-in User role.

- [x] **1. Query missing-image successor.** Files: `Core/Reports/IReportRepository.cs`, `Data/Repositories/ReportRepository.cs`, `Api/Endpoints/ReportEndpoints.cs`. Tests: `ReportEndpointsTests` (API Testcontainers) for order across systems, title ties broken by id, fixed and deleted anchor, empty, sole current, wraparound, invalid GUID and auth. Done when: B4's endpoint returns the successor using the same predicate and order as the report.
- [x] **2. Hotkey action and implicit fallback on the server.** Files: `Core/Users/Hotkeys.cs`, `Api/Endpoints/AuthEndpoints.cs`. Tests: `HotkeysTests` (NUnit) for the fallback in `Merge` and `Apply`, explicit conflict error, role-scoped stored overrides; `HotkeyEndpointsTests` (API Testcontainers) for `implicitUnassigned` on GET and PUT. Done when: a user with a stored `Alt+Shift+M` override sees the new action unassigned and can still save other changes.
- [ ] **3. Editor support for the new action.** Files: `Web/src/lib/hotkeys.ts`, `Web/src/lib/api/types.ts`, `Web/src/lib/components/HotkeysModal.svelte`. Tests: `hotkeys.test.ts` catalog/default parity with `Hotkeys.cs`; `HotkeysModal.test.ts` unrelated save omits the implicit action, deliberate disable saves null, Reset all. Done when: B2's editor rules hold.
- [ ] **4. Dispatch Next missing image.** Files: `Web/src/lib/stores/hotkeys.svelte.ts`. Tests: `hotkeys.svelte.test.ts` from the report, from a game page, wraparound, sole-current and empty messages, error toast, coalesced presses and stale responses. Done when: B3 works from any page.
- [ ] **5. Move Library Reset.** Files: `Web/src/routes/+page.svelte`, `library-page.test.ts`. Tests: `library-page.test.ts` DOM and filter state; Playwright mobile/desktop placement (run in Finish). Done when: B1 holds at 320 pixels.
- [ ] **6. Document.** Files: `docs/features.md`. Done when: Reset placement and the new shortcut are documented and the full repository and Playwright suites pass.

## Decisions

- 2026-11-15 Which shortcut? → `Alt+Shift+M`, wrapping through the live report (default).
- 2026-11-15 Who gets the shortcut? → every signed-in user, as Reports is; image editing stays `LibraryEditor` (reviewer).
- 2026-11-15 Split from the combined curation/bundles spec to keep each branch reviewable (reviewer).
- 2026-11-15 When the current game is the only one left, B4 returns `id: null` with `currentIsOnly: true` (the client stays put) (implementation).
- 2026-11-15 The implicit-unassigned fallback applies only to the new action; older actions keep the existing K1 duplicate behaviour, so no current user's bindings change (implementation).

## Kickoff prompt

```text
/spec implement 2026-11-15-library-reset-missing-images.md
```
