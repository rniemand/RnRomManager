# Features

Implementation is in progress; this guide covers the ingestion review behaviour implemented through S29.

## Ingestion review queue

An unmatched ROM with a plausible header stays in ingest and creates a `Pending` review item with outcome `Review NoDatMatch`. A headerless ROM uses its ingest folder's system. If DAT identification succeeds but several games own the same system and base title, the item becomes `Review AmbiguousGroup`; it stores the DAT identity and candidate game IDs.

Unmatched items include up to five suggestions for distinct base titles from the classified system's loaded DATs. The scorer compares the header title and original filename stem, using the higher of token overlap and edit similarity, plus a size-match boost. Each base title contributes its highest-scoring release; ties use ascending ROM name. Suggestions below 0.4 are excluded, and suggestions refresh whenever the file is processed again.

Files behind pending items are processed on every ingest run. If a file becomes DAT-matched and is ingested, its review item becomes `Resolved` with the resulting game ID. Other non-review outcomes remove its pending row; files skipped because they are too recent retain their row. A copy at another source path becomes a duplicate while the original review remains pending. Missing pending sources are removed at the start of the run.

Re-dropping a dismissed loose file resets it to pending and processes it again. For an archive, the review item records the archive path and entry name. Review or error outcomes keep the archive in ingest. A dismissed entry in the same archive fails with reason `Dismissed`, causing the archive to move to `_failed/` unless another entry still needs review or encounters an error. A duplicate previously resolved from the same archive counts as ingested when choosing the archive's fate, allowing a fully resolved archive to be deleted on its next run.

Review resolution endpoints and DAT search arrive in S30; the review UI arrives in S45.
