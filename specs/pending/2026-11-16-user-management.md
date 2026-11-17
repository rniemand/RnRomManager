---
title: User Management
status: ready # draft | ready | in-progress | completed
created: 2026-11-16
---

# User Management

## Summary
Accounts become one of two types: **Admin** (full access) and **Site user** (read-only library, plus their own collections, bundle queues, hotkeys, password and **private saves**). The `rom-manager` role is folded into `admin`, so ROM/library management is admin-only. Admins already create users with an initial password (forced change on first login), enable/disable them and reset passwords on `/admin/users`; this spec adds a self-change guard, a "new password must differ" rule, per-user save ownership and counts, and writes every user and account operation to the existing audit log so admins can see what users did.

## Out of scope
- Deleting users (users stay disable-only, as today), renaming users, self-service sign-up or password recovery by email.
- Roles other than Admin / Site user, per-system or per-game permissions.
- Sharing saves between users, or transferring a save to another owner.
- Auditing reads (browsing, downloads, play) or per-user writes that aren't account operations (collections, bundle queues, hotkeys).
- A separate audit retention for user events: they follow the existing 30-day purge (AU5).
- Changing the users API contract shape: it keeps the `roles` array (`["admin"]` or `[]`).

## Behaviour

### Account types
- **U1** `Roles.All` becomes `[admin]`. An **Admin** has `roles: ["admin"]`; a **Site user** has `roles: []`. `Roles.Parse` rejects `rom-manager` (and anything else) with the existing `roles` validation error, now reading "Roles must be any of: admin.".
- **U2** Migration `0024_fold_rom_manager.sql`: deletes every `user_roles` row with `role = 'rom-manager'` and rotates `security_stamp` for each user that had one (those sessions end; they sign in again with the new rights). Users with `rom-manager` but not `admin` become Site users; users with both keep `admin`. No other data changes.
- **U3** The `LibraryEditor` authorization policy is removed; every endpoint that used it requires `AdminOnly`. Also `AdminOnly` now: release note save/delete (`PUT|DELETE /api/games/{id}/roms/{romId}/note`), rating/favourite (`PATCH /api/games/{id}/rating`) and publisher logo replace/remove (`PUT|DELETE` publisher logo route). A Site user gets `403 Forbidden` (existing ProblemDetails) on them.
- **U4** Site users keep: every read (library, game detail, discovery, collections they own, ROM/save downloads of what they can see, play), their own collections (`/api/collections*`), bundle queues and bundle downloads, hotkeys (`/api/auth/me/hotkeys`), logout, change password, and their own saves (S1–S5). Nothing else changes for them.

### User administration (existing, with changes)
- **U5** Create (`POST /api/users`) is unchanged: username, `roles` (account type) and initial password are required by the existing policies; the new user has `mustChangePassword: true` and can call only the existing `MustChangeAllowed` routes until they change it (existing `403 PasswordChangeRequired`).
- **U6** `PATCH /api/users/{id}` on the **caller's own id** whose result would have `isDisabled: true` or lack `admin` returns `409 SelfChange` "You can't disable or demote your own account" (the caller is signed in, so they are enabled and admin; any such value is a change). A patch on self that changes nothing still returns 200. `POST /api/users/{id}/reset-password` on self stays allowed (204; forces a change at next login). The check runs after validation and `NotFound`, before `LastAdmin`. The SPA's disabled controls on the own row (Step 7) are a convenience; the server rule governs.
- **U7** `POST /api/auth/change-password` rejects a new password equal to the current one with a `newPassword` validation error "New password must be different from the current password." (checked after the current-password check, before `PasswordPolicy`). This covers the first-login change.
- **U8** Disabling a user (existing) rotates their stamp so their sessions end on the next request; a disabled user's sign-in fails as today (`401 InvalidCredentials`, no hint that the account is disabled).

