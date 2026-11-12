---
title: Large ROM import progress
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-11
---

# Large ROM import progress

## Summary
Importing a Switch ROM (several GB) takes 1–5 minutes, and the Ingestion page shows nothing but `0 / 1 · <file>` the whole time. The time goes into two streamed steps: hashing the file (one full read) and, when `ingest/` and `roms/` are on different volumes or the file system can't rename/hard-link, copying it into the library. This spec adds live per-file progress for those streamed steps — phase, bytes done of total, speed and time left — to the job progress API, and shows it as a second bar wherever the job bar is shown today. A deep integrity check's re-hash of external ROMs reports the same way. For Admins.

## Out of scope
- Small in-zip ROMs (read into memory; they finish in well under a second). They report no step, so no second bar appears.
- Archive extraction (capped at 256 MB total per archive), the in-memory hash of normal ROMs, DAT refresh and media retry jobs.
- Review resolutions and library edits: they run inside one HTTP request, not a job, so there is nothing to poll. `ReviewResolver`'s and `LibraryEditor`'s calls to the changed methods pass no progress.
- Push updates (SSE/WebSockets): the SPA keeps polling `GET /api/jobs/current` every 1.5 s.
- Persisting step progress: like the rest of §3.1 progress it is in memory only.

## Behaviour
- **B1 Step.** While a job runs a streamed step on one file, `GET /api/jobs/current` returns a `step` object next to `current`/`total`/`currentItem`:
  `{ "phase": "Hashing" | "CopyingToLibrary" | "Copying", "bytesDone": <long>, "bytesTotal": <long>, "bytesPerSecond": <long|null> }`. With no streamed step running, `step` is `null`. The field is additive; nothing else in the response changes.
- **B2 Phases.**
  - `Hashing`: the ingest pipeline's streamed hash of an external ROM (`HashCheckedAsync`, from `ProcessExternalAsync`), and the deep integrity check's SHA1 of an external ROM. `bytesTotal` is the file's length from its checked snapshot.
  - `CopyingToLibrary`: `ExternalRomMove`'s copy fallback (`CopyAndPublish`) when the ingest pipeline places a ROM in the library (`Place`).
  - `Copying`: the copy fallback of the pipeline's other `ExternalRomMove.Move` calls (`MoveAsideChecked` to `_failed/`/`_duplicates/`, and the rollback and orphan moves in `RemoveIncoming`), which can take as long.
  - A same-volume rename is instant and reports no step. For both copy phases `bytesTotal` is the expected snapshot's length.
- **B3 Lifetime.** A step starts at 0 bytes when its phase begins and is cleared (`step: null`) when the phase ends, on success, failure, a changed source or an exception. Starting the next phase replaces the previous one. `SetCurrentItem` also clears any step, so a new file never shows the previous file's bar. `bytesDone` never exceeds `bytesTotal` in the response (a file that grows mid-read is clamped; the hasher stops at the cap as today).
- **B4 Speed.** `bytesPerSecond` is the average since the phase began: `bytesDone / elapsed`, computed by `JobRunner` with its `TimeProvider` when the snapshot is taken. It is `null` until at least 1 second has elapsed and `bytesDone > 0`, so the first poll doesn't show a wild figure.
- **B5 Reporting cost.** Progress is written once per buffer read (1 MiB), with `Volatile`/`Interlocked` writes only; no locks, allocations or logging per buffer. Hash and copy results are unchanged.
- **B6 Cancel.** Unchanged: an external ROM's item is never cancelled mid-file (I2), so the step runs to its end; the deep integrity hash keeps honouring its token, and the step is cleared when it throws.
- **B7 UI.** On the Ingestion page Run section, the Integrity page's running block and the `/jobs/{id}` live Progress row, when `step` is non-null a second, thinner progress bar (`JobStepProgress`) appears with this line beneath it:
  `Hashing · 3.2 GiB / 8.0 GiB · 95.0 MiB/s · about 50 s left` (labels: `Hashing`, `Copying to library` for `CopyingToLibrary`, `Copying` for `Copying`, the raw phase for anything else; sizes via `formatSize`, speed as `formatSize(bytesPerSecond) + '/s'`). Speed and time left are left out while `bytesPerSecond` is null; time left is also left out when it is 0. With `bytesTotal` 0 the bar is at 0 % and time left is left out. Time left is `ceil((bytesTotal − bytesDone) / bytesPerSecond)` seconds, shown as `about N s left` under a minute, `about N min left` (rounded up) from a minute. The bar has `role="progressbar"`, `aria-label="Current file progress"`, and `aria-valuenow` = percent of bytes. The component has no `aria-live` ancestor (it changes every poll): on the Ingestion and Integrity pages the existing `aria-live="polite"` is narrowed from the whole running-job card to the job's text line (`current / total · currentItem`), and the component goes directly under the job bar, outside any live region. On `/jobs/{id}`, which has no job bar, it goes in a new `<dt>Current file</dt><dd>` row after the Progress row, shown only while `step` is non-null; the Progress `<dd>` keeps its `aria-live`. When `step` is null nothing extra is rendered. Permissions are unchanged (Admin pages; `GET /api/jobs/current` keeps its role-matrix entry).
