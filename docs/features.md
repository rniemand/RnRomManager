# Features

Implementation is in progress; this guide covers the ingestion review behaviour implemented through S30, the library read API from S32, the library edits from S33–S34, media retry from S35 and the integrity checker and its API from S36–S37.

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

## Browse the library through the API

Every signed-in user (Admin or User) can read the library; the Library and Game detail pages arrive in S41 and S42. Lists and details come from the database mirror; images and ROM bytes are streamed from the game's ZIP.

- `GET /api/systems` lists every registered system with its game count, ROM count and the distinct regions of its ROMs (sorted). Systems without games report zero counts and no regions.
- `GET /api/games` returns a page of `{id, system, displayTitle, releaseYear, regions[], romCount, boxArtUrl}` (48 per page by default, maximum 200; a page past the end is empty).
  - `system` must be a registered system id. `letter` is `#` or `A`–`Z` (any case) and matches the ZIP's letter folder; send `#` as `%23`.
  - `region` matches a game when any of its releases has exactly that region. `q` is a case-insensitive substring of the display title or title; `%`, `_` and `\` match literally.
  - `sort` is `title` (default), `year` or `added`; `dir` is `asc` or `desc`, defaulting to `asc` for title/year and `desc` for added. Games without a year are listed last in both directions, and equal values are ordered by game id.
  - Invalid values return `400 ValidationFailed` with a per-field `errors` object.
- `GET /api/games/{id}` returns the game's `info.json` fields (without `schemaVersion` and `metadata.appVersion`) plus `id` and `hasDefaultBoxArt`. `roms[]` lists the primary release first, then by file name, with identification and hashes flattened (`identificationMethod`, `datName`, `crc32`, `sha1`, `sizeBytes`, …), the parsed `header` and a `downloadUrl`. `images[]` holds `id`, `type`, `isDefault`, `width`, `height` and `url`.
- `GET /api/games/{id}/images/{imageId}` returns the PNG with `ETag` set to its SHA1 and `Cache-Control: private, max-age=86400`; a matching `If-None-Match` returns 304.
- `GET /api/games/{id}/download` returns the game's ZIP with range support and an RFC 5987 `filename*` in `Content-Disposition`.
- `GET /api/games/{id}/roms/{romId}/download` streams one ROM from the ZIP under its library file name.

ROM and image ids change whenever a game is rewritten. A stale id, an image or ROM belonging to another game, or a ZIP missing from disk returns `404 NotFound`; an unreadable ZIP returns `500 IoError`. ZIPs are opened so that an ingest or edit can still replace them during a download.

## Edit the library through the API

Admins can edit metadata, delete, move and merge; the Game detail admin actions arrive in S43. These requests need the antiforgery header. Each edit takes the global job lock, so it returns `409 JobRunning` while a job or another edit runs. Every edit starts from the game ZIP's own `info.json`, writes the ZIP atomically, then updates the database.

- `PATCH /api/games/{id}` takes any of `title`, `releaseYear`, `developers`, `publishers`, `genres` and `description`, and returns the updated game (same shape as `GET /api/games/{id}`). Omitted fields stay unchanged; `releaseYear` and `description` accept `null` to clear them.
  - `title`: 1–255 characters after trimming, and it must not sanitize to an empty file name. `displayTitle` is always derived from it.
  - `releaseYear`: `null` or 1970–2100. Each list: at most 20 items of 1–255 characters, trimmed with duplicates removed. `description`: at most 4000 characters.
  - A title change renames the ZIP (`roms/<system>/<letter>/<title>.zip`, with ` (2)`, ` (3)`, … if another game already has the name). The new ZIP is written and committed before the old one is deleted. A change that only differs in letter case keeps the existing file, because file names compare case-insensitively.
- `DELETE /api/games/{id}/roms/{romId}` returns 204. It moves the ROM's bytes to `ingest/<system>/_removed/` (adding ` (2)`, … on a name clash), rebuilds the ZIP without it and recomputes the primary release. Removing the last release deletes the game.
- `DELETE /api/games/{id}` returns 204. It moves every ROM to `_removed/`, then deletes the ZIP and the game's rows.
- `POST /api/games/{id}/roms/{romId}/move` takes `{targetGameId}` (an existing game) or `{newGameTitle}` (a new game), and returns `{targetGameId}`. Both games' ZIPs are rebuilt with the primary release recomputed, and a game left without releases is deleted. A new game gets the metadata of the moved release's DAT entry and its own box art and snap lookup; an existing target's metadata and images stay as they are. If the source ZIP can't be rebuilt after the target was written, the target is put back.
- `POST /api/games/{id}/merge` takes `{targetGameId}` and returns it. Every release of the source moves into the target in one write; then the database is updated and the source ZIP deleted. The target keeps its title, metadata and images, except that empty fields (year, developers, publishers, genres, description) are filled from the source, a default target box art is replaced by a real source box art, and the source snap is used only when the target has none.
- Move and merge targets must be a different game in the same system (`400 ValidationFailed` otherwise); an unknown target is `404 NotFound`. A moved release keeps its file name (with ` (2)`, … on a clash) and its base title. Later releases with that base title therefore follow it when it is the only game holding the title; if the source still holds it too, later releases go to review as `AmbiguousGroup`.

Removed ROMs are never hard-deleted, and ingestion ignores `_removed/`; move a file back into `ingest/<system>/` to re-ingest it. A ROM whose ZIP entry no longer matches its recorded SHA1 is still moved as it is; a listed entry that is absent from the ZIP has nothing to move and is skipped.

Errors: an unknown game, a ROM of another game, or a release the ZIP no longer lists return `404 NotFound`. A ZIP that can't be read or has a newer `info.json` schema returns `409 TargetZipUnreadable` or `409 InfoJsonNewerSchema` without changing anything. A corrupt entry returns `500 VerifyFailed`, and an IO failure returns `500 IoError`; in both cases the ZIP is unchanged and any copies already made in `_removed/` are taken back. `500 DbWriteFailed` means the ZIP changed but the database didn't; the next ingest or integrity run repairs the database from the ZIP.

## Retry missing media

Thumbnails come from libretro-thumbnails, looked up by the primary release's DAT game name first, then the other releases in rank order; releases without a DAT game name (Manual) are skipped. Both actions need an Admin session and the antiforgery header, and take the global job lock (`409 JobRunning` when busy).

- `POST /api/games/{id}/media/retry` (Game detail, S43) re-runs the lookup for one game whose box art is the default or missing, or which has no snap, and returns the game. A found box art replaces only a default one, and a missing box art gets at least the default. A found snap is added only when the game has none. The game is rewritten only when something changed; finding nothing still returns 200 with the game unchanged. Errors are the same as for the other library edits.
- `POST /api/jobs/media-retry` (Integrity page, S46) starts a `MediaRetry` job and returns `202 {jobId}`. It runs the same lookup for every game with the default box art, no box art or no snap, ordered by title. Games where every release is Manual are skipped. Per-game errors are logged and the job continues. The summary is `{updated, notFound, skipped, error}`; there are no job items.
- After 3 consecutive network failures (timeouts, connection errors, or 5xx/408 after retries; a 404 doesn't count), the job stops and ends `Failed` with the error `thumbnail host unreachable`. Games already updated keep their new media.

## Integrity checker

The `Integrity` job checks the library against its `info.json` files and the database. It runs as a quick check every day at `Integrity:QuickCheckTime` (see [configuration](configuration.md)), or on demand; the Integrity page arrives in S46. It takes the global job lock (a scheduled run is skipped when another job is running) and never moves or deletes library files.

1. **Database repair.** Each ZIP's database rows are replaced from its `info.json`; rows whose ZIP is gone are deleted unless the same game id turned up at another path. Every repair is recorded as an `AutoRepaired` issue (`DbRowInserted`, `DbRowUpdated`, `DbRowDeleted`). ZIPs with a duplicate game id, or with a ROM another game already holds, keep their existing rows and are reported as `DuplicateGameId` / `DuplicateSha1`.
2. **Scan.** Every file directly in `roms/`, everything under a folder that isn't a registered system, and every non-ZIP file (including stale `*.zip.tmp`) under a system folder is an `OrphanFile`; these are never opened. Every `.zip` under a system folder is checked.
3. **Per ZIP.** Quick mode opens a ZIP only when its size or whole-second modification time differs from its database row; otherwise it checks only the location. Deep mode opens every ZIP and also recomputes the SHA1 of every ROM and image entry (`HashMismatch`). An opened ZIP is checked for `ZipUnreadable`, `InfoJsonMissing` / `InfoJsonInvalid` / `InfoJsonNewerSchema`, `EntryMissing`, `EntrySizeMismatch`, `EntryUnlisted`, `MissingBoxArt` and `DefaultBoxArt` (Info). Every ZIP is checked for `WrongLocation`: system folder, letter folder and file name must match its title (case-insensitively; a ` (2)`-style clash suffix is accepted).

An issue found again updates the open issue with the same type, path and entry rather than adding a new one. A completed run resolves an open issue it didn't find again, but only if it actually performed that check: a quick run can't clear entry or `info.json` problems on a ZIP it didn't open, and only a deep run clears `HashMismatch`. Orphans and duplicates are re-checked by every run, and an issue on a ZIP that no longer exists is resolved. A cancelled run records what it found but resolves nothing.

At the end of each completed run, jobs started more than 180 days ago (with their items) and resolved or auto-repaired issues older than 180 days are deleted. The job summary holds `zips`, `opened`, `issues`, `newIssues`, `resolved`, `repaired`, `purgedJobs` and `purgedIssues`.

All integrity endpoints need an Admin session; the POST also needs the antiforgery header.

- `POST /api/jobs/integrity` with `{"mode": "Quick"}` or `{"mode": "Deep"}` starts a run and returns `202 {jobId}` (`409 JobRunning` when busy, `400 ValidationFailed` for another mode). Follow it with `GET /api/jobs/{jobId}`.
- `GET /api/integrity/status` returns `{lastQuick, lastDeep, nextScheduledAt}`: the latest job of each mode (or `null`) and the next scheduled quick check in UTC (`null` when the schedule is disabled).
- `GET /api/integrity/issues?status=&type=&page=&pageSize=` returns a page of issues, newest first. `status` is `Open`, `AutoRepaired` or `Resolved`; `type` is any issue type above.