### Audit of user operations
- **U9** New `AuditActions` (added to `All`, so the audit page's Action filter lists them): `UserCreated`, `UserTypeChanged`, `UserDisabled`, `UserEnabled`, `PasswordReset`, `PasswordChanged`, `SignedIn`, `SignInFailed`, `LockedOut`. Each is recorded through `IAuditLog.Record` only after the operation succeeded (for `SignInFailed`/`LockedOut`: after the counters were written). System, game, job and path columns stay null; the target and facts go in `details`, as `key=value` pairs separated by single spaces:

  | Action | Actor (`user_id`, `username`) | `details` |
  |---|---|---|
  | `UserCreated` | the admin | `user=<name> type=admin\|site-user` |
  | `UserTypeChanged` | the admin | `user=<name> from=<type> to=<type>` |
  | `UserDisabled` / `UserEnabled` | the admin | `user=<name>` |
  | `PasswordReset` (admin route) | the admin | `user=<name>` |
  | `PasswordReset` (CLI `--reset-password`) | none (null, null) | `user=<name> via=cli`, plus ` promoted=admin` when the recovery promoted them (no other enabled admin). The CLI's re-enable and promotion are recorded only through this row (no `UserEnabled`/`UserTypeChanged`). The CLI host never starts `AuditWriter`, so this row is written synchronously through `IAuditRepository.InsertAsync` (stamped with the current UTC time), not `IAuditLog.Record`. |
  | `PasswordChanged` | the user | (none) |
  | `SignedIn` | the user | (none) |
  | `SignInFailed` | the user when the username exists; else `user_id` null and `username` = the attempted name (truncated to 64 by the repository) | `reason=wrong-password\|unknown-user\|disabled\|locked-out` |
  | `LockedOut` | the user | `until=<ISO-8601 UTC>` |

  A PATCH that changes both type and disabled writes one event per change. A no-op PATCH writes nothing. The wrong-password attempt that triggers a lockout writes `SignInFailed` then `LockedOut`.
- **U10** The admin audit page needs no new UI: the existing Action, User and text filters cover the new rows (User filters on the actor; `q` matches `details`, so `user=bob` finds actions on bob). The audit endpoint stays admin-only. `SignedIn` rows may dominate the unfiltered list; the Action filter is the answer (no default filter). Volume is bounded by the login rate limit (10/min/IP) and the 30-day purge; the repository already truncates `username` to 64 and `details` to 1024 characters.

### Private saves
- **S1** A save's **owner** is its `uploadedBy` username (saves.json; matched case-insensitively, usernames being unique and immutable). Saves with `uploadedBy` null have no owner. No saves.json format change; existing saves keep their uploader as owner.
- **S2** `GET /api/games/{id}/saves` returns, for a Site user, only saves they own; for an Admin, all saves (unchanged shape).
- **S3** For a Site user, a save they don't own acts as missing: download, PATCH, file replace and DELETE return `404 NotFound` (the existing ProblemDetails). The viewer (`SaveViewer(string? Username, bool IsAdmin)`, new record in Core) is passed to `PrecheckSaveAsync` and to the update/replace/delete methods; ownership is checked at the point where the save id is resolved today, both in the precheck and again under the lock, so `404` wins over `JobRunning`, `SavesUnreadable` and every other save-level check for a non-owned save. Download checks it in the endpoint. Admins can act on every save; an admin edit keeps `uploadedBy` (owner) and sets `updatedBy` as today.
- **S4** Uploads (`POST /api/games/{id}/saves`, including the Play dialog's copy-to-server buttons) are allowed for every signed-in user and owned by the uploader, as today. `MaxPerRelease` (25) now counts only the uploader's own saves on that release (owner match per S1), for admins too; so `SaveLimitReached` says nothing about other users' saves.
- **S5** The Play dialog's "Play with save" list comes from the same `GET` as the Saves tab, so it shows only S2's saves without SPA changes; covered by `SaveEndpointsTests` (no SPA test).

### Per-user save counts
- **S6** New table `game_user_save_counts (game_id CHAR(36), username VARCHAR(64), save_count INT)`, primary key `(game_id, username)`, `utf8mb4_unicode_ci` (case-insensitive, like `users.username`), FK to `games` with `ON DELETE CASCADE`. The library mirror (`LibraryRepositories` `SaveCount`, :636) keeps computing `games.save_count` by counting `saves/` entries as today, and additionally parses `saves/saves.json` (`GameSaves.Read`) in the same ZIP open, grouping by owner case-insensitively; it writes one row per owner with ≥ 1 save in the same transaction as the game row (the mirror deletes and re-inserts the game, so the cascade clears old rows). When `saves.json` is absent or unreadable it writes no rows for that game. Unowned saves count only in `games.save_count`. Migration `0025_game_user_save_counts.sql` creates it empty; rows appear the next time a game's ZIP is written or re-synced, exactly like `0023` (Risks).
- **S7** Every API `saveCount` is viewer-dependent: for an Admin, `games.save_count` (unchanged); for a Site user, their own row's `save_count` or 0. This covers every `GameListItem` the API returns (library list, discovery game lists, and any collection game list built from `ToListItemDto`) and game detail. Implemented in the endpoints: for a Site user they call new `IGameRepository.GetUserSaveCountsAsync(string username, IReadOnlyCollection<Guid> gameIds, CancellationToken)` (→ `IReadOnlyDictionary<Guid, int>`) for the page's ids and override `saveCount`; repository list queries don't change. The SPA shows it unchanged (library badge, `Saves (n)` tab).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Data | `src/RnRomManager.Data/Migrations/0024_fold_rom_manager.sql` | U2: delete `rom-manager` rows, rotate their users' stamps. |
| Data | `src/RnRomManager.Data/Migrations/0025_game_user_save_counts.sql` | S6: new table, empty. |
| Data | `src/RnRomManager.Data/Repositories/LibraryRepositories.cs` | S6: mirror also writes per-owner counts from `saves/saves.json`; S7: `GetUserSaveCountsAsync`. |
| Core | `src/RnRomManager.Core/Library/ILibraryRepositories.cs` | S7: `IGameRepository.GetUserSaveCountsAsync`. |
| Api | `Endpoints/LibraryEndpoints.cs`, `DiscoveryEndpoints.cs` (and any other `ToListItemDto` caller) | S7: override `saveCount` for Site users. |
| Core | `src/RnRomManager.Core/Users/Roles.cs`, `AdminSeeder.cs` | U1: only `admin`. |
| Core | `src/RnRomManager.Core/Users/UserAdminService.cs`, `UserAdminResult.cs` | U6 `SelfChange`: `CreateAsync`, `UpdateAsync`, `ResetPasswordAsync` take an `AuditActor actor` (its `UserId` is the acting user) from Step 2 on; U9 audit events (service gets `IAuditLog` in Step 3). |
| Core | `src/RnRomManager.Core/Users/AccountService.cs` | U7 differ rule; U9 sign-in/lockout/password-changed events. `LoginAsync` keeps its return value; it records the audit itself. |
| Core | `src/RnRomManager.Core/Audit/AuditActions.cs` | U9 new actions. |
| Core | `src/RnRomManager.Core/Library/LibraryEditor.Saves.cs`, `GameSaves.cs` | S3/S4: `SaveViewer` record, `GameSaves.IsOwner(save, username)`, ownership in precheck and under the lock, per-owner `MaxPerRelease`. |
| Api | `src/RnRomManager.Api/Auth/AuthSetup.cs` | U3: remove `LibraryEditor` policy. |
| Api | `Endpoints/LibraryEndpoints.cs`, `ReportEndpoints.cs`, `PublisherLogoEndpoints.cs` | U3: `AdminOnly`. |
| Api | `Endpoints/UserEndpoints.cs`, `AuthEndpoints.cs`, `Cli/ResetPasswordCommand.cs` | U6 `SelfChange` mapping; pass actors for U9. |
| Api | `Endpoints/SaveEndpoints.cs` | S2/S3 filtering and 404s. |
| Web | `src/lib/stores/auth.svelte.ts`, `src/lib/api/types.ts` | Drop `canEditLibrary`/`rom-manager`; every library-edit gate uses `isAdmin`. |
| Web | `GameImages.svelte`, `routes/games/[id]/+page.svelte`, `routes/reports/duplicates/+page.svelte`, `PublisherLogoModal.svelte`, release note / rating controls | U3 gates: hide note edit, rating/favourite controls and logo edit for Site users (ratings still displayed read-only). |
| Web | `routes/admin/users/+page.svelte` | Role checkboxes → **Account type** select (Admin / Site user) on create and edit; list column "Type"; shows `SelfChange` / `LastAdmin` errors through the existing error path. |
| Web | `routes/change-password/+page.svelte` | Shows U7's field error (existing validation display). |
| Docs | `docs/features.md`, `docs/deployment.md` | Account types, permissions, private saves, audit actions; upgrade note for U2 and S6. |
| Tests | `tests/e2e/specs/mvp.spec.ts` | Replace `rom-manager` usage; add the site-user flow (Step 8). |

API contract: `roles` accepts only `admin`; new 409 code `SelfChange`; `saveCount` and the saves list become viewer-dependent for Site users. No config keys change.

## Risks
- **Rights removed on upgrade (U2):** `rom-manager`-only users lose library edits and are signed out once. Documented in `docs/deployment.md` upgrade notes.
- **Per-user counts start empty (S6):** after upgrade, Site users see save count 0 on games until each game's ZIP is next written or re-synced (same as `0023`). Admin counts are unaffected. Documented in the upgrade note.
- **Save ownership by username (S1):** safe because usernames are unique (case-insensitive collation) and can't be renamed or deleted; if renames are ever added, ownership must move to user ids.
- **Audit noise / attacker-controlled text (U9):** failed sign-ins for unknown usernames write the attempted name; login is rate-limited (10/min/IP), text is truncated to column length and rendered as text (never HTML) by the existing audit page. The audit queue is best-effort (AU2): a dropped event is logged as a Warning, never fails the request.
- **Rights gained on upgrade (U3):** admins created with only `admin` couldn't edit the library before; now every admin can. Intended (two account types); in the upgrade note.
- **Privacy leak (S3):** others' saves return 404, not 403, so a Site user can't probe save ids.

## Steps
Each step leaves the app consistent: Step 1 already switches the SPA's library-edit gate to `isAdmin` and drops `rom-manager` from the users page, so nothing is broken between steps.

- [ ] **1. Fold `rom-manager` into `admin`**: migration `0024`; `Roles.All = [admin]`; remove the `LibraryEditor` policy and switch its endpoints to `AdminOnly`; make release note, rating/favourite and publisher logo writes `AdminOnly` (U1–U4). SPA minimum: `canEditLibrary` returns `isAdmin` (removed fully in Step 7), `rom-manager` removed from `types.ts` and from the users page's role checkboxes. Files: `0024_fold_rom_manager.sql`, `Roles.cs`, `AdminSeeder.cs`, `AuthSetup.cs`, `LibraryEndpoints.cs`, `ReportEndpoints.cs`, `PublisherLogoEndpoints.cs`, `auth.svelte.ts`, `types.ts`, `routes/admin/users/+page.svelte`, and the tests below. Tests: `EndpointRoleMatrixTests` (drop the `RomManager` column; note/rating/logo routes now `Admin`), `SchemaMigrationTests` and `UserRepositoryTests` (migration: rom-manager-only user → no roles and a new stamp; admin+rom-manager → `admin` only, new stamp; admin-only user unchanged), `UserAdminServiceTests`, `UserEndpointsTests` (`rom-manager` → 400), `AdminSeederTests`, `AuthTests`, `ImageEndpointsTests`, `ImageUrlEndpointsTests`, `ResetPasswordCommandTests`, `ExternalLibraryTests`; vitest `stores.svelte.test.ts`, `admin-pages.test.ts` and every `*.test.ts` that builds a `rom-manager` user. Done when: `git grep -nE "Roles\.RomManager|rom-manager" -- src ':!*.test.ts'` hits only migrations `0011`/`0024`; `git grep -n "Roles\.RomManager" -- tests` has no hits (the literal `rom-manager` stays only in the migration tests, the `UserEndpointsTests` rejection test and the e2e spec, fixed in Step 9); listed tests, `npm run lint` and `npm run check` pass.
- [ ] **2. Self-change guard and password-differs rule**: U6 (`UserAdminResult.Failure.SelfChange`; `CreateAsync`, `UpdateAsync`, `ResetPasswordAsync` take `AuditActor actor`, built by the endpoints with `AuditActor.From(ctx.User)`; 409 mapping) and U7. Files: `UserAdminService.cs`, `UserAdminResult.cs`, `UserEndpoints.cs`, `AccountService.cs`. Tests: `UserAdminServiceTests` (self-disable and self-demote → `SelfChange`; self no-op → OK; same change on another admin → OK; `NotFound` before `SelfChange`, `SelfChange` before `LastAdmin`), `AccountServiceTests` (same password → `newPassword` error; different → OK), `UserEndpointsTests` (409 `SelfChange`; self reset-password → 204), `AuthTests` (change-password with the same password → 400). Done when: listed tests pass.
- [ ] **3. Audit user operations**: U9 actions in `AuditActions`; `UserAdminService` (create, type change, enable/disable, admin reset) and `AccountService` (sign-in success, each failure reason, lockout, password change) record through `IAuditLog`; `RecoverAsync` returns `(User User, string Password, bool Promoted)?` and `ResetPasswordCommand` writes the CLI row from it through `IAuditRepository.InsertAsync` (U9). Files: `AuditActions.cs`, `UserAdminService.cs` (incl. `RecoverAsync`), `AccountService.cs`, `AuthEndpoints.cs`, `ResetPasswordCommand.cs`. Tests: `UserAdminServiceTests` and `AccountServiceTests` (each event's action, actor and details with a substituted `IAuditLog`; no-op PATCH records nothing; type+disable PATCH records two; failed operations record nothing beyond U9's sign-in rows; lockout attempt records `SignInFailed` then `LockedOut`), `AuditEndpointsTests` (actions list includes the new ones; an integration test signs in, fails a sign-in with a 200-character unknown username, flushes the `AuditWriter` and reads both rows from `GET /api/admin/audit`, the name cut to 64), `ResetPasswordCommandTests` (a real `audit_events` row with `via=cli`, and `promoted=admin` when promoted). Done when: listed tests pass (Docker).
- [ ] **4. Private saves**: S1–S5. `SaveViewer`; `GameSaves.IsOwner(save, username)`; `PrecheckSaveAsync` and update/replace/delete take the viewer and return `NotFound` for a non-owned save before other save checks, and again under the lock; add counts the uploader's own saves for `MaxPerRelease`; `SaveEndpoints` list filters and download 404s for Site users. Files: `GameSaves.cs`, `LibraryEditor.Saves.cs`, `SaveEndpoints.cs`. Tests: `GameSavesTests` (`IsOwner`: case-insensitive, null owner), `LibraryEditorSavesTests` (site user on own vs others' vs unowned save; 404 wins over `JobRunning`/`SavesUnreadable`; admin edit on others' keeps `uploadedBy`; limit counts only own saves), `SaveEndpointsTests` (list per viewer; download/PATCH/PUT/DELETE 404 for others; admin sees and edits all). Done when: listed tests pass.
- [ ] **5. Per-user save counts in the mirror**: migration `0025`; the mirror writes `game_user_save_counts` per S6. Files: `0025_game_user_save_counts.sql`, `LibraryRepositories.cs`. Tests: `LibraryRepositoryTests` (Testcontainers: rows per owner after a mirror write, case-insensitive grouping; unowned not counted; unreadable `saves.json` → no rows, `save_count` unchanged; re-sync replaces rows; game delete cascades), `SchemaMigrationTests`. Done when: listed tests pass (Docker).
- [ ] **6. Viewer-dependent `saveCount`**: S7. `IGameRepository.GetUserSaveCountsAsync`; library list, game detail and discovery game lists (and any other `ToListItemDto` caller) override `saveCount` for Site users. Files: `ILibraryRepositories.cs`, `LibraryRepositories.cs`, `LibraryEndpoints.cs`, `DiscoveryEndpoints.cs`. Tests: `LibraryRepositoryTests` (`GetUserSaveCountsAsync`: missing → absent), `LibraryEndpointsTests` and `DiscoveryEndpointsTests` (site user sees own count, another site user 0, admin the total). Done when: listed tests pass (Docker).
- [ ] **7. SPA permissions**: remove `canEditLibrary` (callers use `isAdmin`); hide release-note edit, rating/favourite controls (rating shown read-only) and publisher-logo edit for Site users; change-password page shows U7's error. Files: `auth.svelte.ts`, `GameImages.svelte`, `games/[id]/+page.svelte`, `reports/duplicates/+page.svelte`, `PublisherLogoModal.svelte`, `RatingControl.svelte` (read-only mode), the release note control, `change-password/+page.svelte`. Tests: `stores.svelte.test.ts`, `game-admin.test.ts`, `game-images.test.ts`, `release-note.test.ts`, `RatingControl.test.ts`, `PublisherLogoModal.test.ts`, `reports-pages.test.ts`, `Navbar.test.ts`, `auth-pages.test.ts`. Done when: `npm run lint`, `npm run check`, listed vitest files pass; no `canEditLibrary` in `src/RnRomManager.Web/src`.
- [ ] **8. Users page account type**: create and edit use an **Account type** select (Admin / Site user) mapped to `roles`; list shows a Type column; disable/enable and reset unchanged; `SelfChange` and `LastAdmin` shown through the existing error path; the signed-in admin's own row disables the type select and the disable button (tooltip: "You can't change your own account type or disable yourself"). Files: `routes/admin/users/+page.svelte`. Tests: `admin-pages.test.ts`. Done when: lint, check and `admin-pages.test.ts` pass.
- [ ] **9. Docs and e2e**: `docs/features.md` (account types replace the roles section; permission list; private saves, per-user limit and counts; user audit actions and their details), `docs/deployment.md` (upgrade note: U2 sign-out, rom-manager-only users lose edits, admin-only users gain them, S6 counts fill on next write/re-sync); e2e `mvp.spec.ts`: replace `rom-manager` usage; add a flow where an admin creates a Site user, the user signs in, must change the password (same password rejected), sees no edit controls, uploads a save the admin can see and a second site user can't, a wrong-password sign-in fails, the admin disables the user and their next request is signed out, and the audit page lists `UserCreated`, `SignedIn`, `SignInFailed`, `PasswordChanged`, `UserDisabled`. Files: `docs/features.md`, `docs/deployment.md`, `tests/e2e/specs/mvp.spec.ts`. Tests: the e2e flow runs in Finish. Done when: every U/S rule is in a doc; no other doc mentions `rom-manager` or the old roles except past upgrade notes; `npx playwright test --list` lists the new flow.

## Decisions
- 2026-11-16 What happens to `rom-manager`? → Folded into `admin`; rom-manager-only users become Site users (user)
- 2026-11-16 Which writes do Site users keep? → Personal only: own collections, bundle queues, hotkeys, password, saves; notes, rating/favourite and publisher logos become admin-only (user)
- 2026-11-16 How are saves managed per user? → Private per user; admins see and manage all; existing saves belong to `uploadedBy` (user)
- 2026-11-16 Which user operations are audited? → Admin user actions plus account events (sign-in, failed sign-in, lockout, password change, CLI reset) (user)
- 2026-11-16 Save count for Site users? → Per-user counts in a new table (user)
- 2026-11-16 Can an admin disable or demote themselves? → No, 409 `SelfChange` (user)
- 2026-11-16 Keep the `roles` array in the API instead of a new `accountType` field → yes, smallest contract change (code)
- 2026-11-16 Save limit with private saves → `MaxPerRelease` counts the uploader's own saves per release (user)
- 2026-11-16 CLI audit row → written synchronously via `IAuditRepository`, since the CLI never starts `AuditWriter` (reviewer)
- 2026-11-16 Viewer `saveCount` → endpoints override from `GetUserSaveCountsAsync`, list SQL unchanged; covers discovery too (reviewer)
- 2026-11-16 Save owner identity → `uploadedBy` username, no saves.json format change (code: usernames are unique and immutable)

## Kickoff prompt
```text
/spec implement 2026-11-16-user-management.md
```
Starts the spec, or resumes it at the first unticked step.
