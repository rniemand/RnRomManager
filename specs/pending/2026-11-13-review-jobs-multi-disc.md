---
title: Review resolve jobs and multi-disc games
status: ready
created: 2026-11-13
---

# Review resolve jobs and multi-disc games

## Summary
Resolving a Review item runs today inside one HTTP request: a multi-GB Xbox 360 or PlayStation image is re-hashed and moved while the page shows only a disabled button. If the Admin leaves the page, they are sent to the game page when the request finishes. This spec runs every resolution as a background job with the existing step progress bar, and never navigates away from a page the Admin has left. It also lets an Admin add a disc to an existing game from Review: a **Disc** number on Attach and Manual, and resolving a whole unmatched bin/cue set, which can only be deleted today. That covers games such as Final Fantasy VII (PlayStation), where disc 1 matched the Redump DAT and disc 2 is stuck in Review.

## Out of scope
- Dismiss and Delete of review items stay synchronous, as today. Dismissing a bin/cue set member is still refused (`SetFileNotResolvable`, B15 of the disc-systems spec). Delete works on every item.
- Disc numbers on images (cover art per disc).
- Setting a disc number on a release already in the library. Releases matched against the Redump DAT already have a `Disc N` flag from the DAT name.
- Disc numbers on cartridge systems: `disc` is accepted only for `FileSets` systems (B9).
- Renaming files. Set members keep their source names (B10 of the disc-systems spec), so a cue sheet keeps working.
- bin/cue sets inside archives. Sets are planned from loose files only, as ingest does (B7).
- `AmbiguousSet` items (two or more members match the DAT) stay unresolvable. So does a set with a member that ingest skipped as `AlreadyInReview`, because its SHA1 is already another source's Pending row (`ingest_review_items.sha1` is unique). Such a set has no row for that member (B12), and stays delete-only. This limit goes in the docs.
- Re-attaching a reloaded page, or a second tab, to a running resolve job (B7).
- `.m3u` playlists, compressed images (`.chd` etc.).
- An info.json schema change: the disc number is a flag.

## Behaviour