- **B8 Sizes app-wide.** `formatSize` gains a GiB step: from 1 GiB up it shows `8.0 GiB` (one decimal, as MiB does today). This changes every size shown in the app at or above 1 GiB (e.g. Switch ROM sizes in lists and game pages), which is intended.
- **B9 Older servers.** The SPA treats a missing `step` field like `null`.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Jobs/IJob.cs` | `JobProgress` gains `BeginStep(string phase, long bytesTotal, DateTime startedAtUtc)`, `StepAdvanced(long bytesDone)`, `EndStep()`, and `SetCurrentItem` clears the step. The step's phase/total/start are one immutable record swapped with `Volatile.Write`; `bytesDone` is a separate `long` written with `Volatile.Write` and reset to 0 before the record is published. New constants `JobStepPhases.Hashing`/`CopyingToLibrary`/`Copying`. New `JobStepSnapshot(string Phase, long BytesDone, long BytesTotal, long? BytesPerSecond)`; `JobSnapshot` gains `JobStepSnapshot? Step` (last parameter). |
| Core | `src/RnRomManager.Core/Jobs/JobRunner.cs` | `Snapshot()` builds `Step` from the progress with `time.GetUtcNow()` (B4, clamp from B3). It reads the step record **before** `bytesDone`; since `BeginStep` resets `bytesDone` before publishing the record, a new step is never paired with the previous step's bytes, and the clamp only matters for files that grow. |
| Core | `src/RnRomManager.Core/Storage/RomFileHasher.cs` | `HashAsync` gains an optional `Action<long>? onProgress` (bytes read so far), called after each buffer. |
| Core | `src/RnRomManager.Core/Storage/ExternalRomMove.cs` | `Move` gains an optional `Action<long>? onCopied` (bytes written so far), passed to `CopyAndPublish`: called once with `0` after the staging file is created and before the first read (so the caller begins the `Copying` step only when the copy fallback actually runs), then after each buffer with the cumulative count. Never called on the rename path. |
| Core | `src/RnRomManager.Core/Ingest/IngestPipeline.cs` | Optional `JobProgress? progress = null` added to public `ProcessAsync`, `FailFile`, `MoveToFailed`, `MoveToDuplicates`, and to the private methods on the way to every external hash and `ExternalRomMove.Move` call (the rule is every method on the path; today that is `ProcessExternalAsync`, `HashCheckedAsync`, `IdentifyAsync`, `NewGameAsync`, `AddToGameAsync`, `DuplicateOrRepairAsync`/`RepairExternalAsync`, `PackageAsync`, `Place`, `RemoveIncoming`, `MoveAside`/`MoveAsideChecked`). Each streamed phase is wrapped `BeginStep … finally EndStep` (B3). Order: in `HashCheckedAsync`, `BeginStep(Hashing)` is called after the checked open succeeds and **before** `BeforeHash`; `EndStep` after the hash. In `Place` (CopyingToLibrary) and the other moves (Copying), the step begins on `onCopied(0)`. `StoreReleaseAsync` (Review) and `ReviewResolver`'s calls pass null. New test hook `ExternalIngestHooks.WrapHashStream: Func<Stream, Stream>?`, applied to the hash stream, so tests can block mid-read. New test hook `ExternalIngestHooks.Aside: ExternalMoveHooks?`, passed by `MoveAsideChecked` to its `ExternalRomMove.Move` call (today it passes none), so a test can force the copy fallback for `_failed/`/`_duplicates/`. |
| Core | `src/RnRomManager.Core/Ingest/IngestJob.cs` | Passes `run.Context.Progress` to `pipeline.ProcessAsync` and to its own archive-level `FailFile`/`MoveToFailed`/`MoveToDuplicates` calls. |
| Core | `src/RnRomManager.Core/Integrity/IntegrityJob.cs` | `CheckExternalAsync` gains a `JobProgress progress` parameter (the caller passes `context.Progress`). Deep external hash: in the `Found` branch, `BeginStep(Hashing, snapshot.Length)` before reading, then replace `SHA1.HashDataAsync(stream, ct)` with a 1 MiB chunked `IncrementalHash` loop that reports progress (same result, same token), `EndStep` in a `finally`. New test hook `IntegrityHooks.WrapHashStream: Func<Stream, Stream>?` applied to the hash stream. |
| Api | none | `JobSnapshot` is serialized as-is; `step` appears in `GET /api/jobs/current` (camelCase). |
| Web | `src/RnRomManager.Web/src/lib/format.ts`, `format.test.ts` | `formatSize` GiB step (B8). |
| Web | `src/RnRomManager.Web/src/lib/api/types.ts` | `CurrentJob.step?: JobStep | null`; `JobStep` type. |
| Web | `src/RnRomManager.Web/src/lib/jobs.ts` | `formatStep(step)` → the B7 line; `stepPercent(step)`. |
| Web | `src/RnRomManager.Web/src/lib/components/JobStepProgress.svelte` (new) | The second bar + line; renders nothing for null/undefined. |
| Web | `routes/ingestion/+page.svelte`, `routes/integrity/+page.svelte`, `routes/jobs/[id]/+page.svelte` | Render `JobStepProgress` (B7 placement); narrow the Ingestion/Integrity `aria-live` to the job text line. |
| Docs | `docs/features.md`, `docs/development.md` | features.md: Ingestion "Import all" bullet, Integrity "Run deep" bullet and `/jobs/{id}` paragraph describe the per-file bar; a new `GET /api/jobs/current` line in the API section gives the `step` shape and phases. development.md: the "One job at a time" note mentions per-file step progress. |

No migrations, no config keys. The API change is additive.

## Risks
- **Torn reads:** none given the order: `BeginStep` resets `bytesDone` before publishing the record and `Snapshot` reads the record before `bytesDone`, so a new step is never paired with old bytes. The clamp (B3) only covers files that grow while read.
- **Performance:** one delegate call and one volatile write per MiB is negligible next to the I/O and three hashes.
- **Signature churn:** `ExternalRomMove.Move` and `RomFileHasher.HashAsync` have several callers (`LibraryEditor`, `ReviewResolver`); new parameters are optional, so they compile unchanged.

## Steps
- [x] **1. Step progress model and API field**: `JobProgress` step methods, `JobStepPhases`, `JobStepSnapshot`, `JobSnapshot.Step`, `JobRunner.Snapshot` speed and clamp. Files: `src/RnRomManager.Core/Jobs/IJob.cs`, `src/RnRomManager.Core/Jobs/JobRunner.cs`, `tests/RnRomManager.Core.Tests/Jobs/JobRunnerTests.cs` (and a new `JobProgressTests.cs`), `tests/RnRomManager.Api.Tests/Integration/JobEndpointsTests.cs`. Tests: NUnit — BeginStep/StepAdvanced/EndStep values; SetCurrentItem clears the step; BeginStep resets bytes; snapshot with `FakeTimeProvider`: `bytesPerSecond` null before 1 s and when `bytesDone = 0`, average after; `bytesDone` clamped to total. API integration: `GET /api/jobs/current` returns `step: null` for a running job without a step, and the `step` object (camelCase fields) while a test job holds one. Done when: B1, B3 (model part), B4 are covered by passing tests; existing `JobRunnerTests`/`JobEndpointsTests` still pass.
- [x] **2. Hashing and copying report progress during ingestion**: `RomFileHasher.HashAsync` `onProgress`; `ExternalRomMove.Move` copy-progress parameters; `IngestPipeline.ProcessAsync` `progress` threaded to `HashCheckedAsync` and the `Move` calls; `IngestJob` passes it. Adds `ExternalIngestHooks.WrapHashStream` and `ExternalIngestHooks.Aside`. Files: `RomFileHasher.cs`, `ExternalRomMove.cs`, `IngestPipeline.cs`, `IngestJob.cs`; tests in `tests/RnRomManager.Core.Tests/Storage/RomFileHasherTests.cs` (create if missing), `tests/RnRomManager.Core.Tests/Storage/ExternalRomMoveTests.cs`, and `tests/RnRomManager.Api.Tests/Integration/ExternalLibraryTests.cs` (external ingest, with `WrapHashStream`, `Move.WrapSource` and `Move.Rename` forcing `Unsupported`). Tests: hasher reports cumulative bytes ending at file length and hashes are unchanged; copy fallback calls `onCopied(0)` then cumulative bytes ending at length, rename path never calls it; during an external ingest a blocking wrapped stream lets the test observe a `Hashing` step with `bytesDone > 0`, then a `CopyingToLibrary` step, with the right totals on the job's `JobProgress`; the step is null after the item, also when the copy fails (source changed); a ROM moved to `_duplicates/` with `Aside.Rename` forcing `Unsupported` reports `Copying`. Done when: B2 (ingest), B3, B6 (ingest) and "hash/copy results unchanged" (B5) are covered; `dotnet test tests/RnRomManager.Core.Tests` and `dotnet test tests/RnRomManager.Api.Tests` pass.
- [x] **3. Deep integrity hash reports progress**: chunked `IncrementalHash` loop in `IntegrityJob` reporting `Hashing`. Adds `IntegrityHooks.WrapHashStream`; `CheckExternalAsync` takes the job's `JobProgress`. Files: `src/RnRomManager.Core/Integrity/IntegrityJob.cs`, `tests/RnRomManager.Data.Tests/Database/IntegrityJobTests.cs` (Testcontainers). Tests: a deep check of an external ROM, blocked mid-read through `WrapHashStream`, shows a `Hashing` step with `bytesTotal` = file length and `bytesDone > 0`; step null after; SHA1/HashMismatch findings unchanged; cancellation mid-hash clears the step. Done when: B2 (integrity) and B6 (integrity) covered; `dotnet test tests/RnRomManager.Data.Tests` passes.
- [ ] **4. SPA second progress bar and GiB sizes**: `formatSize` GiB step, types, `formatStep`/`stepPercent`, `JobStepProgress.svelte`, used on Ingestion, Integrity and `/jobs/{id}`, with the narrowed `aria-live`. Files: as in § Changes; tests in `src/lib/format.test.ts`, `src/lib/jobs.test.ts`, a component test for `JobStepProgress`, and `routes/ingestion/ingestion-page.test.ts`. Tests (vitest): `formatSize` below, at and above 1 GiB; `formatStep` for each phase label, with null speed, 0 and under/over a minute left, zero total; `stepPercent` 0 for zero total and capped at 100; the component renders nothing for null/undefined `step` and a bar with `aria-label="Current file progress"` and the right `aria-valuenow` otherwise; the Ingestion page shows the step line when `jobs.current.step` is set, with no `aria-live` ancestor. Done when: B7, B8, B9 covered; existing tests that assert MiB sizes at or above 1 GiB are updated; `npm run lint`, `npm run check`, `npm test` pass.
- [ ] **5. Docs**: `docs/features.md` (Ingestion Run section, Integrity deep check, `/jobs/{id}`, `GET /api/jobs/current`), `docs/development.md` progress note. Done when: B1, B2, B4, B7 and B8 are described at the doc spots named in § Changes.

## Decisions
- 2026-11-11 What feedback while a large file imports? → Second bar with phase, bytes, speed and time left (user)
- 2026-11-11 Which files report it? → Only streamed (external-ROM) steps; small in-zip ROMs show no second bar (user)
- 2026-11-11 Include the deep integrity check's re-hash? → Yes (user)
- 2026-11-11 Where shown? → Everywhere the job bar is today: Ingestion, Integrity, `/jobs/{id}` (user)
- 2026-11-11 Speed source → server-computed average since the phase began, so client/server clock skew doesn't matter (code)
- 2026-11-11 Show GiB sizes? → Add a GiB step to `formatSize` app-wide (user)
- 2026-11-11 Copy label for non-library moves → separate `Copying` phase so `_failed/`/`_duplicates/`/rollback copies aren't labelled "to library" (reviewer)
- 2026-11-11 Screen readers → step bar outside any live region; existing live regions narrowed to the job text (reviewer)
- 2026-11-11 Test seams → new `WrapHashStream` hooks on ingest and integrity (reviewer)
- 2026-11-11 The dev machine has only the .NET 11 SDK → `global.json` `rollForward` set to `latestMajor` on this branch (user)
- 2026-11-11 How progress reaches the private placement path → carried on the private `ExternalSource` record (`Progress`), which already flows from `ProcessExternalAsync` through `IdentifyAsync`/`NewGameAsync`/`AddToGameAsync`/`PackageAsync` to `Place` and `RemoveIncoming`; the public methods and the move-aside chain take an explicit `JobProgress?` parameter (code)
- 2026-11-11 Transport → keep 1.5 s polling of `GET /api/jobs/current`; no SSE (code)

## Kickoff prompt
```text
/spec implement 2026-11-11-large-rom-import-progress.md
```
Starts the spec, or resumes it at the first unticked step.
