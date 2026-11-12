---
title: Disc systems (PlayStation, PSP, GameCube, Wii, Xbox 360, Saturn)
status: in-progress
created: 2026-11-12
---

# Disc systems (PlayStation, PSP, GameCube, Wii, Xbox 360, Saturn)

## Summary
Six disc-based systems join the library: PlayStation (`psx`), PlayStation Portable (`psp`), GameCube (`gc`), Wii (`wii`), Xbox 360 (`x360`) and Saturn (`saturn`). All store their ROMs outside the ZIP (like Switch), under their exact file names, and are matched against libretro's Redump DATs. To support bin/cue discs, a release can now be a **file set**: a data track (the file the DAT matches), plus its `.cue` and the other tracks the cue lists. It is for the Admin who ingests disc images and everyone who browses the library.

## Out of scope
- Grouping unmatched `.cue`/`.bin` files into a set in Review. They show in Review but can only be deleted (B15).
- Archives (`.zip`/`.7z`) for these systems. They stay in ingest (B9).
- Compressed formats such as `.chd`, `.cso`, `.rvz`, `.wbfs` and `.gdi`. Also `.m3u` playlists for multi-disc games.
- Full redump.org DATs, which hash every track and cue. libretro's Redump DATs list only the data track (B3).
- Converting `.nrg` to bin/cue. A Saturn `.nrg` never matches Redump, so it always goes to Review.
- Other disc systems (PS2, PS3, Xbox, Sega CD, TurboGrafx-CD, Dreamcast).

## Behaviour

### DATs
- **B1 DAT folder.** A new handler property, `DatFolder` (default `no-intro`), names the folder of the main DAT under `Sources:DatBaseUrl`. The six new handlers use `redump`, so the URL is `{DatBaseUrl}/redump/<DatName>.dat` and the cache path is `cache/dats/redump/<DatName>.dat`. The manifest, the T3 versions result and **Admin → DATs** treat a `redump/` file the same way they treat a `no-intro/` file today. Existing handlers are unchanged.
- **B2 Optional metadat.** A new handler property, `MetadatKinds` (default `DatKind.Metadat`, a new list of the four metadat kinds in `DatKind.All` order), lists which metadat files exist. DAT refresh downloads only those files, and `DatCache` requires only those. A kind the handler doesn't list gives empty metadata (null year, empty developers, publishers and genres). All six new handlers list **no** kinds.
  Facts (libretro-database master, checked 2026-11-12):
  - PSP has `releaseyear`, `publisher` and `genre`, but those files are keyed by `serial`/`image`, with no `crc`. `DatStore` joins metadat on CRC32, so they can't be used without a parser change. That change is out of scope.
  - The other five systems have no metadat.

  A 404 on a file the handler does list still fails the refresh, as today.
