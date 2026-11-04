# Features

Implementation is in progress; this guide covers the ingestion review behaviour implemented through S30.

## Ingestion review queue

An unmatched ROM with a plausible header stays in ingest and creates a `Pending` review item with outcome `Review NoDatMatch`. A headerless ROM uses its ingest folder's system. If DAT identification succeeds but several games own the same system and base title, the item becomes `Review AmbiguousGroup`; it stores the DAT identity and candidate game IDs.

Unmatched items include up to five suggestions for distinct base titles from the classified system's loaded DATs. The scorer compares the header title and original filename stem, using the higher of token overlap and edit similarity, plus a size-match boost. Each base title contributes its highest-scoring release; ties use ascending ROM name. Suggestions below 0.4 are excluded, and suggestions refresh whenever the file is processed again.

Files behind pending items are processed on every ingest run. If a file becomes DAT-matched and is ingested, its review item becomes `Resolved` with the resulting game ID. Other non-review outcomes remove its pending row; files skipped because they are too recent retain their row. A copy at another source path becomes a duplicate while the original review remains pending. Missing pending sources are removed at the start of the run.

Re-dropping a dismissed loose file resets it to pending and processes it again. For an archive, the review item records the archive path and entry name. Review or error outcomes keep the archive in ingest. A dismissed entry in the same archive fails with reason `Dismissed`, causing the archive to move to `_failed/` unless another entry still needs review or encounters an error. A duplicate previously resolved from the same archive counts as ingested when choosing the archive's fate, allowing a fully resolved archive to be deleted on its next run.

## Resolve reviews through the API

All review endpoints require an Admin session, and POST requests require the antiforgery header. The review UI arrives in S45.

- `GET /api/review?status=Pending&page=1&pageSize=48` lists pending items (maximum page size 200). `Resolved` and `Dismissed` history can also be listed.
- `GET /api/review/{id}` returns the header, hashes, suggestions, queued DAT identity and candidate games.
- `GET /api/dat/search?system=gb&q=Tetris&limit=20` searches loaded DAT game names using a case-insensitive substring (up to 20 results).
- `POST /api/review/{id}/resolve` accepts `Dat`, `Attach` or `Manual` actions and returns `{gameId}`.
- `POST /api/review/{id}/dismiss` returns 204. Loose files move to `_failed/` with a `Dismissed` sidecar; archives stay in place until the next ingest run.

`Dat` takes `datSystem` and `datGameName` for a NoDatMatch item. The selected DAT supplies naming, tags and metadata; the release keeps its actual hashes, uses Manual identification with the selected DAT identity, and gains an `Unverified` flag and filename suffix.

`Attach` takes `targetGameId` and `flag` (`Hack`, `Translated` or `None`). Hack/Translated attachments use the target game's title as their base title, empty regions/languages, null revision and only the selected flag. `None` is available only for AmbiguousGroup items and requires one of their candidate games; it keeps the queued DAT identification and normal DAT filename.

`Manual` takes `title`, `regions[]`, `languages[]`, optional `revision`, and `flags[]`. Naming uses the title, regions, revision and flags; the release receives no DAT metadata. DAT and Manual actions group into existing games using the same rules as ingestion. Multiple candidates return `409 AmbiguousGroup`; choose Attach to select the game.

Resolution and dismissal take the global job lock (`409 JobRunning` when busy). A missing source returns `409 SourceMissing` and removes its stale review row. Successful resolution records the game, Admin and UTC time, then deletes a loose source; resolving an archive entry leaves its archive in place. Changed source bytes fail verification.

Failures retain a Pending review for retry. An unreadable target ZIP or newer schema returns 409 with `TargetZipUnreadable` or `InfoJsonNewerSchema`. IO, verification and DB-write failures return 500 with `IoError`, `VerifyFailed` or `DbWriteFailed`. A DB mirror failure can leave a verified ZIP on disk; the next ingest pre-pass repairs the DB from that ZIP before processing sources, as with normal ingestion.