### Resolution as a job
- **B1 Start.** `POST /api/review/{id}/resolve` (Admin), in order:
  1. Validate the request body as today (`ReviewEndpoints.Validate`, plus B9's `disc`) → `400 ValidationFailed`.
  2. Item checks, without the lock: no item, or status isn't `Pending` → `404`. `ReviewResolver.IsResolvableAsync` false → `409 SetFileNotResolvable`.
  3. Start a `ReviewResolveJob` through `JobRunner.StartAsync` (trigger `Manual`, requested by the signed-in user). Another running job → `409 JobRunning`, as for every job start. The job is registered transient in `src/RnRomManager.Api/AppHost.cs`. The endpoint sets `ReviewId`, `FileName` (the item's `originalFileName`, as read in step 2) and `Request` on it before `StartAsync`, as `IngestEndpoints` sets `IngestJob.Selection`, and passes the signed-in user's id as `requestedBy`. The job acts as `JobContext.Actor`, which `JobRunner` builds from `requestedBy` (user id, username, job id).
  4. Return `202 Accepted` with `{ "jobId": "<guid>" }`. The endpoint no longer returns `200 { gameId }`.

  `IJob.Preflight` is not used. The job repeats step 2 with the lock held (B2), which covers a change between the request and the start.
- **B2 Run.** `ReviewResolveJob.RunAsync` calls `ReviewResolver.ResolveInJobAsync(id, request, context.Actor, context.Progress)`. That is today's resolution, with these differences:
  - it never acquires the job lock: the runner holds it;
  - it uses `CancellationToken.None` on every path, including the embedded path, which passes the request's `ct` today;
  - it re-checks Pending and resolvable first; a failed check ends the job with summary code `NotFound` or `SetFileNotResolvable`.

  `ResolveAsync` is removed. Dismiss and Delete keep acquiring the lock themselves.
- **B3 Progress.**
  - For a single file: `SetTotal(1)`, then `SetCurrentItem(<originalFileName>)`. The streamed steps of the external hash and move follow (`Hashing`, `CopyingToLibrary`, as ingest shows them), because `StoreReleaseAsync` passes `progress` to the methods that already take it since the large-ROM spec. Then `ItemDone()`. An embedded ROM shows no step bar.
  - For a set: `SetTotal(N)`, with `SetCurrentItem`/`ItemDone` per member (B14).
- **B4 Not cancellable.** `IJob` gains `bool Cancellable => true`, and `ReviewResolveJob` returns false.
  - `JobRunner.Cancel` returns `CancelResult { NotFound, Cancelled, NotCancellable }` instead of `bool`. For a non-cancellable job it returns `NotCancellable` and does nothing.
  - `POST /api/jobs/{id}/cancel` maps `NotCancellable` to `409` ProblemDetails, code `NotCancellable`, title "This job can't be cancelled.". `NotFound` and `Cancelled` behave as false and true do today.
  - `JobRunner.StopAsync` (shutdown) doesn't cancel a non-cancellable job's token. `RunAsync` never marks such a job `Cancelled`. If the host's shutdown timeout ends the process first, the row is `Aborted` at the next start, as for any job (§7.4); the item is still Pending unless the resolution's last write had happened.
  - The SPA shows no Cancel button for a `ReviewResolve` job: not on the job page, and not on the running-job cards of the Ingestion and Integrity pages, which today show one for whatever job runs. One helper, `isCancellable(type)` in `src/RnRomManager.Web/src/lib/jobs.ts`, decides for all three.
- **B5 Result.** The job ends `Completed`, or `Failed` on an unexpected exception, as any job does. Its `summary` is `{ "reviewId": <id>, "fileName": "<originalFileName>", "gameId": "<guid>" | null, "code": "<code>" | null, "errors": { field: [messages] } | null }`.
  - On success, `code` is null and `gameId` is set.
  - On failure, `code` is the code the resolution returns today (`ValidationFailed` with `errors`, `NotFound` (also an Attach target game that no longer exists, as today), `SourceMissing`, `AmbiguousGroup`, `TargetZipUnreadable`, `InfoJsonNewerSchema`, `Duplicate`, `NameTaken`, `NameTooLong`, `VerifyFailed`, `IoError`, `DbWriteFailed`), or B2's re-check codes (`NotFound`, `SetFileNotResolvable`).
  - `fileName` is the job's `FileName` (B1), so it is set even when the item is gone.
  - The item stays Pending, except for `SourceMissing`, which deletes the row as today.
  - `job_items`: one row per file, filled as `IngestJob.RecordAsync` fills an item: a new `FileId` (`Guid.NewGuid()`, as `IngestJob` does per file), the item's source path (`archivePath ?? sourcePath`), archive entry and system. Outcome `Ingested` with `gameId` on success. Otherwise outcome `Error`, with the code as its reason. A set writes one row per member, all with the set's outcome; a set that B14 step 1 doesn't find writes one row, for the item. B2's `NotFound` for an item that no longer exists writes no row.
- **B6 Job lists.** `ReviewResolve` is a new `JobType` (13 characters; `jobs.type` is `VARCHAR(16)`, so no migration). `GET /api/jobs?type=ReviewResolve` works through the existing enum parse. For this type, the job page (`/jobs/[id]`) shows "Review: <fileName>" and the result, instead of the generic count summary:
  - success: a link to the game;
  - failure: the code and the messages, plus a link to `/review/<reviewId>`, except for `SourceMissing`, which shows "The source file is gone; the review item was removed." with no link.
  - a job with no summary (`Failed` on an unexpected exception, or `Aborted`): the page's generic view, with the job's `error`, as for any job.

### Review item page (SPA)
- **B7 Progress.** After a `202`, the page keeps `{ jobId, reviewId, fileName, done }` in a module-level store, `src/RnRomManager.Web/src/lib/stores/reviewJobs.svelte.ts`, so it survives navigation within the SPA. `done` is today's per-action success text (`Added as <title>.` etc.). The store polls `GET /api/jobs/{jobId}` every 1.5 s, the first poll 1.5 s after the `202`, until the job isn't `Running`. A failed poll is retried at the next tick. While the job runs, the item page:
  - disables every action;
  - shows "Resolving <fileName>…" with a spinner;
  - shows the `JobStepProgress` bar from `/api/jobs/current` (the `jobs` store).

  Limit: after a full reload, or in another tab, the page doesn't know about the job. Its actions stay enabled, and Resolve, Dismiss and Delete each get `409 JobRunning`, shown as the standard `toasts.jobRunning` toast, as today.
- **B8 Completion.** The rule is "the current URL is `/review/<reviewId>`".
  - On that URL, success → `goto` the game, with the `done` toast. Failure → the summary's `code` and `errors` go through the page's existing error handling (`handle`) as an `ApiError` with that code would today: field errors on the form, otherwise `ErrorAlert`. The actions are enabled again. After `SourceMissing`, the page shows the item as gone (today's 404 state).
  - Anywhere else: no navigation. Success → a success toast "<fileName> added to the library." with an **Open game** link. Failure → a danger toast "Resolving <fileName> failed: <code>." with an **Open review item** link (no link for `SourceMissing`).
  - A job that ends with no summary (`Failed`, `Aborted`) is a failure with the job's `error` as its message: `ErrorAlert` on that URL, elsewhere a danger toast "Resolving <fileName> failed." with an **Open review item** link.
  - Toasts gain an optional `link: { href, text }`, which `ToastHost` renders.

### Disc numbers in Review
- **B9 Request.** `ReviewResolutionRequest` gains `disc` (int, optional). `Validate` accepts `Disc` as an Attach `flag`; its `flag` error becomes "Must be Hack, Translated, None or Disc.". Every error below is `400` on the `disc` field:
  - not 1–99: "Must be 1–99.";
  - set with an action other than `Manual`, or with `Attach` and a flag other than `Disc`: "A disc number needs Manual or Attach as a disc.";
  - `Attach` with the new flag `Disc` and no `disc`: "A disc number is required.";
  - set on a system that isn't `FileSets`: `ValidationFailed` from the resolver, in the job summary, "Disc numbers are for disc systems.". The system is only known from the item, so the endpoint's `Validate` can't check it.
- **B10 Disc flag.** A disc number becomes the release flag `Disc <n>` (no leading zero), the same flag `NoIntroName.Parse` makes from a Redump name. For Manual, any flag the user typed that matches `^Disc \d+$` (ignoring case) is removed first, so there is never two. A single file's generated name includes the flag, as every flag does today: Manual `Title (Region) (Disc 2).iso`.
- **B11 Attach as a disc.** `Attach` with `flag` `Disc` adds the file as a release of the chosen game: the same system, any game, not limited to ambiguous candidates.
  - **Title:** the target game's title, as Hack/Translated take it today.
  - **Regions, languages and revision:** copied from the target's primary release (`isPrimary`), so disc 2 lines up with disc 1. They are empty when the target has no releases. An unreadable target ZIP is `TargetZipUnreadable` (the existing `OpenTarget`).
  - **Flags:** `Disc <n>`. **Identification:** `Manual`, with no DAT metadata.
  - **Single-file name:** `Sanitize(<original stem>)`, plus ` (Disc <n>)` when the stem doesn't already contain `(Disc <n>)` (ignoring case), plus the original extension.
  - **Name clash** with one of the game's releases: `NameTaken`. Every `FileSets` system is external (B10 of the disc-systems spec).

### Unmatched bin/cue sets in Review
- **B12 Finding a set.** `ReviewSets.FindAsync(item)` returns the set's members, each with its review row, or null. It returns null on any failure to read the folder or a cue. It finds a set when all of these hold:
  - the item is on a `FileSets` system whose `RomExtensions` include `.cue` (the systems `IngestJob` plans sets for) and is loose (`archivePath` is null);
  - `IngestSetPlanner.Plan`, run over the system's ingest folder as `IngestFileScanner` lists it today (top-level files, ignored names left out; its private `ScanSystem` becomes public), puts the item's file in a complete `Set` unit. Cues up to `MaxCueBytes` are read by `IngestJob`'s `ReadCue`, which moves to a shared `internal static` helper beside `IngestSetPlanner` so both callers use the same checked read;
  - every member of that set has a `Pending` review row with reason `NoDatMatch`, from a new `IIngestReviewRepository.ListPendingByPathsAsync(IReadOnlyCollection<string> sourcePaths)`, which returns the `Pending` loose rows (`archive_path` null) whose `source_path` is one of the members' full paths.

  `ReviewResolver.IsResolvable` becomes `IsResolvableAsync(item)`: true for a non-`.cue`/`.bin` item, as today, and for a `.cue`/`.bin` item on a `FileSets` system exactly when `FindAsync` finds a set. Its callers in `ReviewEndpoints` and the resolver are updated. The cost is one folder listing and a few small cue reads per detail request, which is fine for an Admin-only page.
- **B13 Detail.** `GET /api/review/{id}` adds two fields:
  - `dismissible`: false for every `.cue`/`.bin` item on a `FileSets` system, true otherwise;
  - `setMembers`: null when `FindAsync` finds no set, otherwise `[{ "reviewId", "fileName", "sizeBytes" }]` in set order (cue first).

  The item page shows a **Set** card listing the members ("Resolving this item adds all N files as one release.") and hides Dismiss when `dismissible` is false. Any member's page can resolve the set.
- **B14 Resolve a set.** Resolving any member resolves the whole set, with the same actions as a single file: Dat pick, Attach (Hack, Translated or Disc; `None` doesn't apply) and Manual (with `disc`).
  1. **Find the set again.** Inside the job, `FindAsync` runs with the lock held. If it finds no set, the job fails with `SetFileNotResolvable` (B2's check) and nothing moves.
  2. **Store it.** A new public pipeline entry point, `IngestPipeline.StoreSetReleaseAsync(IReadOnlyList<(IngestFile File, string Sha1)> members, ISystemHandler system, InfoJsonRom rom, DatMetadata metadata, Guid? targetGameId, Func<Guid, Task> afterMirror, AuditActor actor, string reviewAction, JobProgress progress)`, does what `ProcessSetCoreAsync` does after planning. `members` are in set order (cue first), each with its review row's SHA1. `rom` carries what the action decides (step 3); the pipeline fills its `FileName`, `OriginalFileName`, `Version`, `Header`, `Metadata` and `Files` from the hashed members, as `ProcessSetCoreAsync` does. It reuses that method's private parts (`ExternalSource`, `SetPlacement`, `HashCheckedAsync`, `NewGameAsync`/`AddToGameAsync` with `set`), which stay private:
     - classify and hash every member, streamed and once each (`SetCurrentItem(<member>)`, then its `Hashing` step, then `ItemDone()`);
     - each member's SHA1 must be its review row's, else `VerifyFailed`;
     - a member that changed between its check and the moves (the same snapshot re-check as set ingest) is `VerifyFailed` too, not `TooRecent`, and nothing moves;
     - the release's main file is the cue's first `FILE` (track 1, the data track by convention); the cue and every other track go in its `files`, as set ingest stores a matched set;
     - every file keeps its exact source name;
     - the files are placed with set ingest's all-or-nothing rollback; a name already used in the game's folder is `NameTaken`, and nothing moves;
     - grouping is as today: Manual and Dat pick add to the one game that owns the base title, or create one (`AmbiguousGroup` when several own it); Attach uses its target.
  3. **What the action decides:** only the release's title, regions, languages, revision, flags and identification (in `rom`), and `metadata`:
     - **Manual:** from the form, with B10's disc flag;
     - **Attach:** from the target, as B11 describes, or the Hack/Translated flag;
     - **Dat pick:** the chosen system must be in the source's family, and every member's extension and size must fit it (`RomExtensions`, `Min`/`MaxRomSizeBytes`), else `ValidationFailed` on `datSystem`. Tags come from the DAT game name, with flags plus `Unverified`. Identification is `Manual` with the DAT's name, version and game name, as the single-file pick does. Metadata is `store.MetadataFor(crc)` of the DAT game's first rom (Redump has no metadat, so usually empty).
  4. **Mark the rows.** The `afterMirror` hook calls a new `IIngestReviewRepository.SetResolutionAsync(IReadOnlyList<long> ids, …)`, which marks every member's row `Resolved` with the game id in one transaction. If that write fails after the files moved, the result is `DbWriteFailed`. The release is in the library, but the rows stay Pending. Their set can no longer be found, so they are unresolvable (`SetFileNotResolvable`), and the Admin clears them with **Delete**, one member at a time: the source is already gone, so each Delete only removes that member's Pending row (today's `DeleteExternalSourceAsync` rule for a missing source, by source path).
- **B15 Dismiss.** Dismissing a set member stays `409 SetFileNotResolvable`. Dismiss checks its own rule: refused for every `.cue`/`.bin` item on a `FileSets` system (= `!dismissible`).

### Review form and game page (SPA)
- **B16 Attach.** The Attach tab's flag choice gains **Disc** (with a number input, 1–99, required), offered only when the item's system is a disc system (the detail's `fileSets` flag, added to `GET /api/review/{id}`). Choosing it lists every game of the system in the existing `GameTypeahead`, as Hack/Translated do.
- **B17 Manual.** The Manual tab gains an optional **Disc** number input (1–99), only for disc systems. `src/RnRomManager.Web/src/lib/reviewForm.ts` validates it and maps it to `disc`.
- **B18 Game page.** A release with a `Disc <n>` flag shows a `Disc <n>` badge in its card header. When two or more releases (not updates) have one, the list is ordered: releases without a disc first, in today's order, then by disc number, ties in today's order.

### Permissions
Unchanged: every Review endpoint is Admin-only. The job's audit rows carry the requesting user (`JobContext.Actor`), as the synchronous resolve's do today.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Jobs/Job.cs`, `IJob.cs` | `JobType.ReviewResolve`; `IJob.Cancellable`. |
| Core | `src/RnRomManager.Core/Jobs/JobRunner.cs` | `Cancel` returns `CancelResult`; `StopAsync`/`RunAsync` leave non-cancellable jobs alone (B4). |
| Core | `src/RnRomManager.Core/Ingest/ReviewResolveJob.cs` (new) | B2, B3, B5: run, summary, job items. |
| Core | `src/RnRomManager.Core/Ingest/ReviewResolver.cs` | `ResolveAsync` → `ResolveInJobAsync` (no lock, `CancellationToken.None`, progress); `disc` (B9–B11); sets (B14); `IsResolvable` → `IsResolvableAsync` (B12); Dismiss rule (B15). |
| Core | `src/RnRomManager.Core/Ingest/ReviewSets.cs` (new), `IngestSetPlanner.cs`, `IngestJob.cs`, `IngestFileScanner.cs` | B12; `ReadCue` moves to a shared helper; `ScanSystem` public. |
| Core | `src/RnRomManager.Core/Ingest/ReviewResolutionRequest.cs`, `ReviewResolutionResult.cs` | `Disc`. |
| Core | `src/RnRomManager.Core/Ingest/IngestPipeline.cs` | `StoreReleaseAsync(progress)`; new `StoreSetReleaseAsync` (B14). |
| Data | `src/RnRomManager.Core/Ingest/IIngestReviewRepository` + `src/RnRomManager.Data` `IngestReviewRepository` | `ListPendingByPathsAsync(sourcePaths)` (B12); `SetResolutionAsync(ids, …)` in one transaction. |
| Api | `src/RnRomManager.Api/AppHost.cs` | Register `ReviewResolveJob` transient. |
| Api | `src/RnRomManager.Api/Endpoints/ReviewEndpoints.cs` | B1; `disc` validation; `dismissible`, `setMembers`, `fileSets` (B13, B16). |
| Api | `src/RnRomManager.Api/Endpoints/JobEndpoints.cs` | `409 NotCancellable` (B4). |
| Web | `src/RnRomManager.Web/src/lib/stores/reviewJobs.svelte.ts` (new), `src/RnRomManager.Web/src/lib/stores/toasts.svelte.ts`, `src/RnRomManager.Web/src/lib/components/ToastHost.svelte` | B7, B8; toast `link`. |
| Web | `src/RnRomManager.Web/src/routes/review/[id]/+page.svelte`, `src/RnRomManager.Web/src/lib/reviewForm.ts`, `src/RnRomManager.Web/src/lib/api/types.ts` | B7, B8, B13, B16, B17; `JobType` gains `ReviewResolve`. |
| Web | `src/RnRomManager.Web/src/lib/jobs.ts`, `src/RnRomManager.Web/src/routes/jobs/[id]/+page.svelte`, `src/RnRomManager.Web/src/routes/ingestion/+page.svelte`, `src/RnRomManager.Web/src/routes/integrity/+page.svelte` | B4 (`isCancellable`, no Cancel), B6. |
| Web | `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte` | B18. |
| Docs | `docs/features.md`, `docs/development.md` | Review resolves as a job (`features.md` also documents the resolve endpoint's response and the resolve audit row's job id; `development.md`'s "One job at a time" no longer lists review resolutions as synchronous); disc numbers; resolving sets and their limits. |

**API contract changes:**
- `POST /api/review/{id}/resolve` returns `202 { jobId }` instead of `200 { gameId }`. Its failures, apart from B1's, move into the job summary.
- `POST /api/jobs/{id}/cancel` can return `409 NotCancellable`.
- `GET /api/review/{id}` gains `dismissible`, `setMembers` and `fileSets`.

The SPA is the only client. No migrations, no config keys, no info.json change; existing data is untouched.

## Risks
- **Contract change** breaks every test that expects `200 { gameId }`: `ReviewEndpointsTests`, `IngestReviewTests`, `ExternalLibraryTests` (its `Retry`, `Review` and `SetReview` parts post to the endpoint), and the e2e test `review queue: resolve with a suggestion`. Removing `ResolveAsync` breaks its direct callers: `ReviewResolverTests`, `ReviewResolverExternalTests`, `ExternalLibraryTests` (`EditGuards`, `MoveRelease`, `TitleEdit`), `IngestPipelineTests` and `TitleDbIngestTests`. Step 3 moves the direct callers to `ResolveInJobAsync`, step 4 updates the HTTP tests, step 5 updates the SPA, and step 11 updates the e2e. Between steps 4 and 5, resolving from the SPA is broken; each commit still builds and passes its own tests.
- **Lock ownership:** a second `TryAcquire` inside the job would always get `JobRunning`. B2 removes it from the job path, and step 3's test proves it.
- **Partial set moves:** B14 reuses set ingest's placement and rollback rather than a second implementation.
- **Data track guess:** with no DAT match, track 1 is taken as the main file. If a set's first `FILE` isn't the data track, only the release's main-file label is off; all files are kept and listed.
- **Slow small resolves:** an embedded ROM's resolve now waits for one 1.5 s poll. Acceptable for one code path.

## Steps
- [ ] **1. Disc numbers in review requests (Core)**: B9, B10, B11 for single files. Files: `src/RnRomManager.Core/Ingest/ReviewResolutionRequest.cs`, `ReviewResolver.cs`, `src/RnRomManager.Api/Endpoints/ReviewEndpoints.cs` (`Validate` only). Tests:
  - `ReviewRequestValidationTests`: disc out of range; disc with Dat, Hack or None refused; flag Disc without disc refused; Manual with disc accepted.
  - `ReviewResolverExternalTests` (`ExternalSetTestHandler`, a `FileSets` system): Manual with disc 2 next to a `Disc 1` release of the same title is added to that game as `… (Disc 2).iso`, not `NameTaken`; a typed `Disc 3` flag is replaced; Attach Disc copies the target's primary release's regions and keeps the `.iso` extension; a stem already holding `(Disc 2)` isn't doubled; an Attach Disc target with no releases gets empty regions.
  - `ReviewResolverTests`: disc on an embedded system → `ValidationFailed` `disc`.

  Done when: those pass; the endpoint still returns `200` (the job comes in step 4).
- [ ] **2. Non-cancellable jobs (Core + API)**: B4's runner and endpoint parts. `JobEndpoints` changes in the same commit, because `Cancel`'s new return type wouldn't build without it. Files: `src/RnRomManager.Core/Jobs/IJob.cs`, `JobRunner.cs`, `src/RnRomManager.Api/Endpoints/JobEndpoints.cs`. Tests:
  - `JobRunnerTests`: `Cancel` on a non-cancellable test job → `NotCancellable`, and it ends `Completed`; `StopAsync` doesn't cancel it; a cancellable job still gets `Cancelled`, and an unknown id `NotFound`.
  - `JobEndpointsTests`: cancelling a running non-cancellable test job is `409 NotCancellable`; other jobs cancel as before (`202`, `404` when not running).

  Done when: those pass.
- [ ] **3. Review resolve job (Core)**: B2, B3, B5. Files: `src/RnRomManager.Core/Jobs/Job.cs` (`JobType.ReviewResolve`), `src/RnRomManager.Core/Ingest/ReviewResolveJob.cs`, `ReviewResolver.cs`, `IngestPipeline.cs` (`StoreReleaseAsync(progress)`), `src/RnRomManager.Api/AppHost.cs` (registration), plus the existing resolve endpoint, which starts the job, awaits `StartedJob.Completion`, reads the summary through `IJobRepository.GetAsync` and maps its `code` through today's `ToResult` (`409 JobRunning` when `StartAsync` returns null), so the `200` contract holds until step 4. Tests:
  - `ReviewResolveJobTests` (new): run inside a `JobRunner` holding the lock, it doesn't return `JobRunning`; success summary with `gameId`, `fileName` and an `Ingested` job item; `VerifyFailed` keeps the item Pending with an `Error` job item; `SourceMissing` removes the row; an item no longer Pending → `NotFound` summary with the job's `fileName` and no job item; an external resolve reports `Hashing` and `CopyingToLibrary` steps (`ExternalIngestHooks.WrapHashStream`); the job is not cancellable.
  - The direct `ResolveAsync` callers move to `ResolveInJobAsync` and still pass: `ReviewResolverTests`, `ReviewResolverExternalTests`, and in the API test project `ExternalLibraryTests`, `IngestPipelineTests` and `TitleDbIngestTests`. Dismiss and Delete still return `JobRunning` while the lock is held.

  Done when: those pass.
- [ ] **4. Resolve endpoint returns a job (API)**: B1 and B6 (API part). Files: `ReviewEndpoints.cs`. Tests:
  - `ReviewEndpointsTests`: `202 { jobId }`; `404` for a missing or resolved item; `409 SetFileNotResolvable`; `409 JobRunning` while another job runs; `400` for a bad `disc`.
  - `IngestReviewTests` and `ExternalLibraryTests` (`Retry`, `Review`, `SetReview`): every resolve over HTTP waits for the job and reads `gameId` or `code` from `GET /api/jobs/{id}`'s summary; the `SetFileNotResolvable` refusal stays a synchronous `409`.
  - `JobEndpointsTests`: `?type=ReviewResolve` lists it.

  Done when: those classes pass.
- [ ] **5. Review page runs the job (SPA)**: B7, B8, and B4/B6 (SPA part). Files: `src/RnRomManager.Web/src/lib/stores/reviewJobs.svelte.ts`, `toasts.svelte.ts`, `ToastHost.svelte`, `src/RnRomManager.Web/src/lib/jobs.ts`, `src/RnRomManager.Web/src/routes/review/[id]/+page.svelte`, `src/RnRomManager.Web/src/routes/jobs/[id]/+page.svelte`, `src/RnRomManager.Web/src/routes/ingestion/+page.svelte`, `src/RnRomManager.Web/src/routes/integrity/+page.svelte`, `src/RnRomManager.Web/src/lib/api/types.ts`. Tests:
  - `stores.svelte.test.ts`: the store polls until the job is done; on `/review/<id>` it navigates to the game; elsewhere it shows a toast with an Open game link; failure shows a toast with an Open review item link, and none for `SourceMissing`; a `Failed` job with no summary shows the failure toast; toast `link` rendering.
  - `review-page.test.ts`: resolving shows progress and disables actions; field errors from a failed summary show on the form.
  - `job-page.test.ts`: a `ReviewResolve` job shows its file, game link or error, and no Cancel button; one with no summary shows the generic view.
  - `jobs.test.ts`: `isCancellable` is false only for `ReviewResolve`.

  Done when: `npm run lint`, `npm run check` and those vitest files pass.
- [ ] **6. Find review sets (Data + Core + API)**: B12's `ReviewSets.FindAsync`, plus B13, B15 and B16's `fileSets` on the detail. `IsResolvable` is unchanged in this step, so a set member is still unresolvable. Files: `IIngestReviewRepository`, `IngestReviewRepository` (`ListPendingByPathsAsync`), `src/RnRomManager.Core/Ingest/ReviewSets.cs`, `IngestSetPlanner.cs` (shared cue read), `IngestJob.cs`, `IngestFileScanner.cs`, `ReviewResolver.cs` (Dismiss rule), `ReviewEndpoints.cs`. Tests:
  - `IngestReviewRepositoryTests`: `ListPendingByPathsAsync` returns only Pending loose rows of the given paths.
  - `ReviewSetsTests` (new, real temp dirs): a complete set whose members are all Pending `NoDatMatch` is found from the cue and from a bin; null for a missing bin, a Resolved member, an `AmbiguousSet` member, a member with no row (`AlreadyInReview`), an archived item, a lone `.bin` and an unreadable cue.
  - `IngestJobTests` still pass with the shared cue read.
  - `ReviewEndpointsTests`: `dismissible`, `setMembers` and `fileSets` for a set member and for a single file; dismissing a set member is still `409`.

  Done when: those pass.
- [ ] **7. Resolve a set with Manual or Attach (Core)**: B14 steps 1, 2 and 4, Manual and Attach; `IsResolvableAsync` switches to `ReviewSets` (B12). Files: `ReviewResolver.cs`, `IngestPipeline.cs` (`StoreSetReleaseAsync`), `IIngestReviewRepository`, `IngestReviewRepository`, `ReviewEndpoints.cs` (async call). Tests:
  - `ReviewResolverExternalTests`: Manual with disc 2 adds a cue and 2 bins as one release of the game whose disc 1 matched the DAT, with names kept, the cue and track 2 in `files`, and every row Resolved; Attach Disc on a set; a member changed since review → `VerifyFailed`, nothing moved; a set made incomplete → `SetFileNotResolvable`, nothing moved; a failing second move rolls back the first and every row stays Pending; a member name already in the folder → `NameTaken`; the rows' write failing after the move → `DbWriteFailed`; after that, resolving a member is `409 SetFileNotResolvable` and Delete on a member removes only that member's row; a Dat pick on a set → `ValidationFailed` on `action`, "Dat pick on a set isn't supported yet." (step 8 removes this).
  - `IngestReviewRepositoryTests`: `SetResolutionAsync(ids)` marks all of them or none.
  - `ReviewEndpointsTests`: a set member is now `resolvable`; `ExternalLibraryTests.SetReview`'s `SetReview_BinItem_IsDeleteOnly_OverHttp` becomes: a complete unmatched set's bin is resolvable, dismiss is still `409`.

  Done when: those pass.
- [ ] **8. Resolve a set with a Dat pick (Core)**: B14 step 3, Dat pick; removes step 7's interim refusal. Files: `ReviewResolver.cs`. Tests: `ReviewResolverExternalTests`: a Dat pick names the release from the DAT game name with `Unverified`, keeps the file names, and records the DAT's name in identification; a member whose extension or size doesn't fit the chosen system → `ValidationFailed` `datSystem`. Done when: those pass.
- [ ] **9. Disc and set controls on the review page (SPA)**: B13 (SPA), B16, B17. Files: `src/RnRomManager.Web/src/routes/review/[id]/+page.svelte`, `src/RnRomManager.Web/src/lib/reviewForm.ts`, `types.ts`. Tests:
  - `review-page.test.ts`: Attach Disc requires a number and sends `flag: 'Disc', disc`; Manual sends `disc`; the disc inputs are hidden when `fileSets` is false; the Set card lists the members; no Dismiss when `dismissible` is false.
  - `reviewForm.test.ts`: disc validation.

  Done when: lint, check and those files pass.
- [ ] **10. Disc badge and order on the game page (SPA)**: B18. Files: `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte`. Tests: `game-page.test.ts`: the Disc badge shows; releases are ordered by disc; a game with one disc flag keeps today's order. Done when: lint, check and that file pass.
- [ ] **11. e2e and docs**: Files: `tests/e2e/specs/mvp.spec.ts`, `docs/features.md`, `docs/development.md`. Tests: the e2e `review queue: resolve with a suggestion` sees the progress state and lands on the game (it runs in Finish). Done when the docs describe:
  - resolving as a job, with the endpoint's `202`, the job page, no Cancel for it, and the audit row's job id (`features.md`); `development.md`'s "One job at a time" lists review resolution as a job, with Dismiss and Delete still synchronous;
  - disc numbers on Attach and Manual;
  - resolving sets, with their limits (`AmbiguousSet`, `AlreadyInReview` members, archives, track 1 as the main file, and Delete, per member, to clear rows left Pending after a `DbWriteFailed`), replacing `features.md`'s "a `.cue` or `.bin` can't be resolved in Review" and "none for a disc track file".

## Decisions
- 2026-11-13 How should a slow resolve run? → as a background job with the existing step progress bar. On the page: open the game when done. Left the page: a toast with a link, never navigation (user)
- 2026-11-13 How is a disc number stored? → as a `Disc N` release flag, like DAT-matched discs; no info.json schema change (user)
- 2026-11-13 Unmatched bin/cue sets → resolvable from Review as one release, names kept (user)
- 2026-11-13 Disc 2 failure seen → disc 1 matched the DAT, disc 2 sat in Review (user)
- 2026-11-13 Disc number on existing releases → not in this spec; only new imports (user)
- 2026-11-13 Disc numbers on images → dropped (user)
- 2026-11-13 Every resolve runs as a job, embedded ones too → one code path in API and SPA (code/default)
- 2026-11-13 Attach as a disc copies regions/languages/revision from the target's primary release (default)
- 2026-11-13 A set's main file without a DAT match → the cue's first `FILE` (default)
- 2026-11-13 Any set member's page can resolve the set; Dismiss stays refused for set members (default)
- 2026-11-13 `SourceMissing` keeps today's rule (row deleted); the toast and job page show no review link for it (reviewer R1)
- 2026-11-13 Item checks run in the endpoint before the start and again in the job under the lock; `IJob.Preflight` unused (reviewer R2)
- 2026-11-13 Non-cancellable jobs: `CancelResult`, `409 NotCancellable`, shutdown doesn't cancel them; the job uses `CancellationToken.None` (reviewer R3, R4)
- 2026-11-13 Set storage → new `IngestPipeline.StoreSetReleaseAsync`, the set types stay private; a member changed mid-resolve is `VerifyFailed` (reviewer R5)
- 2026-11-13 Dat pick on a set: every member's extension and size must fit; metadata from the DAT game's first rom; identification Manual + DAT name (reviewer R6)
- 2026-11-13 Steps reordered and split: SPA job flow right after the API change, `IsResolvable` switches with set resolution, set Dat pick, review UI, game page and e2e/docs as separate steps (reviewer R7, R8)
- 2026-11-13 Set rows Resolved in one transaction from `afterMirror`. A failure after the move leaves Pending, unresolvable rows, which the Admin clears with Delete (reviewer R9; round 2 R1)
- 2026-11-13 No `SetChanged` code: a set no longer found in the job is `SetFileNotResolvable` (round 2 R3, round 3 R2)
- 2026-11-13 Delete stays per member (by source path); clearing rows after a post-move `DbWriteFailed` takes one Delete per member (round 3 R1)
- 2026-11-13 Between steps 7 and 8 (numbered 6 and 7 before review pass 1's split), a Dat pick on a set is refused with `ValidationFailed` `action` (round 2 R2)
- 2026-11-13 A set member skipped as `AlreadyInReview` leaves the set unresolvable; documented (reviewer R10)
- 2026-11-13 Sets: `SetTotal(N)`, one job item per member (reviewer R11)
- 2026-11-13 No re-attach to a running job after a reload or in another tab; actions get the standard `JobRunning` toast (reviewer R12)
- 2026-11-13 `disc` only on `FileSets` systems (reviewer R13)
- 2026-11-13 `IsResolvable` becomes async I/O; an unreadable folder or cue → not resolvable, never a 500 (reviewer R14)
- 2026-11-13 Job actor → `JobContext.Actor` from `requestedBy`, not one set by the endpoint; the endpoint sets `ReviewId`, `FileName`, `Request` (code, review pass 1)
- 2026-11-13 `CancelResult` would break `JobEndpoints`' build in the Core step → old step 2 split: new step 2 non-cancellable jobs (Core + API), steps 3–11 renumbered (code, review pass 1)
- 2026-11-13 Ingestion and Integrity pages show Cancel for any running job → hidden for `ReviewResolve` through `isCancellable` in `jobs.ts` (code, review pass 1)
- 2026-11-13 Removing `ResolveAsync` breaks `ExternalLibraryTests`, `IngestPipelineTests`, `TitleDbIngestTests` → moved to `ResolveInJobAsync` in step 3; their HTTP resolves wait for the job in step 4 (code, review pass 1)
- 2026-11-13 `NotFound` is also today's code for a missing Attach target; a gone item writes no job item; `fileName` comes from the job (code, review pass 1)
- 2026-11-13 Job items get `Guid.NewGuid()` file ids, as `IngestJob` does (code, review pass 1)
- 2026-11-13 Set lookup: the system folder as `IngestFileScanner` lists it, `ReadCue` shared, new `ListPendingByPathsAsync` (code, review pass 1)
- 2026-11-13 `StoreSetReleaseAsync` takes `(IngestFile, Sha1)` members and an `InfoJsonRom` + `DatMetadata`, as `StoreReleaseAsync` does (code, review pass 1)
- 2026-11-13 `Validate` accepts the Attach flag `Disc` (code, review pass 1)
- 2026-11-13 Review job store keeps the per-action success text; first poll 1.5 s after the `202`; a job with no summary is a failure with its `error` (default, review pass 1)
- 2026-11-13 Docs also update the audit row's job id and `development.md`'s "One job at a time" (code, review pass 1)

## Kickoff prompt
```text
/spec implement 2026-11-13-review-jobs-multi-disc.md
```
Starts the spec, or resumes it at the first unticked step.