- **B3 Redump facts.** libretro's `metadat/redump/*.dat` files (version 2026.10.07, checked 2026-11-12) have **one rom per game**:
  - PSX (13,407 games) and Saturn (2,622): the data track `.bin` only. There is never a `.cue` or a second track. The listed track is named `… (Track 1).bin` on discs with fewer than 10 tracks and `… (Track 01).bin` otherwise; a single-track disc has no `(Track …)` tag. In Saturn, 161 entries list `(Track 2)` or `(Track 02)` instead (discs whose first track isn't the data track). Nothing here depends on the track number: the data track is whichever member's SHA1 matches (B7), and a lone `.bin` matching any `(Track <digits>)` rom waits (B7, B11).
  - PSP (4,110), GameCube (2,237) and Wii (3,817): one `.iso`.
  - Xbox 360 (3,959): 3,950 `.iso` games and 9 `.bin` system/installer discs. `x360` lists only `.iso`, so `DatStore` filters the 9 `.bin` entries out (N3), as today.
  - The DATs repeat a game once per serial with the same name and rom (for example 2,455 repeated names in PSX). `DatStore`'s SHA1 index keeps the first entry, which is the same rom, so matching is unaffected.

  Largest entries: Xbox 360 8,738,854,912 bytes, Wii 8,511,160,320, PSP 7,815,495,680 (a promo disc; two PSP entries are over 2 GiB), GameCube 1,459,978,240, PSX 817,101,264, Saturn 665,712,432. The libretro-thumbnails folders are named exactly `<DatName>` (spaces kept), which is what `ThumbnailKey.Url` already uses.
- **B3a `UK` and `United Arab Emirates` regions.** Redump names use `UK` (180 rom names across all six DATs, alone or in a list such as `(UK, Australia)`, checked 2026-11-12), which `NoIntroRegions` doesn't know (it lists `United Kingdom`), so `NoIntroName.Parse` would make `UK` a flag and give no region. Five Redump game names use `(United Arab Emirates)`, also unknown. `NoIntroRegions` gains both, so `Foo (UK)` parses as region `UK` and `Foo (United Arab Emirates)` as region `United Arab Emirates`, each with no flag. Every other region group in the six DATs is already known. It applies to every system; the No-Intro DATs don't use either, so existing systems don't change.

### Systems
- **B4 Contract values.**

  | Id | DisplayName | ShortName | Family | RomExtensions | Min / Max bytes | DatName | MetadatKinds | ReadHeader |
  |---|---|---|---|---|---|---|---|---|
  | `psx` | PlayStation | PSX | `psx` | `.bin`, `.cue`, `.iso` | 16 / 1 GiB | `Sony - PlayStation` | none | null |
  | `psp` | PlayStation Portable | PSP | `psp` | `.iso` | 16 / 8 GiB | `Sony - PlayStation Portable` | none | null |
  | `gc` | GameCube | GC | `nintendo-disc` | `.iso` | 32 KiB / 9 GiB | `Nintendo - GameCube` | none | B6 |
  | `wii` | Wii | Wii | `nintendo-disc` | `.iso` | 32 KiB / 9 GiB | `Nintendo - Wii` | none | B6 |
  | `x360` | Xbox 360 | X360 | `x360` | `.iso` | 2 KiB / 9 GiB | `Microsoft - Xbox 360` | none | null |
  | `saturn` | Saturn | Saturn | `saturn` | `.bin`, `.cue`, `.nrg` | 16 / 1 GiB | `Sega - Saturn` | none | null |

  A PSX `.iso` never matches, because the PSX DAT lists only `.bin` data tracks (B3). Like a Saturn `.nrg`, it always goes to Review, where it can be resolved (B15).
  GameCube and Wii have the same size limits on purpose. Ingest checks size against the first handler in the family that lists the extension (`IngestPipeline.ProcessCoreAsync`), so different limits would make a large Wii disc fail as `TooLarge` against the GameCube limits.
  All six handlers set `EmbedRomsInZip` to false, `DatSource` to `Libretro`, `DatFolder` to `redump` and `FileSets` to true (B5). Each has its own 512×512 default box art, with its SVG source.
  Projects:
  - `RnRomManager.Systems.Sony` holds `psx` and `psp`.
  - `RnRomManager.Systems.NintendoDisc` holds `gc` and `wii`.
  - `RnRomManager.Systems.Microsoft` holds `x360`.
  - `saturn` goes in the existing `RnRomManager.Systems.Sega`.

  Each new project has a matching `tests/…Tests` project.
- **B5 `FileSets` flag.** A new handler property, `bool FileSets` (default false). When it is true:
  - external files keep exact names (B10);
  - ingest groups bin/cue sets (B7);
  - Review restrictions apply (B15).

  The registry refuses a handler with `FileSets` true and `EmbedRomsInZip` true at startup, naming it. Handlers in one family must agree on `FileSets`, as they already must on `EmbedRomsInZip`.
- **B6 GameCube/Wii header.** `NintendoDiscHeader.Read(rom, callerId)` is shared by both handlers and reads the first MiB:
  - The GameCube magic is `C2 33 9F 3D` at 0x1C. The Wii magic is `5D 1C 9E A3` at 0x18.
  - `ClassifiedSystemId` is `gc` or `wii`, from the magic that is present. `IsPlausible` is true when either magic is present. With neither, the result is `IsPlausible` false, classified as the caller.
  - `Title` is the bytes from 0x20 up to the first NUL (at most 0x3E0 bytes), decoded as Latin-1 and trimmed.
  - `Fields`:
    - `gameId`: the 6 ASCII bytes at 0x00, or null if any byte is not printable;
    - `discNumber`: the byte at 0x06;
    - `version`: the byte at 0x07;
    - `title`.
  - A span too short for a field gives null for that field. It never throws.

  Because both handlers share the `nintendo-disc` family, a Wii disc dropped in `ingest/gc/` is stored under `wii` (§7.2 step 4). An unmatched `.iso` with no magic fails as `InvalidHeader`.

### Ingest
- **B7 Sets.** For a `FileSets` system whose `RomExtensions` include `.cue` (`psx`, `saturn`), before files are processed one by one, the ingest job plans sets from **all** loose files in `ingest/<id>/`, even for a selected-files run (IN1). On `psp`, `gc`, `wii` and `x360` nothing is planned: a `.cue` or `.bin` there fails as `UnsupportedType`, as today.
  - **Selected-files runs:** a set is processed when any of its members is selected, and then all of its members are processed. Unselected files that belong to no selected set are left alone.
  - **Parsing:** a file is a cue when its extension is `.cue`, ignoring case (the same for `.bin`, `.iso` and `.nrg` below). `IngestJob` reads every `.cue` of at most 64 KiB through the same checked open as other ingest reads and passes its bytes to the planner, which parses them with `CueSheet.Parse` (B8). Its set is the cue plus every file its `FILE` lines name. Names resolve in the same folder, ignoring case; if two files in the folder differ only by case and a cue names one of them, that set is `SetConflict`.
  - **Incomplete set:** if a named file is missing, nothing in that set is processed. Every present member is `Skipped` with reason `IncompleteSet` and stays in ingest, so it is retried on the next run. A lone `.cue` whose files aren't there is the same case.
  - **Broken cue:** a `.cue` that can't be read or doesn't parse has no member list. It and every `.bin` whose name starts with the cue's stem (ignoring case; Redump names tracks `<stem> (Track N).bin`) are `Skipped` `IncompleteSet` and stay in ingest. A `.bin` that a parsed cue names belongs to that cue's set instead, even when it starts with a broken cue's stem (`Game 2 (Track 1).bin` and a broken `Game.cue`). Other files are planned as usual.
  - **Unsupported member:** a `FILE` name that doesn't end in `.bin` (ignoring case: `.wav`, `.ape`, `.img`, `.iso`, `.cue`, …), the cue naming itself, or a cue whose own file name fails B8's name rules (it is stored under that exact name, and the validator would refuse it, B12), skips the whole set. Every present member is `Skipped` with reason `UnsupportedSetMember` and stays in ingest.
  - **Shared member:** if one file is named by two or more cues, all of those sets are `Skipped` with reason `SetConflict`.
  - **Large cue:** a `.cue` over 64 KiB isn't parsed. It is handled as a lone file that is not a cue.
  - **Lone files:** a `.bin`, `.iso` or `.nrg` that no cue names, or a `.cue` over 64 KiB, is processed on its own as a single file (B11). A lone file that doesn't match goes to Review under B15. A lone `.bin` that matches a DAT rom whose name has a ` (Track <digits>)` tag (a data track of a multi-track disc, B3) is `Skipped` with reason `IncompleteSet` and stays, so its cue and other tracks can join it later; this check runs right after the DAT match, before grouping. A lone `.bin` that matches an untagged (single-track) DAT rom but whose base title two or more games own is `Skipped` `AmbiguousGroup` and stays, as a set's data track does (B7a), because a `.bin` item can't be resolved in Review (B15).
  - **Other files:** a file that no cue names and that isn't one of the above is handled as today: another extension fails as `UnsupportedType`, and an archive follows B9.
  - **Progress:** the job's item total (`SetTotal`) is the number of planned files, counted after a selected set pulls in its unselected members. `ItemDone` is called once per member, after its job item is recorded, so the count reaches the total. `SetCurrentItem` names each member as it is hashed.
  - **Order:** each unit (a set, a lone file or a skipped member) runs at the position of its first member in the scanner's name order (ordinal), so `Game (Track 1).bin` brings its set forward before `Game.cue`. Within a set, the cue comes first, then its `FILE` names in order. "An earlier member in this run" (B7a) follows this order.
  - The set is processed as one unit:
    - If any member is `TooRecent`, the whole set is `Skipped` with `TooRecent`.
    - Size limits apply to each file. A file that is too small or too large fails the whole set. That file gets `TooSmall` or `TooLarge` and moves to `_failed/` as today; the other members get `Skipped` with reason `IncompleteSet` and stay. On later runs the cue names a missing file, so they stay `IncompleteSet` until the Admin deletes them or puts a fitting file back.
    - Every member is streamed and hashed once. The set's **data track** is the one member whose SHA1 matches the system's DAT.
    - Only the data track's SHA1 goes through step 3's library and review-row checks (B7a). The other members' SHA1s are looked up only by B14 (when the data track is a duplicate) and by the repeated-SHA1 rule (when nothing matches); a matched set stores them even when another source's `Pending` row has the same SHA1.
    - Placement keeps today's order (files first, then the ZIP, then the mirror): the members are moved next to the ZIP one by one in the set's order (cue first), then the ZIP is written once.
    - A member that changes after it was hashed (today's post-hash re-check) makes the whole set `Skipped` `TooRecent`; nothing moves.
    - A member whose path is over the 1,024-character source path limit (I1) is `Error` `IoError` as today; the other members are `Skipped` `IncompleteSet`. All stay in ingest.
    - An `Error` while storing the set (`IoError`, `VerifyFailed`, `TargetZipUnreadable`, `InfoJsonNewerSchema`, `Unexpected`) undoes every move, and every member gets that `Error` with the same code and details. All stay in ingest. Each member is moved back as a single file is today (`IngestPipeline.RollBack`): to its ingest path, or to a ` (2)`-style sibling when that name was taken meanwhile (`AllocateRollbackPath`, noted in that member's details). A member moved back under another name is no longer named by its cue, so on the next run it is a lone file and its set is `IncompleteSet` or takes the newcomer.
    - `DbWriteFailed` (the files and the ZIP are in place, the mirror failed) gives every member `Error` `DbWriteFailed`. The job stops after recording all of them, as it does for a single file.
  - **Cancellation:** like a single external file (I2), a set that has started is processed to the end with `CancellationToken.None`; cancellation is checked only between sets and lone files.
- **B7a Set outcomes.**
  - **Exactly one match:** one release is stored. Its main file (`roms` row, info.json `fileName`) is the data track. The cue and the other tracks go in the release's `files` list (B12). Identification, base title, regions, metadata and media come from the data track's DAT entry. Every member's job item is `Ingested`.
  - **Multi-disc games:** grouping is unchanged. `NoIntroName.Parse` makes `(Disc 1)`, `(Disc 2)` flags with one base title, so each disc becomes a release (with its own `files`) of one game, and `PrimaryRelease` picks the primary as today.
  - **No match:** each member goes to Review (`NoDatMatch`) as its own item, with B15's restrictions.
  - **Two or more members match:** each member goes to Review with reason `AmbiguousSet`. Its row stores no suggestions (`[]`), and `dat_identification` and `candidate_game_ids` are null, since the item can't be resolved (B15).
  - **Duplicate:** the data track's SHA1 is already in the library (`roms.sha1`). B14 runs first. Then every member not used for a repair is `Duplicate`, and all of them move to `_duplicates/` as today.
  - **Data track already in Review:** the data track's SHA1 has a `Pending` review row from another source. Today a single file in that case moves to `_duplicates/`; here every member is `Duplicate` and moves there.
  - **Data track dismissed:** the data track's SHA1 has a `Dismissed` row. The row moves to the new path, as it does for a loose file today, and the set is processed under "Exactly one match".
  - **`AmbiguousGroup`:** two or more games own the data track's base title. Every member is `Skipped` with reason `AmbiguousGroup` and stays in ingest. The Admin merges or moves the games, and the next run ingests the set. It doesn't go to Review, because Attach there would split the set.
  - **Repeated SHA1 among unmatched members:** `ingest_review_items.sha1` is unique, and silent audio tracks repeat. If a member's SHA1 already has a **`Pending`** row whose source is a different file, or matches an earlier member in this run, that member isn't queued. It stays in ingest, `Skipped` with reason `AlreadyInReview`, and is never moved to `_duplicates/`, because that would break its set. A member's own `Pending` row from an earlier run is re-queued as today (upsert, still `Review`). `Resolved` and `Dismissed` rows don't block a member.
  - **Members' own review rows:** each member's result completes its own `Pending` review row, as `IngestPipeline.ProcessAsync` does for a single file (`CompleteSourceAsync`): `Ingested` resolves it, `Duplicate` and `Failed` delete it, `Review` and `Skipped` keep it, and an `Error` keeps it (I4). So a set queued for Review on one run and matched on a later one (after a DAT refresh) leaves no `Pending` rows.
  - **Audit:** a stored set writes one `Ingested` event, as a single file does today (path: the data track's source, target: the ZIP, details `release <main fileName>`). Each member moved to `_duplicates/` writes its own `MovedToDuplicates` event, and each repaired member its own `ZipRepaired` event (B14), as for single files.
- **B8 Cue parsing.** `Core/Ingest/CueSheet.Parse(ReadOnlySpan<byte> bytes)` reads `FILE "<name>" <type>` lines, and also unquoted `FILE <name> <type>`, ignoring case and leading whitespace. It returns the names in order without duplicates (compared ignoring case; the first spelling is kept).
  - It is an error when there is no `FILE` line, there are more than 99 distinct names (a disc has at most 99 tracks, and each `FILE` holds at least one), or a name is empty, equals `.` or `..`, contains `/`, `\` or `:`, or fails `InfoJsonValidator.PortableNameProblem`.
  - A name that merely contains `..` is valid. Real Redump names do this, for example `...Iru!`.
  - The bytes are decoded as UTF-8 (a leading BOM is dropped), falling back to Latin-1 on invalid bytes.
  - Other cue commands are ignored.
- **B9 Archives.** For a `FileSets` system, a `.zip` or `.7z` in `ingest/<id>/` is `Skipped` with reason `ArchiveNotSupported` and stays in place. Nothing is extracted.
- **B10 Exact names.** For a `FileSets` system, `ExternalRomName` stores a file under its exact name in the ZIP's folder, with no `<ZipBase>.` prefix (E1) and no ` (N)` suffix (E2):
  - A set member keeps its **source file name**, because the cue refers to it byte for byte. Its logical `fileName` equals `externalFileName`.
  - A single file (B11) is named the way today's libretro logic names it: the DAT rom stem plus the source extension. Review resolutions keep their names (B15).
  - **Release tags:** for `FileSets` systems, `IngestPipeline.LibretroRelease` (a private method) parses regions, languages, revision and flags from `DatGameName`, not from the rom stem. Otherwise a data track would get a `Track 1` or `Track 01` flag. Review does the same wherever it takes tags from a DAT entry: the Dat action (B15) and Attach of an `AmbiguousGroup` item with flag `None` (today parsed from the queued `romName`).
  - **Logical names:** for `FileSets` systems, `fileName` always equals `externalFileName`. Ingest and Review don't call `ExternalRomName.AllocateLogicalName` (it would add ` (N)` or cut); a name that another release of the destination game already uses is taken, as below.
  - If a name is already taken in the folder (on disk, by a claim, or by the destination ZIP; checks ignore case), nothing in the item or set is moved. The item, or every member, ends as `Error` with code `NameTaken` and stays in ingest.
  - A name over 250 UTF-8 bytes ends as `Error` `NameTooLong`. It is not cut, because cutting would break the cue.
  - The ZIP name follows E2 as today.
- **B11 Single files.** A matched `.iso`/`.bin` that no cue names becomes a single-file release (no `files`), as Switch files do today but with B10 naming. A lone `.bin` matching a `(Track N)` DAT rom waits in ingest instead (`IncompleteSet`, B7). An unmatched `.iso` or `.nrg` goes to Review, where it can be resolved.
- **B12 Release files.** In info.json schema **v6**, every rom has a `files` array. It is null when the release is one file. Otherwise each entry is `{ fileName, externalFileName, originalFileName, sizeBytes, crc32, md5, sha1 }`: the cue first, then the cue's `FILE` names in order, leaving out the data track (it is the main file).
  - The validator allows a non-null `files` only for `FileSets` systems. Every `files` name follows today's `externalFileName` rules (`PortableNameProblem`, at most `MaxExternalFileNameBytes`, 255 UTF-8 bytes; ingest itself never writes more than 250, B10) and has one of the handler's extensions. Names must be unique across the game (main files and files together, ignoring case). Each file's `sizeBytes` is at most the handler's `MaxRomSizeBytes`, as for a main file (the validator has no minimum today). On a `FileSets` system, `fileName` must equal `externalFileName`, for main files and `files` entries alike (B10). A non-null `files` must have at least one entry (an empty array is rejected; a single file is `null`). Each entry's `crc32`, `md5` and `sha1` must be lowercase hex of the right length, as for `metadata`, and `originalFileName` follows the main rom's rule.
  - For `files` names, and for main `fileName`s on `FileSets` systems, the validator's `..` rule rejects only a name equal to `.` or `..`, or one containing a path separator, as B8 does.
  - The `InfoJsonUpgraderV5` adds `files: null` to every rom.
  - **Size:** `files` makes a large info.json possible (about 40 KB per 99-track release). `LibraryZipWriter` refuses to publish an info.json over `InfoJsonReader.MaxBytes` (1 MiB) with `VerifyFailed`, because the reader would refuse it and leave the game unreadable. Today the writer never checks this. The ingest or edit then rolls back as for any `VerifyFailed`.
  - In the database, migration `0020_rom_files.sql` adds `rom_files`, with every column `NOT NULL` and `utf8mb4_unicode_ci`, like `roms`:
    - `id`: `BIGINT` auto-increment PK;
    - `rom_id`: `BIGINT`, FK → `roms.id` ON DELETE CASCADE;
    - `game_id`: `CHAR(36)`, FK → `games` ON DELETE CASCADE;
    - `ordinal`: `INT`, the entry's 0-based index in `files` (0 is the cue);
    - `file_name`, `external_file_name`, `original_file_name`: `VARCHAR(255)`;
    - `size_bytes`: `BIGINT`;
    - `crc32`: `CHAR(8)`; `md5`: `CHAR(32)`; `sha1`: `CHAR(40)`.

    Indexes: a **non-unique** index on `sha1`, because audio tracks repeat across discs, plus indexes on `rom_id` and `game_id`.
  - Existing rows need no backfill. The mirror (`LibraryMirror.ApplyAsync`, used by ingest, edits and the DbSync rebuild) writes `rom_files` from info.json in the same transaction as `roms`. Today `roms` is inserted in one batch without reading back ids, so each `rom_files` row finds its `rom_id` through `roms.sha1` (unique, `ux_roms_sha1`): `INSERT INTO rom_files (rom_id, game_id, …) SELECT r.id, r.game_id, … FROM roms r WHERE r.sha1 = @RomSha1`. `MirrorEntry.ToRows()` also returns the file rows, each carrying its release's main `sha1`.
  - Backups need no change: `MySqlBackupStore` dumps and restores every base table.
- **B13 Claims.** `IRomRepository.ListExternalAsync` returns the main files and the `rom_files` rows together, ordered by ZIP path, then ROM id, then the main file before its files in `ordinal` order. `ExternalRomRow` gets a nullable `FileId` (null for a main file). Name reservations, the integrity orphan scan and `LibraryFiles.OpenExternal`'s claim check therefore all see every file of a set.
  - **A game's own names:** code that lists a game's external names from its info.json (today `Roms.Select(r => r.ExternalFileName)`) uses a new `InfoJsonGame.ExternalFileNames()`, which returns each main `externalFileName` and then each `files` entry's. It replaces every such list: the pipeline's `AddToGameAsync` and `DuplicateOrRepairAsync` reservations, `LibraryEditor`'s title-edit claim check (the "also claimed by another game" refusal), move-release and merge reservations, and `IntegrityJob`'s `ReadPointers` and `PointerWasChecked` (issue resolution compares the ZIP's names with the mirrored ones, so a set file's issue could never resolve without it).
  - **New repository methods:** `IRomRepository.ListFilesByGameAsync(gameId)` (game detail), `GetFileAsync(gameId, romId, fileId)` (download; null unless the file belongs to that release of that game) and `ListFilesBySha1Async(sha1)` (B14). Each returns `RomFileRow` records with the B12 columns.
- **B14 Track self-repair.** The data track already repairs itself through today's single-file path. Ingesting a set whose data track is a duplicate also checks each other member:
  - **Repaired:** a member whose name matches a file of that release (ignoring case), and whose SHA1 equals that file's recorded SHA1, while the file has an open `EntryMissing`, `EntrySizeMismatch` or `HashMismatch` issue. It is put back with today's `RepairPlan` and step-aside, one file at a time. Its item is `Repaired` and its issue resolves, as single-file repair does now.
  - **Duplicate:** every other member.
  - A lone `.bin` (one no cue names, B7) is repaired the same way when exactly one set file has its name (ignoring case) and SHA1 (`ListFilesBySha1Async`) and an open issue of those types. With none, or two or more, it continues through step 3 as today (another source's `Pending` row makes it `Duplicate`; otherwise it goes to Review). The check runs right after the `roms.sha1` lookup and before the review-row checks, the same place in step 3 as the main-file repair.
  - A lone `.cue` is never repaired on its own: the files it names aren't beside it, so it is `IncompleteSet` (B7). Dropping the whole set repairs a missing cue (its data track is a duplicate, so the cue is checked above).

### Review
- **B15 Review for `FileSets` systems.**
  - **Unmatched `.cue`/`.bin`:** on a `FileSets` system, a review item whose `originalFileName` extension is `.cue` or `.bin` (ignoring case) can't be resolved. Every set member is a `.cue` or `.bin` (B7), so this covers every `AmbiguousSet` item too. `POST /api/review/{id}/resolve` returns `409` `SetFileNotResolvable` (added to the 409 list in `ReviewEndpoints.ToResult`; any code not listed there is a 500), and the Review item page offers only Delete: it hides the action tabs (Suggestions, Matched DAT entry, Attach, Manual) and the Dismiss button, and shows the text "Disc track files must match the Redump DAT to be imported." where the tabs were. The File and Header cards, the Google it link and Delete stay. Delete works as today. `GET /api/review/{id}` gains `resolvable` (bool): false for such an item, true for every other item. The item page uses it to decide; it never works out the rule itself.
  - **Name errors:** a resolve whose exact name is taken or too long (B10) returns `409` with code `NameTaken` or `NameTooLong`; both are added to `ReviewEndpoints.ToResult`'s 409 list. Today the resolver passes the pipeline's code through, so without this they would be 500s. The item stays `Pending`.
  - **Unmatched `.iso`/`.nrg`:** resolves as today. The Dat action names the file `<DatGameName> (Unverified)<source ext>`, not the first DAT rom's stem, because a Saturn `.nrg` maps to a game whose DAT rom is `… (Track 1).bin`. It takes metadata from the game's DAT rom. Its base title and tags (regions, languages, revision and flags) come from `DatGameName`, as in B10 and as ingest groups it, not from `datRom.RomName`.
  - **`..` in names:** for a `FileSets` system, `ReviewResolver`'s check that refuses a generated file name containing `..` applies B12's rule instead (only a name equal to `.` or `..`, or one with a path separator, is refused), so `...Iru! (Japan) (Unverified).nrg` resolves.
  - **Dismiss:** `POST /api/review/{id}/dismiss` on a `.cue`/`.bin` item also returns `409` `SetFileNotResolvable`, so a set's members can't end up half dismissed.
  - **DAT search:** `GET /api/dat/search` (in `ReviewEndpoints`) returns each `gameName` once (its first entry), removing repeats before `limit` is applied, because the Redump DATs repeat a game per serial (B3). This applies to every system: a game with several ROMs also shows once, which matches the Dat action, since it already uses the game's first ROM (`ReviewResolver`).
  - **Reason filter:** `AmbiguousSet` is accepted by `GET /api/review`'s `reason` filter (today only `NoDatMatch` or `AmbiguousGroup`, else 400), added to the SPA's `ReviewReason` type, and offered in the Review queue's reason filter as "Ambiguous set".

### Library edits
- **B16 Title edit.** For a `FileSets` system, renaming the ZIP never renames the external files, because their names don't contain the ZIP base. The ZIP rename and the folder change are unchanged. If the new folder (letter bucket) differs, every file of every release moves with its exact name, and a taken name refuses the edit with `409` `NameTaken` before any file moves. A set file another game also claims in the old folder refuses the edit as a main file does today (`TargetZipUnreadable`, B13). A missing file is logged and keeps its name, as today. `NameTaken` is added to the 409 list in `LibraryEndpoints.ToProblem` (any code not listed there is a 500).
- **B17 Delete.** Deleting a release or a game moves every file of each release (main and `files`) to `ingest/<id>/_removed/`, using today's side-path allocation (`AllocateSidePath`) for each file. A missing file is logged and skipped, as a missing main file is today. A failed move undoes the moves already made (`UndoMoves`), as today. The audit events keep today's shape:
  - deleting a release writes one `ReleaseDeleted` event: `target_path` is the main file's `_removed/` path and `details` is the main `fileName`, as today, followed by `; ` and each `files` name in order (truncated to the column, as all audit text is);
  - deleting a game writes one `GameDeleted` event, unchanged (`target_path` `ingest/<id>/_removed/`).
- **B18 Move / merge.** On a `FileSets` system, move-release and merge move every file of each moved release with its exact name: the main file and each `files` entry, so a single-file `.iso` release (no `files`) follows the same rules, and "set files" below means all of them. A taken name in the target folder refuses the whole operation with `409` `NameTaken` before any file moves.
  - **Each file as a main file today:** a missing set file is logged and only its entry moves; a file that is neither found nor missing refuses with `IoError`; a failed move undoes the moves already made (`UndoMoves`) and returns `IoError`; a failed ZIP write undoes every move.
  - **Same folder:** when the source and target ZIPs are in the same folder, the set files don't move, and the source game's own claims don't count as taken.
  - **Logical names:** a logical-name clash in the target game (`AllocateLogicalName`) also refuses with `NameTaken`. It never adds ` (N)`, so `fileName` always equals `externalFileName`.
  - **Move record:** `ExternalMoveRecord` gets a format **v2**: every v1 field, unchanged (`romSha1` and the two single names still describe the main file), plus `files`, a list of `{ sourceExternalFileName, targetExternalFileName }` pairs for the release's `files` entries in order. Like v1 it is written only by move-release (merge writes no record, as today) and validated for **manual recovery** only (W3/W7); there's no automatic replay. A release with no `files` still writes v1.
    - Its size cap rises from 16 KiB to 320 KiB. That fits 99 pairs (B8) of 250-byte names even when the default JSON encoder writes every byte as a 6-byte `\uXXXX` escape (99 × 2 × 1,500 bytes, plus keys).
    - A v1 record (one pair) is still read and validated as today.
    - Validation (`ExternalMoveRecord.ValidateAsync`) accepts version 1 or 2. A v1 record has no `files`; a v2 record has a non-empty `files` list. Every `files` name passes the same plain-pointer check as the two single names (`Problem`). The backed-up release (the one with `romSha1`) must have a `files` list of the same length whose `externalFileName`s equal the recorded `sourceExternalFileName`s in order, ignoring case, as v1 compares the main pointer. Anything else is `InvalidDataException`, as today.
  - **Notes** stay keyed by the main file's `fileName`.

### Integrity
- **B19 Integrity.** Each `rom_files` row is checked like an external main file: missing gives `EntryMissing`, a wrong length gives `EntrySizeMismatch`, and a deep run checks `HashMismatch`. Findings are keyed by `(zipPath, externalFileName)`.
  - Set files are in `ListExternalAsync` (B13), so they are never `OrphanFile`, and a name claimed by two games gives `EntryMissing` "also claimed by game …", as for a main file. The location check (`CheckLocation`) looks only at the ZIP name, so it needs no change.
  - Issue details are as for a main file today; the file's own exact name is the issue's entry.

### API and UI
- **B20 Game detail.** Each release in `GET /api/games/{id}` gains:
  - `files`: null, or a list of `{ id, fileName, sizeBytes, crc32, md5, sha1, downloadUrl }`;
  - `totalSizeBytes`: the main size plus the files.

  `GET /api/games/{id}/roms/{romId}/files/{fileId}/download` streams one set file, named by its `fileName`. It works like today's external ROM download (`LibraryFiles.OpenExternal`, file found by `GetFileAsync`, B13): range support, the same auth, `404` when the file belongs to another release, and `404` when another game claims the name in that folder, as today.
  - On the Game page each release is a card (`<article>`) with a collapsible details body. In that body, after the CRC32/SHA1 row, a release with files shows a "N files" toggle button (`aria-expanded`, `aria-controls`), where N counts every file of the release (the main file plus `files`, so 3 for a cue and two tracks). It opens a table listing the main file first and then each `files` entry, each with its file name, `FileSize`, SHA1 (copy button) and a download icon button with an `aria-label`. The table follows the T1–T4 table rules.
  - The details body's Size field shows `totalSizeBytes`. The card header's download button and the page's **Download ROM** button/dropdown still download the main file and show its own size, as today.
  - `GameAdminModal`'s delete-release text for a release with files reads "The ROM and its N other files move to the system's `_removed` ingest folder.", N being the `files` count (B17); without files it is unchanged. The delete-game text is unchanged.
- **B21 Sizes.** In the library list, `PrimarySizeBytes` includes the primary release's files. The Library Size report sums `rom_files.size_bytes` along with the external main files. `romCount` stays a count of releases.

### UI and colours
- **B22 Badges and icons.**
  - `app.css` (a `--bs-<id>` variable and a `.text-bg-<id>` rule, the same in both themes, like `tg16`) and `systemColours.ts` get these colours. Every pair meets AA:

    | Id | Background | Foreground | Contrast |
    |---|---|---|---|
    | `psx` | `#5c6670` | `#fff` | 5.9:1 |
    | `psp` | `#8a1c7c` | `#fff` | 8.3:1 |
    | `gc` | `#b8860b` | `#000` | 6.5:1 |
    | `wii` | `#7fb3d5` | `#000` | 9.3:1 |
    | `x360` | `#5dc21e` | `#000` | 9.2:1 |
    | `saturn` | `#34495e` | `#fff` | 9.3:1 |

    `systemColours.test.ts` only checks the mapping; contrast is checked by the e2e axe scans, which see every seeded system's badge.
  - Icons in `icons.ts`:
    - `system-psx` and `system-psp`: `bi bi-playstation`;
    - `system-x360`: `bi bi-xbox`;
    - `system-gc`, `system-wii` and `system-saturn`: `bi bi-disc`.

### Job items
- **B23 Reasons and codes.**
  - New skip reasons: `IncompleteSet`, `UnsupportedSetMember`, `SetConflict`, `AmbiguousGroup`, `AlreadyInReview`, `ArchiveNotSupported`. They are recorded on job items, as `TooRecent` is. The Ingestion page doesn't change.
  - New review reason: `AmbiguousSet`, also a value of the Review reason filter (B15).
  - New error codes: `NameTaken`, `NameTooLong`.
  - New ProblemDetails codes (409): `SetFileNotResolvable`, `NameTaken` and `NameTooLong` (`ReviewEndpoints.ToResult`); `NameTaken` (`LibraryEndpoints.ToProblem`). `Problems.cs` only builds the response; it doesn't change.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `Core/Systems/ISystemHandler.cs`, `SystemRegistry` | `DatFolder`, `MetadatKinds` and `FileSets` with defaults; registry checks (B5) |
| Core | `Core/Dats/DatKind.cs`, `DatRefreshJob.cs`, `DatCache.cs`, `DatManifest` | B1, B2: folder and kinds per handler; versions keyed for `redump/` too |
| Api | `Endpoints/DatEndpoints.cs` | Admin → DATs lists `redump/` files (B1) |
| Core | `Core/Naming/NoIntroRegions.cs` | `UK` and `United Arab Emirates` regions (B3a) |
| Core | `Core/Storage/ExternalRomName.cs` | Exact-name allocation for `FileSets` (B10) |
| Core | `Core/Library/InfoJsonRom.cs`, `InfoJsonRomFile.cs` (new), `InfoJsonReader.cs` (v6), `InfoJsonUpgraderV5.cs` (new), `InfoJsonValidator.cs`, `LibraryRows.cs`, `ILibraryRepositories.cs`, `LibraryZipWriter.cs` (verify set files by length; refuse an info.json over 1 MiB) | B12, B13 |
| Core | `Core/Ingest/CueSheet.cs` (new), `IngestSetPlanner.cs` (new), `IngestJob.cs`, `IngestPipeline.cs`, `IngestOutcome.cs` (reasons) | B7–B11, B14, B23 |
| Core | `Core/Ingest/ReviewResolver.cs`, `Core/Dats/DatStore.cs` (comment: DATs may repeat a SHA1) | B3, B15 |
| Core | `Core/Library/LibraryEditor.cs`, `LibraryEditResult.cs` (`NameTaken`), `ExternalMoveRecord.cs` | B16–B18 |
| Core | `Core/Integrity/IntegrityJob.cs` | B19 |
| Core | `Core/Library/InfoJsonGame.cs` (`ExternalFileNames()`), `IngestPipeline.cs`, `LibraryEditor.cs`, `IntegrityJob.cs` (call sites) | B13 |
| Data | `Migrations/0020_rom_files.sql`, `Repositories/LibraryRepositories.cs` (`RomFileRow`, file methods), `ReportRepository.cs` | Table, mirror, claims, sizes (B12, B13, B21). Existing data: no backfill; every existing rom gets `files: null` on its next read (upgrader) |
| Api | `AppHost.cs` (upgrader V5, 6 handlers), `RnRomManager.Api.csproj`, `Endpoints/LibraryEndpoints.cs` (`ToProblem`), `LibraryFiles.cs`, `ReviewEndpoints.cs` (`ToResult`, DAT search, `resolvable`) | B15, B20, B23. API contract: additive (`files`, `totalSizeBytes`, `resolvable`, the file download route) |
| Systems | `RnRomManager.Systems.Sony`, `.NintendoDisc`, `.Microsoft` (new), `.Sega` (Saturn), their test projects, `RnRomManager.slnx`, `Dockerfile` restore layer | B4, B6 |
| Web | `lib/api/types.ts`, `routes/games/[id]/+page.svelte`, `lib/components/GameAdminModal.svelte` (delete text), `lib/systemColours.ts`, `app.css`, `lib/components/icons.ts`, `routes/review/[id]/+page.svelte`, `routes/review/+page.svelte` (reason filter) | B15, B20, B22 |
| TestData | `FixtureDatWriter.cs` (DatFolder/MetadatKinds), `FixtureGame.cs`, `IngestSeeder.cs` (the six DatName constants and `EmptyRedump`), new `ExternalSetTestHandler` (`FileSets` true, `.bin`/`.cue`/`.iso`), synthetic disc builders | Tests and seed |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md` (v6, `files`, exact names), `docs/adding-a-system.md` (new members, removed "one file per release" limit, the first-match size check), `docs/deployment.md`, `docs/development.md` | Doc rule |

## Risks
- **Large change to the release model.** Most of the code assumes one file per release. `files` is additive, null for every existing release and only allowed for `FileSets` systems, so existing systems take the old paths unchanged. Each step keeps the existing tests green.
- **Schema v6.** Older app versions refuse v6 ZIPs (`InfoJsonNewerSchema`), as they do for every schema bump. Downgrading after ingesting a disc game needs a restore.
- **Shared track hashes.** Audio tracks repeat across discs, so `rom_files.sha1` isn't unique. Only the data track (`roms.sha1`) decides duplicates.
- **Only the data track is verified.** The cue and audio tracks have no DAT hash to check against. They're trusted at ingest and protected afterwards by their recorded hashes (integrity, B14).
- **Crash mid-set.** A process crash between member moves leaves the moved members beside the ZIP, where integrity reports them as `OrphanFile`, and the rest in ingest (as for a single file today, W6). The Admin moves the orphans back to `ingest/<id>/` and re-runs ingest.
- **Exact names can collide.** Two games in one letter folder with the same cue track name (for example a generic `Track 02.bin`) give `NameTaken` for the second. The file stays in ingest, and nothing is lost.
- **Very large files.** Multi-GB ISOs stream through the existing external hash and copy path, with step progress. Copies across volumes take as long as the disk needs.
- **New DATs.** After the upgrade, ingestion refuses to start (`DatsMissing`) until **Refresh DATs** has run, as with every new system. This goes in the deployment upgrade notes.

## Steps
- [x] **1. Per-handler DAT folder, metadat kinds and two regions**: B1, B2, B3a. Add `UK` and `United Arab Emirates` to `NoIntroRegions`. Add `DatFolder` and `MetadatKinds` to `ISystemHandler` (defaults keep today's behaviour). Update `DatRefreshJob` (URLs, the `versions` prefix filter, and `Parse`, which today treats only `kind == DatKind.NoIntro` as the main DAT and must treat the handler's `DatFolder` file as the main DAT), `DatCache`, the manifest lookup, the Admin DATs listing's prefix filter (`DatEndpoints`) and `FixtureDatWriter`. `FixtureDatWriter.Write` takes an optional layout per DatName (`DatFolder`, `MetadatKinds`), defaulting to `no-intro` and all four kinds, and writes only those files.
  - NSubstitute substitutes don't run default interface members (checked with NSubstitute 6.2.0: a substitute returns `""`, an empty list and `false`), so a substituted handler would get `DatFolder` `""` and no metadat. Every test that builds a substitute `ISystemHandler` and loads or refreshes DATs sets `DatFolder.Returns(DatKind.NoIntro)` and `MetadatKinds.Returns(DatKind.Metadat)` in its handler factory: `DatCacheTests`, `DatRefreshJobTests`, `TitleDbCacheTests` (its `gb` handler), `IngestJobTests`, `LibraryEditorCollectionTests`, `LibraryImageEditTests` and `MediaServiceTests`. Production code reads the members directly; it never treats `""` as `no-intro`.
  - Files: `ISystemHandler.cs`, `DatKind.cs`, `DatRefreshJob.cs`, `DatCache.cs`, `DatEndpoints.cs`, `FixtureDatWriter.cs`, `Core/Naming/NoIntroRegions.cs`, the matching tests, `NoIntroNameTests.cs`, and the seven test files above.
  - Tests:
    - `NoIntroNameTests`: `Foo (UK)` gives region `UK`, no flags and base title `Foo`; `Foo (UK, Australia)` gives both regions; `Foo (United Arab Emirates)` gives that region and no flags;
    - `DatRefreshJobTests`: a handler with `DatFolder = "redump"` and `MetadatKinds = []` fetches exactly one URL, `/redump/<DatName>.dat`, and the result lists its version; a redump DAT whose header names another system fails the refresh; a listed kind that returns 404 still fails the refresh.
    - `DatCacheTests`: that system loads without metadat files, and returns empty metadata.
    - `DatEndpointsTests`: the redump row is listed.
  - Done when: the classes above pass, and existing DAT and `NoIntroNameTests` cases pass unchanged.
- [x] **2. `FileSets` flag and exact external names**: B5, B10 (allocation only). Add `FileSets` to the contract and registry checks. Add exact-name allocation to `ExternalRomName`, returning taken or too-long rather than suffixing or cutting. Add the `ExternalSetTestHandler` to TestData: Id and family `extset`, `DatName` `Test - External Set`, `RomExtensions` `.bin`, `.cue`, `.iso`, `.nrg` (the `.nrg` tests in steps 9 and 12 need it), sizes and box art as `ExternalTestHandler`, no header, `FileSets` true, and `DatFolder` `redump` with no `MetadatKinds`, like the six real handlers. Like `ExternalTestHandler`, it is registered only by the test hosts that use it (an Api host through `ApiFactory.TestServices`, as `ExternalLibraryTests` does; `IntegrityJobTests` appends it to its handler array), each writing its fixture DAT, so no global system count changes.
  - Files: `Core/Systems/ISystemHandler.cs`, `Core/Systems/SystemRegistry.cs`, `Core/Storage/ExternalRomName.cs`, `tests/RnRomManager.TestData/ExternalSetTestHandler.cs` (new, with its box art as `ExternalTestHandler` has), `SystemRegistryTests.cs`, `ExternalRomNameTests.cs`.
  - Tests:
    - `SystemRegistryTests`: `FileSets` with embedded storage is refused; a family with mixed `FileSets` values is refused.
    - `ExternalRomNameTests`: an exact name comes back unchanged; a taken name (ignoring case) is reported; 251 bytes is reported too long.
  - Done when: the classes above pass.
- [x] **3. Release files in info.json**: B12 (info.json part). Add `InfoJsonRomFile`, `files` and schema v6, `InfoJsonUpgraderV5` (registered in `AppHost`), the validator rules, and `LibraryZipWriter`'s length check for set files (its `ExternalFiles` lookup, which today throws `ArgumentException` for a touched name that isn't a main `externalFileName`, also finds `files` entries and checks against their `sizeBytes`).
  - Files: `Core/Library/InfoJsonRom.cs`, `InfoJsonRomFile.cs` (new), `InfoJsonReader.cs` (`CurrentVersion` 5 → 6), `InfoJsonUpgraderV5.cs` (new), `InfoJsonValidator.cs`, `LibraryZipWriter.cs`, `Api/AppHost.cs` (upgrader registration), `InfoJsonTests.cs`, `LibraryZipTests.cs`, `LibraryZipCheckedWriteTests.cs`.
  - Tests:
    - `InfoJsonTests` (it also holds the validator cases): its serialized `"schemaVersion": 5` assertion becomes 6; a v6 round-trip with files; `files` on a non-`FileSets` system, an empty `files` array, an uppercase `sha1` in a `files` entry, a duplicate name across the game, a bad extension, an oversize file, a `files` name over 255 UTF-8 bytes and a `fileName` that differs from its `externalFileName` on a `FileSets` system are each rejected; a `...Iru!` name is accepted on a `FileSets` system.
    - `InfoJsonTests` also holds the upgrader cases, as for V2–V4: `V5Upgrade_` v5 → v6 adds `files: null`. Add `InfoJsonUpgraderV5` to the upgrader lists built in `InfoJsonTests` and `LibraryZipTests`.
    - `LibraryZipCheckedWriteTests` (the checked write's external-file tests live there): a write touching a set file with the right length succeeds, and one whose set file has the wrong length fails verification. `LibraryZipTests`: a write whose info.json would be over 1 MiB fails with `VerifyFailed` and leaves the old ZIP.
  - Done when: the classes above pass.
- [x] **4. `rom_files` table, mirror and claims**: B12 (database part), B13. Add migration `0020_rom_files.sql`, the mirror insert/delete in `LibraryMirror.ApplyAsync`, `ListExternalAsync` (union, `FileId`), `ExternalRomRow.FileId`, `RomFileRow` and the three `IRomRepository` file methods, and `InfoJsonGame.ExternalFileNames()` with every call site B13 lists switched to it.
  - Files: `Data/Migrations/0020_rom_files.sql` (new), `Data/Repositories/LibraryRepositories.cs` (`LibraryMirror`, `RomRepository`), `Core/Library/LibraryRows.cs` (`MirrorEntry.ToRows`, `RomFileRow`), `Core/Library/ILibraryRepositories.cs`, `Core/Library/InfoJsonGame.cs`, the call sites in `IngestPipeline.cs`, `LibraryEditor.cs` and `IntegrityJob.cs`, `LibraryMirrorTests.cs`, `SchemaMigrationTests.cs`, `InfoJsonTests.cs`.
  - Tests (`LibraryMirrorTests`, Data integration): rows are written and cascade-deleted; `ListExternalAsync` includes the files; two releases sharing a track SHA1 are allowed; `SyncAllAsync` restores `rom_files` from the ZIPs; `ListFilesByGameAsync`, `GetFileAsync` (null for another release's file) and `ListFilesBySha1Async` return the rows. `InfoJsonTests`: `ExternalFileNames()` lists main names, then `files` names.
  - `SchemaMigrationTests`: `rom_files` added to its table list, `0020_rom_files` to the journalled scripts, the re-run count 19 → 20, and a column `[TestCase]` for `rom_files`.
  - Done when: `LibraryMirrorTests` and `SchemaMigrationTests` (Docker running) and `InfoJsonTests` pass, and the existing `IntegrityJobTests`, `ExternalLibraryTests` and `IngestPipelineTests` still pass.
- [x] **5. Cue parser**: B8. Add `Core/Ingest/CueSheet.cs`.
  - Files: `Core/Ingest/CueSheet.cs` (new), `tests/RnRomManager.Core.Tests/Ingest/CueSheetTests.cs` (new).
  - Tests: `CueSheetTests` covers quoted and unquoted names, mixed case, BOM, Latin-1 fallback, duplicates collapsed (also ones differing only by case), an error on no `FILE` line, an error on 100 distinct names (99 parse), an error on a name of `.` or `..` or with `/`, `\` or `:`, `...Iru! (Japan).bin` accepted, and other commands ignored.
  - Done when: `CueSheetTests` passes.
- [x] **6. Single-file ingest for `FileSets` systems and archive skip**: B9, B10, B11, B23 (`ArchiveNotSupported`, `NameTaken`, `NameTooLong`). A matched `.iso` is stored under its exact name with no `files`; an unmatched one goes to Review.
  - Files: `Core/Ingest/IngestPipeline.cs` (`IngestErrors`, `LibretroRelease`, external naming), `Core/Ingest/IngestOutcome.cs` (`IngestReason`), `Core/Ingest/IngestJob.cs` (archive skip), `ExternalLibraryTests.cs` (host), `ExternalLibraryTests.Sets.cs` (new).
  - Tests (`ExternalLibraryTests.Sets.cs`, a new partial of the Api integration class `ExternalLibraryTests`, whose host in `ExternalLibraryTests.cs` now also registers `ExternalSetTestHandler` and writes its fixture DAT; `IngestPipelineTests`' host registers no external handler):
    - a matched `.iso` is stored beside the ZIP with no prefix;
    - a matched data `.bin` named `… (Track 1).bin` gets no `Track 1` flag;
    - a second game with the same file name in the same folder gives `NameTaken`, and the file stays;
    - a `.zip` is `Skipped` with `ArchiveNotSupported`;
    - an unmatched `.iso` goes to Review;
    - a matched `.iso` whose name an existing release of the same game uses gives `NameTaken`, with no ` (2)`.
  - Done when: `ExternalLibraryTests` passes (Docker running).
- [x] **7. Set planning**: B7 (planning only), B23 (`IncompleteSet`, `UnsupportedSetMember`, `SetConflict`). Add `Core/Ingest/IngestSetPlanner.cs`: a pure function from one folder's loose files (name, size, and for a `.cue` of at most 64 KiB its bytes, or null when it couldn't be read) and the selection to an ordered list of sets, lone files and skipped members with their reasons. It parses cues through `CueSheet`. Not yet called by `IngestJob`.
  - Files: `Core/Ingest/IngestSetPlanner.cs` (new), `Core/Ingest/IngestOutcome.cs` (`IngestReason`), `tests/RnRomManager.Core.Tests/Ingest/IngestSetPlannerTests.cs` (new).
  - Tests (`IngestSetPlannerTests`):
    - cue + two `.bin`s gives one set in cue order, cue first;
    - a missing member gives `IncompleteSet` for every present member, and a lone `.cue` whose files are missing is the same;
    - a cue naming a `.wav`, an `.iso`, or itself, gives `UnsupportedSetMember`, and so does a cue whose own name contains `:`;
    - a cue that doesn't parse, or couldn't be read, gives `IncompleteSet` for itself and each `.bin` starting with its stem, and another `.bin` is a lone file; a `.bin` that starts with a broken cue's stem but is named by a parsed cue stays in that cue's set;
    - units come in the scanner's name order, by their first member (`Game (Track 1).bin` before `Game.cue`);
    - two cues naming one `.bin` give `SetConflict` for both sets, and so do two files differing only by case;
    - a `.cue` over 64 KiB is a lone file;
    - names resolve ignoring case;
    - a selection holding only the `.bin` selects the whole set; an unselected lone file is left out.
  - Done when: `IngestSetPlannerTests` passes.
- [ ] **8. Set ingest: store, Review and rollback**: B7 (processing a planned set), B7a (one match, no match, `AmbiguousSet`, audit), B23 (`AmbiguousSet`). Call the planner from `IngestJob` in its unit order, hash every member once, pick the data track, and place every file with rollback of all of them if any move or the ZIP write fails. Job items are per file.
  - Files: `Core/Ingest/IngestJob.cs`, `Core/Ingest/IngestPipeline.cs`, `Core/Ingest/IngestOutcome.cs`, `ExternalLibraryTests.Sets.cs`.
  - Tests (`ExternalLibraryTests.Sets.cs`):
    - cue + data + audio track gives one release with 2 `files` in cue order, every item `Ingested`, and one `Ingested` audit event;
    - no match gives three Review items;
    - two matches give `AmbiguousSet`;
    - a failure on the third move rolls back the first two (file system hook), and ingest is left unchanged;
    - a ZIP write that fails with `TargetZipUnreadable` gives all three items that `Error`, and the files stay in ingest.
  - Done when: `ExternalLibraryTests` passes (Docker running).
- [ ] **9. Set ingest: guards, job rules and lone files**: B7 (`TooRecent`, changed after hashing, size limits, path limit, cancellation, progress, systems without `.cue`, lone files). The set checks that stop a set before anything moves, and the job-level rules.
  - Files: `Core/Ingest/IngestJob.cs`, `Core/Ingest/IngestPipeline.cs`, `ExternalLibraryTests.Sets.cs`, `IngestJobTests.cs`.
  - Tests:
    - `ExternalLibraryTests.Sets.cs`: one member `TooRecent`, or changed after hashing, skips the set; a member over the size limit moves to `_failed/` as `TooLarge` and the others are `Skipped` `IncompleteSet` and stay, also on the next run; a lone `.nrg` goes to Review, and a lone `.txt` fails as `UnsupportedType`; a lone matched `… (Track 1).bin` with no cue is `Skipped` `IncompleteSet` and stays, while a lone matched untagged `.bin` is stored as a single file;
    - `IngestJobTests` (its handler array and `ByFamily` substitute gain `ExternalSetTestHandler`, with its fixture DAT written by `FixtureDatWriter`): a cancelled job finishes the set it started (the token is cancelled from `ExternalHooks.BeforeHash` on the set's second member); a selection holding one `.bin` of a 3-file set gives a progress total of 3; on a `FileSets` handler without `.cue` in its extensions (a substitute from the test's `Handler` helper: id and family `discx`, `EmbedRomsInZip` false, `FileSets` true, `RomExtensions` `.bin` only, `DatFolder` `redump`, no `MetadatKinds`, an empty fixture DAT), `x.cue` fails `UnsupportedType` and `x.bin` goes to Review, with no planning.
  - Done when: `ExternalLibraryTests` (Docker running) and `IngestJobTests` pass.
- [ ] **10. Set ingest against existing state**: B7a (duplicate, data track in Review, dismissed, `AmbiguousGroup`, repeated SHA1s, members' own review rows), B7 (lone `.bin` `AmbiguousGroup`), B23 (`AmbiguousGroup`, `AlreadyInReview`).
  - Files: `Core/Ingest/IngestPipeline.cs`, `Core/Ingest/IngestOutcome.cs`, `ExternalLibraryTests.Sets.cs`.
  - Tests (`ExternalLibraryTests.Sets.cs`):
    - a duplicate data track sends all members to `_duplicates/`, one `MovedToDuplicates` event each;
    - an `AmbiguousGroup` data track skips every member, and a lone matched `.bin` with an ambiguous group is `Skipped` `AmbiguousGroup` and stays;
    - a data track with a `Pending` review row from another source sends every member to `_duplicates/`;
    - two unmatched sets sharing an identical audio track queue it once, and the second copy is `Skipped` `AlreadyInReview` and stays;
    - re-running an unmatched set keeps all three items as `Review` (own rows upserted);
    - an unmatched set whose data track is added to the DAT before the next run is ingested, and its three `Pending` rows become `Resolved`;
    - a dismissed data track's set is ingested.
  - Done when: those classes pass (Docker running).
- [ ] **11. Track self-repair**: B14.
  - Files: `Core/Ingest/IngestPipeline.cs`, `ExternalLibraryTests.Repair.cs` (existing partial).
  - Tests (`ExternalLibraryTests.Repair.cs`):
    - a deleted audio track with an open `EntryMissing` is put back by re-ingesting the set: its item is `Repaired`, the issue resolves, and the other members are `Duplicate`;
    - a lone track file repairs too;
    - a matching name with a different SHA1 is not used for a repair;
    - a lone track whose name and SHA1 match open-issue files of two games isn't repaired and goes to Review.
  - Done when: `ExternalLibraryTests` passes (Docker running).
- [ ] **12. Review rules (API)**: B15 except the SPA (step 20). Add `SetFileNotResolvable` (409, in `ReviewEndpoints.ToResult`), the Dat-resolve naming and `..` rule for `FileSets` systems, the DAT search's one row per `gameName`, the `AmbiguousSet` reason filter in `GET /api/review`, and `resolvable` on the item detail.
  - Files: `ReviewResolver.cs`, `ReviewEndpoints.cs`, the tests below.
  - Tests:
    - `ReviewResolverExternalTests` (its `SystemRegistry` gains `ExternalSetTestHandler`; the set cases queue their item under `ingest/extset/`): resolving or dismissing a `.bin` item gives `SetFileNotResolvable`; an `.nrg` resolved to a Dat game whose rom is `… (Track 1).bin` is stored as `<Game> (Unverified).nrg` with no `Track 1` flag; one whose game name starts with `...` resolves; an `AmbiguousGroup` `.iso` attached with flag `None` takes its tags from `datGameName`, not the queued `romName`.
    - `ReviewEndpointsTests`: a DAT search over a repeated game name returns it once; `reason=AmbiguousSet` is accepted.
    - `ExternalLibraryTests.Sets.cs` (its host has `ExternalSetTestHandler`; `ReviewEndpointsTests`' host doesn't): resolving or dismissing a `.bin` item gives `409` with code `SetFileNotResolvable`; its detail has `resolvable` false and an `.iso` item's true; resolving an `.iso` item whose exact name is taken in its folder gives `409 NameTaken`, and the item stays `Pending`.
  - Done when: `ReviewResolverExternalTests`, `ReviewEndpointsTests` and `ExternalLibraryTests` pass (Docker running).
- [ ] **13. Title edit and delete on sets**: B16, B17. `NameTaken` in `LibraryEndpoints.ToProblem`.
  - Files: `Core/Library/LibraryEditor.cs`, `Core/Library/LibraryEditResult.cs` (`NameTaken`), `Api/Endpoints/LibraryEndpoints.cs`, `ExternalLibraryTests.Sets.cs`.
  - Tests (`ExternalLibraryTests.Sets.cs`, from step 6):
    - a title edit in the same bucket renames only the ZIP;
    - a title edit is refused with `TargetZipUnreadable` when another game also claims one of the set's files in the old folder;
    - a bucket change moves every file, and a taken name there gives `409 NameTaken` with nothing moved;
    - release delete leaves 3 files in `_removed/` and writes one `ReleaseDeleted` event whose details list them;
    - game delete moves every file and writes one `GameDeleted` event.
  - Done when: `ExternalLibraryTests` passes (Docker running).
- [ ] **14. Move and merge of sets**: B18. Whole-set move/merge and `ExternalMoveRecord` v2 with v1 still read.
  - Files: `Core/Library/LibraryEditor.cs`, `Core/Library/ExternalMoveRecord.cs`, `ExternalLibraryTests.Sets.cs`, `ExternalMoveRecordTests.cs`.
  - Tests:
    - `ExternalLibraryTests.Sets.cs`: move into a folder with a taken name gives `409 NameTaken` and nothing moves; a same-folder merge moves no set files and succeeds; a logical-name clash on merge gives `NameTaken`; a move writes a v2 record; a move whose second set file fails to move (file system hook) undoes the first and gives `IoError`; moving a single-file `.iso` release to a game in another folder keeps its exact name (no `<ZipBase>.` prefix, no ` (N)`).
    - `ExternalMoveRecordTests`: v2 round-trip with 99 pairs of 250-byte names made of `'` (each escaped to 6 bytes) under the 320 KiB cap, a v2 record whose `files` names differ from the backed-up release's `files` is refused (`InvalidDataException`), and a v1 record still read and validated.
  - Done when: those classes pass (Docker running).
- [ ] **15. Integrity for set files**: B19.
  - Files: `Core/Integrity/IntegrityJob.cs`, `IntegrityJobTests.cs`.
  - Tests (`IntegrityJobTests`, Data integration, with `ExternalSetTestHandler` appended to its handlers):
    - a missing track gives `EntryMissing` keyed by its name, and the issue resolves on the next run once the track is back;
    - a truncated track gives `EntrySizeMismatch`;
    - a deep run on a changed track gives `HashMismatch`;
    - set files are not `OrphanFile`.
  - Done when: `IntegrityJobTests` passes (Docker running).
- [ ] **16. API for release files**: B20 (API part), B21. Add `files` and `totalSizeBytes` to the detail, the file download route, and the sizes in the list and report.
  - Files: `Api/Endpoints/LibraryEndpoints.cs`, `Api/Endpoints/LibraryFiles.cs`, `Data/Repositories/LibraryRepositories.cs` (`PrimarySizeBytes`), `Data/Repositories/ReportRepository.cs` (`GameBytes`, which both the summary and the system drill-down use), `LibraryEndpointsTests.cs`, `ReportEndpointsTests.cs`.
  - Tests:
    - `LibraryEndpointsTests` (integration, mirroring an `ExternalSetTestHandler` game directly, as its external tests do): detail lists files and `totalSizeBytes`; `GET /api/games` gives `primarySizeBytes` including the primary release's files; the download streams bytes with range support; another release's file id gives 404.
    - `ReportEndpointsTests`: the size includes the files.
  - Done when: those classes pass (Docker running).
- [ ] **17. The four headerless handlers**: B4. Add `RnRomManager.Systems.Sony` (`psx`, `psp`) and `.Microsoft` (`x360`) with their test projects, the Saturn handler in `.Sega`, their box art, `RnRomManager.slnx` and the `Dockerfile` restore layer. They are not registered yet.
  - Files: `src/RnRomManager.Systems.Sony/` (new: csproj, `PlayStationHandler.cs`, `PlayStationPortableHandler.cs`, `EmbeddedResources.cs`, `Resources/boxart/`), `src/RnRomManager.Systems.Microsoft/` (new: csproj, `Xbox360Handler.cs`, `EmbeddedResources.cs`, `Resources/boxart/`), `src/RnRomManager.Systems.Sega/SaturnHandler.cs` and its box art, `tests/RnRomManager.Systems.Sony.Tests/`, `tests/RnRomManager.Systems.Microsoft.Tests/`, `tests/RnRomManager.Systems.Sega.Tests/SaturnHandlerTests.cs`, `RnRomManager.slnx`, `Dockerfile`.
  - Tests:
    - each handler's contract test (every B4 value, `ReadHeader` null);
    - box art is a valid 512×512 PNG and differs from the others in its project.
  - Done when: `Systems.Sony.Tests`, `Systems.Microsoft.Tests` and `Systems.Sega.Tests` pass, and `docker build .` succeeds.
- [ ] **18. The GameCube and Wii handlers**: B4, B6. Add `RnRomManager.Systems.NintendoDisc` (`gc`, `wii`, `NintendoDiscHeader`) with its test project, box art, `RnRomManager.slnx` and the `Dockerfile` restore layer. Not registered yet.
  - Files: `src/RnRomManager.Systems.NintendoDisc/` (new: csproj, `GameCubeHandler.cs`, `WiiHandler.cs`, `NintendoDiscHeader.cs`, `EmbeddedResources.cs`, `Resources/boxart/`), `tests/RnRomManager.Systems.NintendoDisc.Tests/`, `RnRomManager.slnx`, `Dockerfile`.
  - Tests:
    - each handler's contract test (every B4 value);
    - box art is a valid 512×512 PNG, and the two differ;
    - `NintendoDiscHeaderTests`: GC magic, Wii magic, no magic, a truncated span, a non-printable game id, and the title cut at the NUL.
  - Done when: `Systems.NintendoDisc.Tests` passes, and `docker build .` succeeds.
- [ ] **19. Register the six systems**: B4. `AppHost` registration and `RnRomManager.Api.csproj` project references, the startup `ingest/<id>/` and `roms/<id>/` folders, and the six (empty) fixture Redump DATs in the seeder's `FixtureDatWriter.Write`. The SPA's colours and icons follow in step 22; until then the new systems use the neutral fallback colour and the controller icon.
  - Files: `Api/AppHost.cs`, `Api/RnRomManager.Api.csproj`, `tests/RnRomManager.TestData/IngestSeeder.cs`, the 17 test hosts below, `IngestSeederTests.cs`, `DatEndpointsTests.cs`, `SystemStartupTests.cs`, `IngestEndpointsTests.cs`, `LibraryEndpointsTests.cs`, `IngestPipelineTests.cs`.
  - `IngestSeeder` gets the six DatName constants and `EmptyRedump`, a map from each to an empty game list with its layout (`redump`, no metadat kinds). Every Api test host that lists each libretro DAT by hand (today `AuditLibraryEditTests`, `DatEndpointsTests`, `ImageEndpointsTests`, `ImageUrlEndpointsTests`, `IngestArchiveTests`, `IngestDeleteEndpointsTests`, `IngestEndpointsTests`, `IngestPipelineTests`, `IngestReviewTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `NoteEndpointsTests`, `ReportEndpointsTests`, `ReviewEndpointsTests`, `ZipEntryEndpointsTests`) adds them; otherwise ingestion fails `DatsMissing`.
  - Update every count and list this breaks:
    - `IngestSeederTests`: DAT files 65 → 71 (plus the titledb file);
    - `DatEndpointsTests`: refresh requests 66 → 72; `/api/admin/dats` rows 15 → 21, and their order;
    - `SystemStartupTests` and `IngestEndpointsTests`: the registry-order list of 20 ids. `AppHost` registers the six after `SwitchHandler` in B4's table order (`psx`, `psp`, `gc`, `wii`, `x360`, `saturn`);
    - `LibraryEndpointsTests`: the `/api/systems` order and display names (20 systems; still exactly 13 with `embedRoms` true). The endpoint orders by `DisplayName` with `OrdinalIgnoreCase`, so the order is: 32X, Atari 2600, ColecoVision, Game Boy, Game Boy Advance, Game Boy Color, Game Gear, GameCube, Master System, Mega Drive / Genesis, Nintendo 64, Nintendo Entertainment System, Nintendo Switch, PlayStation, PlayStation Portable, Saturn, Super Nintendo Entertainment System, TurboGrafx-16, Wii, Xbox 360.
  - Tests:
    - `IngestPipelineTests`: a Wii ISO over 2 GiB (the Wii magic at 0x18, the rest zeros, extended with `SetLength`) dropped in `ingest/wii/`, and again in `ingest/gc/`, is not `TooLarge`: each goes to Review (`NoDatMatch`, the six Redump DATs are empty) with system `wii`.
  - Done when: the Core and Api test projects pass (Docker running).
- [ ] **20. Review page**: B15 (SPA part). The `AmbiguousSet` reason in the SPA's `ReviewReason` type and the queue's reason filter ("Ambiguous set"), `resolvable` in the item type, and the item page's Delete-only state with the hint, driven by `resolvable`.
  - Files: `lib/api/types.ts`, `routes/review/+page.svelte`, `routes/review/[id]/+page.svelte`, `routes/review/review-page.test.ts`.
  - Tests: `review-page.test.ts` (it tests `review/[id]` and the queue): an item with `resolvable` false shows the hint and Delete, with no action tabs and no Dismiss button; one with `resolvable` true shows today's actions; the queue's reason filter offers "Ambiguous set" and puts `reason=AmbiguousSet` in the URL.
  - Done when: `npm run lint`, `npm run check` and `npx vitest run src/routes/review/review-page.test.ts` pass.
- [ ] **21. Game page file list**: B20 (UI part). `lib/api/types.ts`, the Game page toggle and table, and the delete-release text.
  - Files: `lib/api/types.ts`, `routes/games/[id]/+page.svelte`, `lib/components/GameAdminModal.svelte`, `routes/games/game-page.test.ts`, `routes/games/game-admin.test.ts`.
  - Tests: `game-page.test.ts`: a release with 2 `files` shows "3 files", and the toggle lists the main file then the 2 files, with download buttons that have an `aria-label`, and the size shows the total; `game-admin.test.ts`: deleting a release with 2 `files` reads "The ROM and its 2 other files move…", and one without files keeps today's text; `table-style.test.ts` passes.
  - Done when: `npm test`, `npm run lint` and `npm run check` pass.
- [ ] **22. System colours and icons**: B22.
  - Files: `lib/systemColours.ts`, `lib/systemColours.test.ts`, `app.css`, `lib/components/icons.ts`, `lib/stores/stores.svelte.test.ts`.
  - Tests:
    - `systemColours.test.ts` for the six colours;
    - `stores.svelte.test.ts` (it holds the `iconClass` cases): `system-psx` and `system-psp` give `bi bi-playstation`, `system-x360` gives `bi bi-xbox`, and `system-gc`, `system-wii` and `system-saturn` give `bi bi-disc`.
  - Done when: `npm run lint`, `npm run check` and `npx vitest run src/lib/systemColours.test.ts src/lib/stores/stores.svelte.test.ts` pass.
- [ ] **23. Seed, integration and e2e**: one synthetic game per system in Basic, written loose:
  - `ingest/psx/`: `Disc Runner (USA).cue`, `Disc Runner (USA) (Track 1).bin` (data, in the fixture Redump DAT) and `Disc Runner (USA) (Track 2).bin`.
  - `ingest/saturn/`: `Sky Rally (Japan).cue`, `Sky Rally (Japan) (Track 1).bin` (data) and `Sky Rally (Japan) (Track 2).bin`.
  - `ingest/psp/`: `Pocket Ninja (USA).iso`.
  - `ingest/gc/`: `Cube Kart (USA).iso`, 64 KiB with the GC magic, game id `GCKE01` and title `CUBE KART`.
  - `ingest/wii/`: `Wave Bowling (USA).iso`, 64 KiB with the Wii magic, game id `RWBE01` and title `WAVE BOWLING`.
  - `ingest/x360/`: `Arc Strike (USA).iso`.

  Every `.bin` and `.iso` is 64 KiB of deterministic bytes from its own builder seed, so no two files share a SHA1 (above every minimum in B4). Each `.cue` names its two tracks with `FILE "…" BINARY` lines. Each system's fixture Redump DAT (step 19's `EmptyRedump` entry, now filled) lists exactly one game: the `.iso`, or the `(Track 1).bin` data track, under its seeded file name and with its game name (the file name without extension; for a set, the cue's stem). Track 2 and the cues are in no DAT, as in Redump (B3).
  - Files: `tests/RnRomManager.TestData/IngestSeeder.cs`, a synthetic disc builder in `tests/RnRomManager.TestData/` (new), `IngestSeederTests.cs`, `SeedIngestTests.cs`, `tests/e2e/specs/mvp.spec.ts`.

  None of the six fixture games has metadat (B2). Basic goes from 17 games, 22 releases and 22 Ingested to 23 games, 28 releases and 32 Ingested items, because each set member is an item; Duplicate 1, Review 2 and Failed 1 don't change. Full goes from 217 games, 222 releases and 222 Ingested to 223, 228 and 232.
  - Update every count:
    - `IngestSeederTests`: seed files 26 → 36 (Basic) and 226 → 236 (Full);
    - `SeedIngestTests`: the `[TestCase]` values (Ingested 32 / 232, games 23 / 223); its assertion that the release count equals the Ingested count changes to releases = Ingested − 4 (the set members beyond each data track); job items stay Ingested + 4;
    - `mvp.spec.ts`: `Ingested 32 · Duplicate 1 · Review 2 · Failed 1`, every `17 games` → `23 games`, the System option list (in `/api/systems` order, step 19: the six new systems each `(1)`, with `GameCube (1)` after `Game Gear (1)` and `PlayStation (1)`, `PlayStation Portable (1)`, `Saturn (1)` between `Nintendo Switch (1)` and `Super Nintendo Entertainment System (1)`, and `Wii (1)`, `Xbox 360 (1)` last), and the Auditing page's `Ingested` rows 22 → 28 (one event per set, B7a; the page loads 100 rows).
  - Tests:
    - `SeedIngestTests`: the PSX and Saturn releases have 2 `files` each, beside the ZIP under their exact names with their original bytes; their releases have no `Track 1` flag; the GC header has `gameId` `GCKE01` and `title` `CUBE KART`, and the Wii header `RWBE01` and `WAVE BOWLING`.
    - e2e: the PlayStation game's "3 files" toggle downloads Track 2 byte-exact.
  - Done when: the Core and Api test projects pass (Docker running), and `pwsh scripts/test-e2e.ps1` passes.
- [ ] **24. Docs**:
  - `README.md`: the systems list, the Refresh DATs note, the ingest folders, and identification against Redump as well as No-Intro DATs (its intro names only No-Intro).
  - `features.md`: the systems, disc sets (cue rule, `IncompleteSet`, archives, Review limits), sizes, and the Game page file list.
  - `zip-format.md`: v6 `files`, exact names for `FileSets` systems, the layout's six system folders, the GC/Wii header fields, the Naming table's Tags row (tags from the DAT game name for `FileSets` systems) and Base title row (`UK` and `United Arab Emirates` are known regions, B3a), the `system` id list, and the Validation `..` rule for `FileSets` names. Also: the intro's list of external systems; the External ROM files section's E1/E2 exception (exact names, `NameTaken`/`NameTooLong`, no prefix or suffix) and its Writes and Downloads bullets (a set's files are placed, verified by length and downloaded one by one); the Writes section's 1 MiB info.json refusal (B12); the heading `info.json (schema version 6)` and `schemaVersion` `6`; and a Versions row for 6 (adds `files`, the v5→v6 upgrader adds `files: null`, older apps treat a v6 file as `InfoJsonNewerSchema`), app version `0.4.0` (v0.3.0 is the latest tag, and v4 and v5 are both 0.4.0, still unreleased).
  - `features.md` also: the Review queue's "Ambiguous set" reason and the broken-cue rule; Admin → DATs' Refresh DATs fetching the Redump DATs; the External storage section's title-edit rule (set files keep their names, B16); and the API reference: `files`, `totalSizeBytes` and the file download route on `GET /api/games/{id}`, `resolvable` on the review item, the Dat action's naming for `FileSets` systems, and the new 409 codes.
  - `adding-a-system.md`: the new members, with the "one file per release" limit replaced by `FileSets`, and the size rule corrected (ingest checks size against the first family member that lists the extension). Also: the `DatName` and `DatSource` rows (the main DAT's folder is `DatFolder`, and only the `MetadatKinds` files are fetched, so a libretro system has one to five files), step 4's "five files per libretro system", the `DefaultBoxArtPng` row's list of drawn box art, and the "Storage of every shipped system" table's six new rows.
  - `deployment.md`: the new ingest folders (the `/app/ingest` row), the upgrade step (Refresh DATs), the External ROM storage section's scope (Switch and the six disc systems) the "Upgrading to 0.4.0" notes (ZIPs move to `info.json` version 6, adding `files`), and a recovery note for a crash mid-set (Risks: the moved members are `OrphanFile`s beside the ZIP; move them back to `ingest/<id>/` with the rest and ingest again).
  - `development.md`: project rows, `ExternalSetTestHandler`, the architecture sketch's "Switch ROM files beside their ZIPs" and the DAT sources bullet ("five DATs"; Redump folder and optional metadat), the `SeedIngestTests` paragraph (the disc sets, exact names and GC/Wii headers it checks), the e2e paragraph (the disc set's per-file download), the seed table (Basic: 23 games, 28 releases, Ingested 32; Full: 223 games, 228 releases, Ingested 232; the current table's 21/221 releases and Ingested 21/221 are stale, the code has 22/222), and the Basic paragraph naming the six new games and their files.

  Tests: none. Done when: every item above is in its doc.

## Decisions
- 2026-11-12 Multi-file discs → one release is a set of files (user).
- 2026-11-12 DATs → libretro Redump, with missing metadat skipped (user).
- 2026-11-12 Headers → GameCube/Wii magic only; the others are headerless (user).
- 2026-11-12 Seed → one synthetic game per system (user).
- 2026-11-12 Partial set → wait in ingest (`IncompleteSet`) (user).
- 2026-11-12 Unmatched sets → only single files (`.iso`/`.nrg`) resolve in Review (user).
- 2026-11-12 Download → per-file links, no set ZIP (user).
- 2026-11-12 Naming → exact names beside the ZIP, `NameTaken` on a clash, applied to all six systems (user).
- 2026-11-12 Archives → loose files only (`ArchiveNotSupported`) (user).
- 2026-11-12 Move/merge → the journal is extended to sets (v2) (user).
- 2026-11-12 Track self-repair → included (user).
- 2026-11-12 Set membership → the data track (Redump SHA1) plus the cue that names it, because libretro's Redump DATs list only the data track (user, after code/DAT check).
- 2026-11-12 GameCube and Wii share the `nintendo-disc` family, so a disc in either folder lands in the right system (code: §7.2 step 4).
- 2026-11-12 GameCube and Wii get the same size limits (32 KiB–9 GiB), because ingest size-checks against the first family handler that lists the extension (reviewer).
- 2026-11-12 PSP metadat isn't used: libretro keys it by serial, not CRC (reviewer, checked on master).
- 2026-11-12 Set outcomes for `AmbiguousGroup` (skip and stay), data track already in Review (all `Duplicate`), dismissed data track (the row moves, and the set is ingested as one match), and repeated SHA1s among unmatched members (`AlreadyInReview`, stay) (reviewer).
- 2026-11-12 Selected-files runs plan sets from the whole folder and process a set if any member is selected (reviewer).
- 2026-11-12 Names containing `..` are allowed; only `.`/`..` and path separators are rejected (reviewer: real Redump names such as `...Iru!`).
- 2026-11-12 A repaired track's item is `Repaired`, matching today's single-file self-repair (reviewer).
- 2026-11-12 The v2 move record is for manual recovery only, like v1, capped at 64 KiB (reviewer: no replay exists). Cap superseded below (320 KiB).
- 2026-11-12 Same-folder move/merge leaves set files in place; a logical-name clash gives `NameTaken`, never ` (N)` (reviewer).
- 2026-11-12 Dismiss is blocked for `.cue`/`.bin` review items, like resolve (reviewer).
- 2026-11-12 PSP extensions → `.iso` only: the PSP Redump DAT has no `.bin`/`.cue`, so those files could only ever be deleted from Review (fact).
- 2026-11-12 PSP maximum → 8 GiB: the DAT's largest PSP entry is 7,815,495,680 bytes and two entries are over 2 GiB (fact).
- 2026-11-12 Xbox 360's 9 `.bin` system discs → filtered out by its `.iso`-only extensions, as N3 does today (fact).
- 2026-11-12 Repeated Redump game entries (one per serial) → SHA1 matching keeps the first, and DAT search returns each `gameName` once (fact).
- 2026-11-12 Seed and examples use `(Track 1)`: Redump pads track numbers only on discs with 10 or more tracks (fact).
- 2026-11-12 Set member with an over-long path (I1), changed after hashing, or a cancelled job (I2) → member `IoError` with the rest `IncompleteSet`; whole set `TooRecent`; a started set finishes (code).
- 2026-11-12 Claim conflict on a file download → `404`, as `LibraryFiles.OpenExternal` does today, not `409` (code).
- 2026-11-12 New 409 codes go in `ReviewEndpoints.ToResult` and `LibraryEndpoints.ToProblem`; unlisted codes are 500 and `Problems.cs` holds no codes (code).
- 2026-11-12 Delete audit keeps today's shape: one `ReleaseDeleted` (details: main name then the set files) and an unchanged `GameDeleted` (code).
- 2026-11-12 The move record is written by move-release only; merge writes none, as today (code).
- 2026-11-12 Integrity's location check only checks the ZIP name, so exact set names need no change there (code).
- 2026-11-12 Unreadable or unparsable cue → it and every `.bin` starting with its stem are `Skipped` `IncompleteSet`; other files plan as usual (user).
- 2026-11-12 Cue members must be `.bin`; anything else is `UnsupportedSetMember`, because the Redump data tracks are `.bin` and an `.iso` member could never match (fact).
- 2026-11-12 A lone matched `.bin` with an ambiguous group → `Skipped` `AmbiguousGroup`, like a set, because `.bin` review items can't be resolved (reviewer).
- 2026-11-12 Set ingest audit → one `Ingested` per set, one `MovedToDuplicates` or `ZipRepaired` per moved or repaired member, as for single files (reviewer).
- 2026-11-12 `CueSheet.Parse` takes bytes (it decodes); the planner gets each small cue's bytes from `IngestJob`, so it stays pure; names differing only by case → `SetConflict` (reviewer).
- 2026-11-12 `DatRefreshJob.Parse` must treat the `DatFolder` file as the main DAT; today it checks `kind == DatKind.NoIntro` (code).
- 2026-11-12 The DAT search route is `GET /api/dat/search`, not `/api/review/dat/search`; repeats are removed before `limit` (code).
- 2026-11-12 `ReviewResolver` refuses generated names containing `..`; for `FileSets` systems it follows B12's rule (code).
- 2026-11-12 `AmbiguousSet` joins the Review reason filter, which accepts only `NoDatMatch`/`AmbiguousGroup` today (code).
- 2026-11-12 Thumbnail folders are named `<DatName>` with spaces, as `ThumbnailKey.Url` builds them (fact).
- 2026-11-12 Upgrader tests live in `InfoJsonTests` (V2–V4 do), not a new class (code).
- 2026-11-12 Dropped "issue text names the main file": unspecified text, and each set file is already its own issue entry (reviewer).
- 2026-11-12 Set ingest split into store/Review/rollback vs existing-state outcomes, and handlers into headerless vs GameCube/Wii, to stay near 300 lines (reviewer).
- 2026-11-12 Review resolve with a taken or over-long exact name → `409` `NameTaken`/`NameTooLong` via `ReviewEndpoints.ToResult`; the resolver passes the pipeline's code through, so unlisted they'd be 500 (code).
- 2026-11-12 A game's own external names come from `InfoJsonGame.ExternalFileNames()` (main then `files`) at every site that lists them today; integrity's `ReadPointers`/`PointerWasChecked` would otherwise never resolve a set file's issue, and the title edit's claim check would miss set files (code).
- 2026-11-12 `FileSets` ingest and Review skip `AllocateLogicalName`; a clash with a release of the target game is `NameTaken`, as for move/merge (code: it adds ` (N)` today).
- 2026-11-12 Lone-file track repair needs exactly one set file with that name, SHA1 and an open issue; otherwise Review (reviewer: `rom_files.sha1` isn't unique).
- 2026-11-12 Files no cue names and of no set extension are handled as today (`UnsupportedType`, B9); the progress total counts planned files after selection expansion (code: `SetTotal(snapshot.Count)`, the scanner lists every top-level file).
- 2026-11-12 Set-ingest integration tests go in `ExternalLibraryTests.Sets.cs` from step 6: `IngestPipelineTests`' host registers no external handler (code).
- 2026-11-12 File repository methods named (`ListFilesByGameAsync`, `GetFileAsync`, `ListFilesBySha1Async`); a set-file download is named by its `fileName`; a missing set file in delete or title edit is skipped as today (code).
- 2026-11-12 Steps split to stay near 300 lines: info.json vs `rom_files`, set planning (`IngestSetPlanner`) vs set ingest, edits vs move/merge, API vs Game page, handlers vs registration; test classes renamed to the existing ones (`InfoJsonTests`, `LibraryMirrorTests`, `ExternalLibraryTests`, `ReviewResolverExternalTests`, `review-page.test.ts`) and counts made exact (reviewer).
- 2026-11-12 Set planning only on systems that list `.cue` (`psx`, `saturn`); elsewhere a `.cue`/`.bin` is `UnsupportedType` as today (code: `ProcessCoreAsync` refuses extensions the family doesn't list).
- 2026-11-12 A store `Error` on a set gives every member the same `Error` after the rollback; `DbWriteFailed` marks every member and stops the job, as for a single file (code: `IngestJob.ItemAsync`).
- 2026-11-12 A PSX `.iso` always goes to Review: the PSX DAT has only `.bin` data tracks (fact).
- 2026-11-12 The SPA learns an item is Delete-only from `resolvable` on `GET /api/review/{id}`: no endpoint exposes `FileSets` (code).
- 2026-11-12 Step 20 adds the six Redump DATs to the 17 Api test hosts that list every libretro DAT by hand, or they fail `DatsMissing` (code).
- 2026-11-12 e2e Auditing `Ingested` rows 22 → 28: one event per stored set, and the page loads 100 rows (code).
- 2026-11-12 Set-handler 409 tests go in `ExternalLibraryTests.Sets.cs`: `ReviewEndpointsTests`' host doesn't register `ExternalSetTestHandler` (code).
- 2026-11-12 v2 move record cap → 320 KiB: the record uses System.Text.Json's default encoder, which escapes non-ASCII and `'`/`&`/`+` to 6 bytes, so 64 KiB couldn't hold 99 pairs (user).
- 2026-11-12 A cue with more than 99 distinct `FILE` names doesn't parse: a disc has at most 99 tracks, which also bounds the move record (fact).
- 2026-11-12 `ExternalSetTestHandler` lists `.nrg` (the lone-`.nrg` and Review `.nrg` tests need it) and uses the `redump` layout with no metadat, like the real handlers (code).
- 2026-11-12 A set member's own `Pending` review row is completed by its result through `CompleteSourceAsync`, as for a single file (code: `IngestPipeline.ProcessAsync`).
- 2026-11-12 Planned units run in the scanner's ordinal name order by first member; a `.bin` named by a parsed cue never falls under a broken cue's stem rule (code: `IngestFileScanner` orders by name).
- 2026-11-12 A set member over a size limit moves to `_failed/` as today (`FailFile`); the rest stay `IncompleteSet` until the Admin acts (code).
- 2026-11-12 `LibraryZipWriter` refuses an info.json over 1 MiB (`VerifyFailed`): sets can make it that large, and the reader refuses anything bigger (code: `InfoJsonReader.MaxBytes`, the writer doesn't check).
- 2026-11-12 For `FileSets` systems, Review's Dat action takes its base title and tags from `DatGameName`, and Attach (`AmbiguousGroup`, flag `None`) its tags, as ingest does (code: `ReviewResolver` parses the rom name today).
- 2026-11-12 DAT search dedupe applies to every system; the "No-Intro has no repeated names" claim was dropped (reviewer).
- 2026-11-12 Step 8 split into storing a set (8) and its guards, job rules and lone files (9) to stay near 300 lines; later steps renumbered (reviewer).
- 2026-11-12 `files` names follow the `externalFileName` rules (portable, 250 bytes); the size check is the maximum only, as for main files; `fileName` must equal `externalFileName` on `FileSets` systems (code: `InfoJsonValidator.ExternalProblem`; B10).
- 2026-11-12 The Game page's "N files" counts the main file too and lists it first, matching the e2e "3 files" (reviewer).
- 2026-11-12 Docs step also covers README's No-Intro-only intro, Refresh DATs in features.md, the External storage title-edit rule and the API reference additions (doc rule).
- 2026-11-12 Game page file list → in the release card's details body; the Size field shows `totalSizeBytes`; the header and Download ROM buttons keep downloading the main file (code: releases are cards, not table rows, and have no size column).
- 2026-11-12 System colours → exact hex values in B22; AA is checked by the e2e axe scans, because `systemColours.test.ts` has no contrast check (code).
- 2026-11-12 Step 4 updates `SchemaMigrationTests`, which lists every table and migration and counts 19 scripts (code).
- 2026-11-12 B21's `primarySizeBytes` gets a `LibraryEndpointsTests` case; it had none (reviewer).
- 2026-11-12 v2 move record = every v1 field plus `files` pairs; registry order appends the six after Switch; seed files are 64 KiB each; `ItemDone` once per member; lone-track repair runs where step 3's main-file repair does (reviewer).
- 2026-11-12 How does the mirror get `rom_files.rom_id`? → by joining `roms.sha1` (unique) in an `INSERT … SELECT`, because `LibraryMirror.ApplyAsync` batch-inserts `roms` without reading ids back (code).
- 2026-11-12 Validator rules for `files` entries' hashes and an empty array? → lowercase hex as for `metadata`; `[]` is rejected, a single file is `null` (code: `InfoJsonValidator` hash rule).
- 2026-11-12 Step 3 updates `InfoJsonTests`' `"schemaVersion": 5` assertion; `InfoJsonReader.CurrentVersion` holds the version (code).
- 2026-11-12 Set member rolled back to a taken ingest name? → moved back as a ` (2)` sibling, as a single file is today (`RollBack`, `AllocateRollbackPath`); it is a lone file on the next run (code).
- 2026-11-12 Seeded Wii disc lacked the game id and title the seed test asserts? → GC `GCKE01`/`CUBE KART`, Wii `RWBE01`/`WAVE BOWLING` (reviewer).
- 2026-11-12 Outcome of the >2 GiB Wii size test? → Review (`NoDatMatch`) as `wii` from both folders, never `TooLarge` (code: empty fixture DATs, the header classifies).
- 2026-11-12 Lone matched data track of a multi-track disc (DAT rom name has a `(Track N)` tag), no cue beside it? → `Skipped` `IncompleteSet`, waits in ingest for its cue and tracks; only untagged single-track `.bin`s are stored alone (user).
- 2026-11-12 `files` name byte limit? → 255 UTF-8 bytes, today's validator rule for `externalFileName` (`MaxExternalFileNameBytes`); 250 is the allocator's limit (`ExternalRomName.MaxNameBytes`) (code).
- 2026-11-12 Set placement order and crash mid-set? → members moved in set order, then one ZIP write (W6 order); a crash leaves `OrphanFile`s plus ingest leftovers, recovered by hand as for a single file (code).
- 2026-11-12 Do a matched set's non-data members go through step 3's review-row checks? → no, only the data track; others only through B14 and the unmatched repeated-SHA1 rule (code: `IdentifyAsync` step 3 is per SHA1, and `rom_files.sha1` isn't unique).
- 2026-11-12 Multi-disc games? → each disc is a release of one game: `NoIntroName.Parse` makes `(Disc N)` a flag with a shared base title (code).
- 2026-11-12 B14's "only time a track isn't sent to Review" was wrong: today's step 3 still sends a lone track with another source's `Pending` SHA1 to `_duplicates/` (code).
- 2026-11-12 Exact `/api/systems` and e2e System option order → `OrdinalIgnoreCase` by display name (code: `LibraryEndpoints` `/systems`); icon mappings get `stores.svelte.test.ts` cases; seed DAT contents, `IngestJobTests`/`ReviewResolverExternalTests` hosts and the development.md seed numbers made exact (reviewer).
- 2026-11-12 Every step names its files; new handler classes follow the `<System>Handler` pattern (`PlayStationHandler`, `PlayStationPortableHandler`, `GameCubeHandler`, `WiiHandler`, `Xbox360Handler`, `SaturnHandler`) with a per-project `EmbeddedResources` like `Systems.Sega` (reviewer).
- 2026-11-12 A cue whose own file name isn't portable (`:`, `?`, a device name…)? → the set is `UnsupportedSetMember`: it would be stored under that exact name and the validator refuses it; B8 only checks the names inside the cue (reviewer).
- 2026-11-12 Move/merge of a set with a missing or unmovable file? → each set file follows today's main-file rules: missing is logged and only its entry moves, a failed move undoes earlier moves with `IoError` (code: `LibraryEditor` merge and move-release).
- 2026-11-12 `LibraryZipWriter.ExternalFiles` throws for a touched name that isn't a main `externalFileName`, so step 3 extends it to `files` entries (code).
- 2026-11-12 Do test substitutes of `ISystemHandler` get the new defaults? → no: NSubstitute 6.2.0 ignores default interface members (`""`, empty, `false`), so step 1 sets `DatFolder`/`MetadatKinds` in the seven DAT-loading test factories; `FileSets` `false` is already right (fact: checked with a scratch project).
- 2026-11-12 Redump's `(UK)` tag (296 rom names; `NoIntroRegions` knows only `United Kingdom`, so it would parse as a flag)? → add `UK` as a known region for every system (user).
- 2026-11-12 Steps out of the data → core → API → UI → docs order (Review UI in step 12, the Game page before the handlers, colours and icons in the registration step)? → Review rules split into API (12) and SPA (20); the handlers (17, 18) and registration (19) come before the UI steps (20 Review page, 21 Game page, 22 colours and icons); seed/e2e is 23 and docs 24. Earlier decisions keep the old step numbers (old 17 → 21, 18 → 17, 19 → 18, 20 → 19 and 22, 21 → 23, 22 → 24) (reviewer: spec skill step order).
- 2026-11-12 Lone member repair names its input exactly: a lone `.bin` (one no cue names, B7); a lone `.cue` is `IncompleteSet` and only a whole set repairs it (code: B7 planning runs first).
- 2026-11-12 `AmbiguousSet` review rows store `[]` suggestions and null `dat_identification`/`candidate_game_ids`; `SetCurrentItem` names each member as it is hashed (reviewer: unresolvable items need neither; obvious default).
- 2026-11-12 The delete-release confirmation ("The ROM moves…") would hide a set's other files → "The ROM and its N other files move…" for a release with files (reviewer: `GameAdminModal` text; obvious default).
- 2026-11-12 Docs step made exact for `zip-format.md` (intro, E1/E2 exception, Writes, v6 heading and Versions row at 0.4.0), `adding-a-system.md` (DatName/DatSource rows, box-art list, storage table), `deployment.md` (0.4.0 notes, External storage scope, crash-mid-set recovery) and `development.md` (architecture, DAT sources, seed and e2e paragraphs) (doc rule; fact: v0.3.0 is the latest tag).
- 2026-11-12 B3 said every PSX/Saturn entry is `(Track 1)`/`(Track 01)` or untagged? → 161 Saturn entries list `(Track 2)`/`(Track 02)`; the rules already key on the SHA1 match and any `(Track <digits>)` tag, so only the fact changes (fact: Saturn DAT 2026.10.07).
- 2026-11-12 `UK` count and DATs? → 180 rom names in all six DATs, including lists like `(UK, Australia)`; the earlier "296 in four DATs" was wrong (fact).
- 2026-11-12 `(United Arab Emirates)` in five Redump game names is also unknown to `NoIntroRegions` → added with `UK`, same reason as B3a; no other unknown region group in the six DATs (fact).
- 2026-11-12 Step 9's "`FileSets` handler without `.cue`" named no handler → a `discx` substitute from `IngestJobTests`' `Handler` helper, with the `.cue` `UnsupportedType` and the `.bin` in Review (reviewer).
- 2026-11-12 B18 named only sets, so moving or merging a single-file `.iso` release on a `FileSets` system had no naming rule (today's code prefixes `<ZipBase>.` and may add ` (N)`) → B18 covers every file of every moved release, as B16 and B17 already do; step 14 tests a single `.iso` move (code: `LibraryEditor.MoveReleaseExternalAsync` calls `ExternalRomName.Allocate`; B10).
- 2026-11-12 What does v2 move-record validation check for `files`? → versions 1 and 2 accepted; each `files` name passes `Problem`'s pointer check, and the backed-up release's `files` names must equal the recorded source names in order (ignoring case), as v1 checks the main pointer (code: `ExternalMoveRecord.ValidateAsync` rejects any version but `CurrentVersion` and checks only the main pointer).
- 2026-11-12 What does the Delete-only Review item page keep, and which name decides `.cue`/`.bin`? → the action tabs and Dismiss are hidden and the hint takes the tabs' place; the File and Header cards, Google it and Delete stay; the extension is `originalFileName`'s (code: `review/[id]/+page.svelte` tabs, `ReviewItem.OriginalFileName`).
- 2026-11-12 Step 3's set-file length tests go in `LibraryZipCheckedWriteTests`, not `LibraryZipTests`: only the checked write verifies external files, and its tests already host an external system (code).

## Kickoff prompt
```text
/spec implement 2026-11-12-disc-systems.md
```
Starts the spec, or resumes it at the first unticked step.
