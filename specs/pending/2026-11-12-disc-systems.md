---
title: Disc systems (PlayStation, PSP, GameCube, Wii, Xbox 360, Saturn)
status: ready
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
- **B2 Optional metadat.** A new handler property, `MetadatKinds` (default: all four), lists which metadat files exist. DAT refresh downloads only those files, and `DatCache` requires only those. A kind the handler doesn't list gives empty metadata (null year, empty developers, publishers and genres). All six new handlers list **no** kinds.
  Facts (libretro-database master, checked 2026-11-12):
  - PSP has `releaseyear`, `publisher` and `genre`, but those files are keyed by `serial`/`image`, with no `crc`. `DatStore` joins metadat on CRC32, so they can't be used without a parser change. That change is out of scope.
  - The other five systems have no metadat.

  A 404 on a file the handler does list still fails the refresh, as today.
- **B3 Redump facts.** libretro's `metadat/redump/*.dat` files (version 2026.10.07) have **one rom per game**:
  - PSX (13,407 games) and Saturn (2,622): the data track `.bin` only. There is never a `.cue` or an audio track.
  - PSP (4,110), GameCube (2,237), Wii and Xbox 360: one `.iso`.

  Sizes reach about 7.8 GB (Xbox 360) and 8.5 GB (Wii dual layer). The thumbnail folders have the same names as the DATs.

### Systems
- **B4 Contract values.**

  | Id | DisplayName | ShortName | Family | RomExtensions | Min / Max bytes | DatName | MetadatKinds | ReadHeader |
  |---|---|---|---|---|---|---|---|---|
  | `psx` | PlayStation | PSX | `psx` | `.bin`, `.cue`, `.iso` | 16 / 1 GiB | `Sony - PlayStation` | none | null |
  | `psp` | PlayStation Portable | PSP | `psp` | `.iso`, `.bin`, `.cue` | 16 / 2 GiB | `Sony - PlayStation Portable` | none | null |
  | `gc` | GameCube | GC | `nintendo-disc` | `.iso` | 32 KiB / 9 GiB | `Nintendo - GameCube` | none | B6 |
  | `wii` | Wii | Wii | `nintendo-disc` | `.iso` | 32 KiB / 9 GiB | `Nintendo - Wii` | none | B6 |
  | `x360` | Xbox 360 | X360 | `x360` | `.iso` | 2 KiB / 9 GiB | `Microsoft - Xbox 360` | none | null |
  | `saturn` | Saturn | Saturn | `saturn` | `.bin`, `.cue`, `.nrg` | 16 / 1 GiB | `Sega - Saturn` | none | null |

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
- **B7 Sets.** For a `FileSets` system, before files are processed one by one, the ingest job plans sets from **all** loose files in `ingest/<id>/`, even for a selected-files run (IN1):
  - **Selected-files runs:** a set is processed when any of its members is selected, and then all of its members are processed. Unselected files that belong to no selected set are left alone.
  - **Parsing:** every `.cue` of at most 64 KiB is parsed with `CueSheet.Parse` (B8). Its set is the cue plus every file its `FILE` lines name. Names resolve in the same folder, ignoring case.
  - **Incomplete set:** if a named file is missing, or the cue doesn't parse, nothing in that set is processed. Every present member is `Skipped` with reason `IncompleteSet` and stays in ingest, so it is retried on the next run. A lone `.cue` whose files aren't there is the same case.
  - **Unsupported member:** a `FILE` name with an extension the handler doesn't list (`.wav`, `.ape`, `.img`, `.cue`, or `.iso` for Saturn), or the cue naming itself, skips the whole set. Every present member is `Skipped` with reason `UnsupportedSetMember` and stays in ingest.
  - **Shared member:** if one file is named by two or more cues, all of those sets are `Skipped` with reason `SetConflict`.
  - **Large cue:** a `.cue` over 64 KiB isn't parsed. It is handled as a lone file that is not a cue.
  - **Lone files:** a `.bin`, `.iso` or `.nrg` that no cue names, or a `.cue` over 64 KiB, is processed on its own as a single file (B11). A lone file that doesn't match goes to Review under B15.
  - The set is processed as one unit:
    - If any member is `TooRecent`, the whole set is `Skipped` with `TooRecent`.
    - Size limits apply to each file. A file that is too small or too large fails the whole set. That file gets `TooSmall` or `TooLarge`; the other members get `Skipped` with reason `IncompleteSet`.
    - Every member is streamed and hashed once. The set's **data track** is the one member whose SHA1 matches the system's DAT (family DATs for `nintendo-disc`).
- **B7a Set outcomes.**
  - **Exactly one match:** one release is stored. Its main file (`roms` row, info.json `fileName`) is the data track. The cue and the other tracks go in the release's `files` list (B12). Identification, base title, regions, metadata and media come from the data track's DAT entry. Every member's job item is `Ingested`.
  - **No match:** each member goes to Review (`NoDatMatch`) as its own item, with B15's restrictions.
  - **Two or more members match:** each member goes to Review with reason `AmbiguousSet`.
  - **Duplicate:** the data track's SHA1 is already in the library (`roms.sha1`). B14 runs first. Then every member not used for a repair is `Duplicate`, and all of them move to `_duplicates/` as today.
  - **Data track already in Review:** the data track's SHA1 has a `Pending` review row from another source. Today a single file in that case moves to `_duplicates/`; here every member is `Duplicate` and moves there.
  - **Data track dismissed:** the data track's SHA1 has a `Dismissed` row. The row moves to the new path, as it does for a loose file today, and the set is processed under "Exactly one match".
  - **`AmbiguousGroup`:** two or more games own the data track's base title. Every member is `Skipped` with reason `AmbiguousGroup` and stays in ingest. The Admin merges or moves the games, and the next run ingests the set. It doesn't go to Review, because Attach there would split the set.
  - **Repeated SHA1 among unmatched members:** `ingest_review_items.sha1` is unique, and silent audio tracks repeat. If a member's SHA1 already has a **`Pending`** row whose source is a different file, or matches an earlier member in this run, that member isn't queued. It stays in ingest, `Skipped` with reason `AlreadyInReview`, and is never moved to `_duplicates/`, because that would break its set. A member's own `Pending` row from an earlier run is re-queued as today (upsert, still `Review`). `Resolved` and `Dismissed` rows don't block a member.
- **B8 Cue parsing.** `Core/Ingest/CueSheet.Parse(string text)` reads `FILE "<name>" <type>` lines, and also unquoted `FILE <name> <type>`, ignoring case and leading whitespace. It returns the names in order without duplicates.
  - It is an error when there is no `FILE` line, or a name is empty, equals `.` or `..`, contains `/`, `\` or `:`, or is not a valid portable file name.
  - A name that merely contains `..` is valid. Real Redump names do this, for example `...Iru!`.
  - The text is decoded as UTF-8, falling back to Latin-1 on invalid bytes.
  - Other cue commands are ignored.
- **B9 Archives.** For a `FileSets` system, a `.zip` or `.7z` in `ingest/<id>/` is `Skipped` with reason `ArchiveNotSupported` and stays in place. Nothing is extracted.
- **B10 Exact names.** For a `FileSets` system, `ExternalRomName` stores a file under its exact name in the ZIP's folder, with no `<ZipBase>.` prefix (E1) and no ` (N)` suffix (E2):
  - A set member keeps its **source file name**, because the cue refers to it byte for byte. Its logical `fileName` equals `externalFileName`.
  - A single file (B11) is named the way today's libretro logic names it: the DAT rom stem plus the source extension. Review resolutions keep their names (B15).
  - **Release tags:** for `FileSets` systems, `LibretroRelease` parses regions, languages, revision and flags from `DatGameName`, not from the rom stem. Otherwise a data track would get a `Track 01` flag.
  - If a name is already taken in the folder (on disk, by a claim, or by the destination ZIP; checks ignore case), nothing in the item or set is moved. The item, or every member, ends as `Error` with code `NameTaken` and stays in ingest.
  - A name over 250 UTF-8 bytes ends as `Error` `NameTooLong`. It is not cut, because cutting would break the cue.
  - The ZIP name follows E2 as today.
- **B11 Single files.** A matched `.iso`/`.bin` that no cue names becomes a single-file release (no `files`), as Switch files do today but with B10 naming. An unmatched `.iso` or `.nrg` goes to Review, where it can be resolved.
- **B12 Release files.** In info.json schema **v6**, every rom has a `files` array. It is null when the release is one file. Otherwise each entry is `{ fileName, externalFileName, originalFileName, sizeBytes, crc32, md5, sha1 }`, in the cue's `FILE` order with the cue first.
  - The validator allows a non-null `files` only for `FileSets` systems. Every name must have one of the handler's extensions. Names must be unique across the game (main files and files together, ignoring case). Each file must be within the size limits.
  - For `files` names, and for main `fileName`s on `FileSets` systems, the validator's `..` rule rejects only a name equal to `.` or `..`, or one containing a path separator, as B8 does.
  - The `InfoJsonUpgraderV5` adds `files: null` to every rom.
  - In the database, migration `0020_rom_files.sql` adds `rom_files`, with every column `NOT NULL` and `utf8mb4_unicode_ci`, like `roms`:
    - `id`: `BIGINT` auto-increment PK;
    - `rom_id`: `BIGINT`, FK → `roms.id` ON DELETE CASCADE;
    - `game_id`: `CHAR(36)`, FK → `games` ON DELETE CASCADE;
    - `ordinal`: `INT`;
    - `file_name`, `external_file_name`, `original_file_name`: `VARCHAR(255)`;
    - `size_bytes`: `BIGINT`;
    - `crc32`: `CHAR(8)`; `md5`: `CHAR(32)`; `sha1`: `CHAR(40)`.

    Indexes: a **non-unique** index on `sha1`, because audio tracks repeat across discs, plus indexes on `rom_id` and `game_id`.
  - Existing rows need no backfill. The mirror (ingest, edits, DbSync rebuild) writes `rom_files` from info.json.
- **B13 Claims.** `IRomRepository.ListExternalAsync` returns the main files and the `rom_files` rows together. `ExternalRomRow` gets a nullable `FileId`. Name reservations, the integrity orphan scan and `LibraryFiles.OpenExternal`'s claim check therefore all see every file of a set.
- **B14 Track self-repair.** The data track already repairs itself through today's single-file path. Ingesting a set whose data track is a duplicate also checks each other member:
  - **Repaired:** a member whose name matches a file of that release (ignoring case), and whose SHA1 equals that file's recorded SHA1, while the file has an open `EntryMissing`, `EntrySizeMismatch` or `HashMismatch` issue. It is put back with today's `RepairPlan` and step-aside, one file at a time. Its item is `Repaired` and its issue resolves, as single-file repair does now.
  - **Duplicate:** every other member.
  - A lone member file (no cue in the folder) matching a set file with an open issue is repaired the same way. This is the only time a `.bin`/`.cue` that matches no DAT is not sent to Review.

### Review
- **B15 Review for `FileSets` systems.**
  - **Unmatched `.cue`/`.bin`:** a review item whose file extension is `.cue` or `.bin` can't be resolved. `POST /api/review/{id}/resolve` returns `409` `SetFileNotResolvable`, and the Review UI shows only Delete, with the text "Disc track files must match the Redump DAT to be imported." Delete works as today.
  - **Unmatched `.iso`/`.nrg`:** resolves as today. The Dat action names the file `<DatGameName> (Unverified)<source ext>`, not the first DAT rom's stem, because a Saturn `.nrg` maps to a game whose DAT rom is `… (Track 01).bin`. It takes metadata from the game's DAT rom. Its tags (regions, languages, revision and flags) come from `DatGameName`, as in B10, not from `datRom.RomName`.
  - **Dismiss:** `POST /api/review/{id}/dismiss` on a `.cue`/`.bin` item also returns `409` `SetFileNotResolvable`, so a set's members can't end up half dismissed.
  - **DAT search:** `GET /api/review/dat/search` is unchanged, because each Redump game has one rom (B3).

### Library edits
- **B16 Title edit.** For a `FileSets` system, renaming the ZIP never renames the external files, because their names don't contain the ZIP base. The ZIP rename and the folder change are unchanged. If the new folder (letter bucket) differs, every file of every release moves with its exact name, and a taken name refuses the edit with `409` `NameTaken`.
- **B17 Delete.** Deleting a release or a game moves every file of each release (main and `files`) to `ingest/<id>/_removed/`, using today's side-path allocation for each file. The audit events stay as they are today, and their details list every file moved:
  - deleting a release writes one `ReleaseDeleted` event;
  - deleting a game writes one `GameDeleted` event.
- **B18 Move / merge.** Move-release and merge move every file of a set with its exact name. A taken name in the target folder refuses the whole operation with `409` `NameTaken` before any file moves.
  - **Same folder:** when the source and target ZIPs are in the same folder, the set files don't move, and the source game's own claims don't count as taken.
  - **Logical names:** a logical-name clash in the target game (`AllocateLogicalName`) also refuses with `NameTaken`. It never adds ` (N)`, so `fileName` always equals `externalFileName`.
  - **Move record:** `ExternalMoveRecord` gets a format **v2** with a list of `{ sourceExternalFileName, targetExternalFileName }` pairs and the release's main SHA1. Like v1 it is written and validated for **manual recovery** only (W3/W7); there's no automatic replay.
    - Its size cap rises from 16 KiB to 64 KiB, which fits 99 tracks × 2 names × 250 bytes.
    - A v1 record (one pair) is still read and validated as today.
  - **Notes** stay keyed by the main file's `fileName`.

### Integrity
- **B19 Integrity.** Each `rom_files` row is checked like an external main file: missing gives `EntryMissing`, a wrong length gives `EntrySizeMismatch`, and a deep run checks `HashMismatch`. Findings are keyed by `(zipPath, externalFileName)`.
  - Set files are listed, so they are never orphans. The location check accepts exact names for `FileSets` systems, so they don't need the ZIP base.
  - Issue text names the release's main file name too.

### API and UI
- **B20 Game detail.** Each release in `GET /api/games/{id}` gains:
  - `files`: null, or a list of `{ id, fileName, sizeBytes, crc32, md5, sha1, downloadUrl }`;
  - `totalSizeBytes`: the main size plus the files.

  `GET /api/games/{id}/roms/{romId}/files/{fileId}/download` streams one set file. It works like today's ROM download: range support, the claim check, `404` when the file belongs to another release, `409` on a claim conflict, and the same auth.
  - On the Game page, a release with files shows a "N files" toggle under the row, listing each file with its `FileSize`, SHA1 (copy button) and a download icon button with an `aria-label`. The list follows the T1–T4 table rules.
  - The release's size column shows `totalSizeBytes`.
- **B21 Sizes.** In the library list, `PrimarySizeBytes` includes the primary release's files. The Library Size report sums `rom_files.size_bytes` along with the external main files. `romCount` stays a count of releases.

### UI and colours
- **B22 Badges and icons.**
  - `app.css` and `systemColours.ts` get the colours `psx`, `psp`, `gc`, `wii`, `x360` and `saturn`. Each has a foreground that meets AA (validated by the existing tests).
  - Icons in `icons.ts`:
    - `system-psx` and `system-psp`: `bi bi-playstation`;
    - `system-x360`: `bi bi-xbox`;
    - `system-gc`, `system-wii` and `system-saturn`: `bi bi-disc`.

### Job items
- **B23 Reasons and codes.**
  - New skip reasons: `IncompleteSet`, `UnsupportedSetMember`, `SetConflict`, `AmbiguousGroup`, `AlreadyInReview`, `ArchiveNotSupported`. They are recorded on job items, as `TooRecent` is. The Ingestion page doesn't change.
  - New review reason: `AmbiguousSet`.
  - New error codes: `NameTaken`, `NameTooLong`.
  - New ProblemDetails codes: `SetFileNotResolvable`, `NameTaken`.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `Core/Systems/ISystemHandler.cs`, `SystemRegistry` | `DatFolder`, `MetadatKinds` and `FileSets` with defaults; registry checks (B5) |
| Core | `Core/Dats/DatKind.cs`, `DatRefreshJob.cs`, `DatCache.cs`, `DatManifest` | B1, B2: folder and kinds per handler; versions keyed for `redump/` too |
| Api | `Endpoints/DatEndpoints.cs` | Admin → DATs lists `redump/` files (B1) |
| Core | `Core/Storage/ExternalRomName.cs` | Exact-name allocation for `FileSets` (B10) |
| Core | `Core/Library/InfoJsonRom.cs`, `InfoJsonRomFile.cs` (new), `InfoJsonReader.cs` (v6), `InfoJsonUpgraderV5.cs` (new), `InfoJsonValidator.cs`, `LibraryRows.cs`, `ILibraryRepositories.cs`, `LibraryZipWriter.cs` (verify set files by length) | B12, B13 |
| Core | `Core/Ingest/CueSheet.cs` (new), `IngestJob.cs`, `IngestPipeline.cs`, `IngestFileScanner.cs` (plan input) | B7–B11, B14, B23 |
| Core | `Core/Ingest/ReviewResolver.cs` | B15 |
| Core | `Core/Library/LibraryEditor.cs`, `ExternalMoveRecord.cs` | B16–B18 |
| Core | `Core/Integrity/IntegrityJob.cs` | B19 |
| Data | `Migrations/0020_rom_files.sql`, `Repositories/LibraryRepositories.cs`, `ReportRepository.cs` | Table, mirror, claims, sizes (B12, B13, B21). Existing data: no backfill; every existing rom gets `files: null` on its next read (upgrader) |
| Api | `AppHost.cs` (upgrader V5, 6 handlers), `Endpoints/LibraryEndpoints.cs`, `LibraryFiles.cs`, `ReviewEndpoints.cs`, `Problems.cs` | B15, B20, B23. API contract: additive (`files`, `totalSizeBytes`, the file download route) |
| Systems | `RnRomManager.Systems.Sony`, `.NintendoDisc`, `.Microsoft` (new), `.Sega` (Saturn), their test projects, `RnRomManager.slnx`, `Dockerfile` restore layer | B4, B6 |
| Web | `lib/api/types.ts`, `routes/games/[id]/+page.svelte`, `lib/systemColours.ts`, `app.css`, `lib/components/icons.ts`, `routes/review/[id]/+page.svelte` | B15, B20, B22 |
| TestData | `FixtureDatWriter.cs` (DatFolder/MetadatKinds), `FixtureGame.cs`, `IngestSeeder.cs`, new `ExternalSetTestHandler` (`FileSets` true, `.bin`/`.cue`/`.iso`), synthetic disc builders | Tests and seed |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md` (v6, `files`, exact names), `docs/adding-a-system.md` (new members, removed "one file per release" limit), `docs/deployment.md`, `docs/development.md` | Doc rule |

## Risks
- **Large change to the release model.** Most of the code assumes one file per release. `files` is additive, null for every existing release and only allowed for `FileSets` systems, so existing systems take the old paths unchanged. Each step keeps the existing tests green.
- **Schema v6.** Older app versions refuse v6 ZIPs (`InfoJsonNewerSchema`), as they do for every schema bump. Downgrading after ingesting a disc game needs a restore.
- **Shared track hashes.** Audio tracks repeat across discs, so `rom_files.sha1` isn't unique. Only the data track (`roms.sha1`) decides duplicates.
- **Only the data track is verified.** The cue and audio tracks have no DAT hash to check against. They're trusted at ingest and protected afterwards by their recorded hashes (integrity, B14).
- **Exact names can collide.** Two games in one letter folder with the same cue track name (for example a generic `Track 02.bin`) give `NameTaken` for the second. The file stays in ingest, and nothing is lost.
- **Very large files.** Multi-GB ISOs stream through the existing external hash and copy path, with step progress. Copies across volumes take as long as the disk needs.
- **New DATs.** After the upgrade, ingestion refuses to start (`DatsMissing`) until **Refresh DATs** has run, as with every new system. This goes in the deployment upgrade notes.

## Steps
- [ ] **1. Per-handler DAT folder and metadat kinds**: B1, B2. Add `DatFolder` and `MetadatKinds` to `ISystemHandler` (defaults keep today's behaviour). Update `DatRefreshJob`, `DatCache`, the manifest and versions handling, the Admin DATs listing (`DatEndpoints`) and `FixtureDatWriter`. `FixtureDatWriter.Write` takes an optional layout per DatName (`DatFolder`, `MetadatKinds`), defaulting to `no-intro` and all four kinds, and writes only those files.
  - Files: `ISystemHandler.cs`, `DatKind.cs`, `DatRefreshJob.cs`, `DatCache.cs`, `DatEndpoints.cs`, `FixtureDatWriter.cs`, the matching tests.
  - Tests:
    - `DatRefreshJobTests`: a handler with `DatFolder = "redump"` and `MetadatKinds = []` fetches exactly one URL, `/redump/<DatName>.dat`, and the result lists its version.
    - `DatCacheTests`: that system loads without metadat files, and returns empty metadata.
    - `DatEndpointsTests`: the redump row is listed.
    - A listed kind that returns 404 still fails the refresh.
  - Done when: Core and Api test classes above pass, and existing DAT tests pass unchanged.
- [ ] **2. `FileSets` flag and exact external names**: B5, B10 (allocation only). Add `FileSets` to the contract and registry checks. Add exact-name allocation to `ExternalRomName`, returning taken or too-long rather than suffixing or cutting. Add the `ExternalSetTestHandler` to TestData.
  - Tests:
    - `SystemRegistryTests`: `FileSets` with embedded storage is refused; a family with mixed `FileSets` values is refused.
    - `ExternalRomNameTests`: an exact name comes back unchanged; a taken name (ignoring case) is reported; 251 bytes is reported too long.
  - Done when: Core test classes above pass.
- [ ] **3. Release files in info.json and the database**: B12, B13. Add `InfoJsonRomFile`, `files` and schema v6. Add `InfoJsonUpgraderV5` (registered in `AppHost`), the validator rules, and migration `0020_rom_files.sql`. Update the mirror insert/delete, `ListExternalAsync` (union, `FileId`), DbSync rebuild and `LibraryZipWriter`'s length check for set files.
  - Tests:
    - `InfoJsonTests`: a v6 round-trip with files.
    - `InfoJsonUpgraderV5Tests`: v5 → v6 adds `files: null`.
    - `InfoJsonValidatorTests`: `files` on a non-`FileSets` system, a duplicate name across the game, a bad extension and an oversize file are each rejected; a `...Iru!` name is accepted on a `FileSets` system.
    - Data integration (`LibraryMirrorTests` / `RomRepositoryTests`): rows are written and cascade-deleted; `ListExternalAsync` includes the files; two releases sharing a track SHA1 are allowed.
    - `DbSyncTests`: a rebuild restores `rom_files`.
  - Done when: Core and Data test classes above pass (Docker running).
- [ ] **4. Cue parser**: B8. Add `Core/Ingest/CueSheet.cs`.
  - Tests: `CueSheetTests` covers quoted and unquoted names, mixed case, BOM, Latin-1 fallback, duplicates collapsed, an error on no `FILE` line, an error on a name of `.` or `..` or with `/`, `\` or `:`, `...Iru! (Japan).bin` accepted, and other commands ignored.
  - Done when: `CueSheetTests` passes.
- [ ] **5. Single-file ingest for `FileSets` systems and archive skip**: B9, B10, B11, B23 (`ArchiveNotSupported`, `NameTaken`, `NameTooLong`). A matched `.iso` is stored under its exact name with no `files`; an unmatched one goes to Review.
  - Tests (`IngestPipelineTests` / `IngestJobTests`, using `ExternalSetTestHandler`):
    - a matched `.iso` is stored beside the ZIP with no prefix;
    - a matched data `.bin` named `… (Track 01).bin` gets no `Track 01` flag;
    - a second game with the same file name in the same folder gives `NameTaken`, and the file stays;
    - a `.zip` is `Skipped` with `ArchiveNotSupported`;
    - an unmatched `.iso` goes to Review.
  - Done when: those classes pass.
- [ ] **6. Set ingest**: B7, B7a, B23 (`IncompleteSet`, `UnsupportedSetMember`, `SetConflict`, `AmbiguousGroup`, `AlreadyInReview`, `AmbiguousSet`). Plan sets in `IngestJob`, hash every member once, pick the data track, and place every file with rollback of all of them if any move or the ZIP write fails. Job items are per file.
  - Tests (`IngestJobTests` / `IngestPipelineTests`):
    - cue + data + audio track gives one release with 2 `files` in cue order, and every item `Ingested`;
    - a missing member gives `IncompleteSet`, and every file stays;
    - two cues naming one bin give `SetConflict`;
    - no match gives three Review items;
    - two matches give `AmbiguousSet`;
    - a duplicate data track sends all members to `_duplicates/`;
    - a failure on the third move rolls back the first two (file system hook), and ingest is left unchanged;
    - one member `TooRecent` skips the set;
    - a cue naming a `.wav` gives `UnsupportedSetMember`;
    - an `AmbiguousGroup` data track skips every member;
    - a data track with a `Pending` review row from another source sends every member to `_duplicates/`;
    - two unmatched sets sharing an identical audio track queue it once, and the second copy is `Skipped` `AlreadyInReview` and stays;
    - re-running an unmatched set keeps all three items as `Review` (own rows upserted);
    - a dismissed data track's set is ingested;
    - a selected-files run with only the `.bin` selected processes the whole set;
    - a lone `.nrg` goes to Review.
  - Done when: those classes pass.
- [ ] **7. Track self-repair**: B14.
  - Tests (`IngestPipelineTests` and the self-repair integration test class):
    - a deleted audio track with an open `EntryMissing` is put back by re-ingesting the set: its item is `Repaired`, the issue resolves, and the other members are `Duplicate`;
    - a lone track file repairs too;
    - a matching name with a different SHA1 is not used for a repair.
  - Done when: those classes pass.
- [ ] **8. Review rules**: B15. Add `SetFileNotResolvable` (409) and the Dat-resolve naming for `FileSets` systems, plus the UI's Delete-only state.
  - Tests:
    - `ReviewResolverTests`: resolving or dismissing a `.bin` item gives `SetFileNotResolvable`; an `.nrg` resolved to a Dat game whose rom is `… (Track 01).bin` is stored as `<Game> (Unverified).nrg` with no `Track 01` flag.
    - `ReviewEndpointsTests`: the 409 body has the right code.
    - The review page test (`review-item-page.test.ts` or the existing one) shows only Delete and the hint.
  - Done when: the tests pass, plus `npm run lint` and `npm run check`.
- [ ] **9. Edits on sets**: B16, B17, B18. Title edit without renames, delete moving every file to `_removed/`, move/merge of whole sets, and `ExternalMoveRecord` v2 with v1 still read.
  - Tests:
    - `LibraryEditorTests`:
      - a title edit in the same bucket renames only the ZIP;
      - a bucket change moves every file;
      - release delete leaves 3 files in `_removed/` and writes one `ReleaseDeleted` event listing them;
      - move into a folder with a taken name gives `409 NameTaken` and nothing moves;
      - a same-folder merge moves no set files and succeeds;
      - a logical-name clash on merge gives `NameTaken`.
    - `ExternalMoveRecordTests`: v2 round-trip with 99 pairs under the 64 KiB cap, and a v1 record still read and validated.
  - Done when: those classes pass.
- [ ] **10. Integrity for set files**: B19.
  - Tests (`IntegrityJobTests`):
    - a missing track gives `EntryMissing` keyed by its name;
    - a truncated track gives `EntrySizeMismatch`;
    - a deep run on a changed track gives `HashMismatch`;
    - set files are not orphans;
    - exact names pass the location check.
  - Done when: `IntegrityJobTests` passes.
- [ ] **11. API and Game page**: B20, B21. Add `files` and `totalSizeBytes` to the detail, the file download route, the sizes in the list and report, and the Game page file list.
  - Tests:
    - `LibraryEndpointsTests` (integration): detail lists files; the download streams bytes with range support; another release's file id gives 404.
    - `ReportEndpointsTests`: the size includes the files.
    - `game-page.test.ts` (or the existing Game page test): the toggle lists the files with download buttons, and the size shows the total.
  - Done when: the tests pass, plus `npm run lint` and `npm run check`.
- [ ] **12. The six handlers**: B4, B6, B22. Add the new projects and test projects, Saturn in Sega, box art, `AppHost` registration with `RnRomManager.Api.csproj` project references to the three new projects, the `Dockerfile` restore layer and `slnx`, the SPA colours and icons, `ingest/<id>/` folders, and the fixture DAT entries for all six (empty) in the seeder's `FixtureDatWriter.Write`.
  - Update every count and list this breaks: DAT file count 65 + titledb → 71 + titledb (one redump file per new system); systems 14 → 20; the `/api/systems` order; startup ids and folders; `IngestSeederTests`; the `/api/admin/dats` order.
  - Tests:
    - each handler's contract test (every B4 value);
    - box art is a valid 512×512 PNG and differs from the others in its project;
    - `NintendoDiscHeaderTests`: GC magic, Wii magic, no magic, a truncated span, a non-printable game id, and the title cut at the NUL;
    - `IngestPipelineTests`: a Wii ISO over 2 GiB (sparse file) is size-accepted in both `ingest/wii/` and `ingest/gc/`;
    - `SystemStartupTests`;
    - `systemColours.test.ts` for the six colours.
  - Done when: the new and changed test projects pass; `npm run lint`, `npm run check` and `npx vitest run src/lib/systemColours.test.ts` pass; and `docker build .` succeeds.
- [ ] **13. Seed, integration and e2e**: one synthetic game per system in Basic, written loose:
  - `ingest/psx/`: `Disc Runner (USA).cue`, `Disc Runner (USA) (Track 01).bin` (data, in the fixture Redump DAT) and `Disc Runner (USA) (Track 02).bin`.
  - `ingest/saturn/`: `Sky Rally (Japan).cue` with `(Track 01).bin` (data) and `(Track 02).bin`.
  - `ingest/psp/`: `Pocket Ninja (USA).iso`.
  - `ingest/gc/`: `Cube Kart (USA).iso`, 64 KiB with the GC magic and title.
  - `ingest/wii/`: `Wave Bowling (USA).iso`, 64 KiB with the Wii magic.
  - `ingest/x360/`: `Arc Strike (USA).iso`.

  None of the six fixture games has metadat (B2). Basic goes from 17 games, 22 releases and 22 Ingested to 23 games, 28 releases and 32 Ingested items, because each set member is an item. Full gains the same 6 games, 6 releases and 10 items.
  - Update every count in `IngestSeederTests`, `SeedIngestTests` (Basic and Full) and `mvp.spec.ts`, including the System option list.
  - `SeedIngestTests`' assertion that the release count equals the Ingested count changes to releases = Ingested − 4 (the set members beyond each data track).
  - Tests:
    - `SeedIngestTests`: the PSX and Saturn releases have 2 `files` each, beside the ZIP under their exact names with their original bytes; their releases have no `Track 01` flag; the GC/Wii headers have `gameId` and `title`.
    - e2e: the PlayStation game's "3 files" toggle downloads Track 02 byte-exact.
  - Done when: the Core and Api test projects pass (Docker running), and `pwsh scripts/test-e2e.ps1` passes.
- [ ] **14. Docs**:
  - `README.md`: the systems list, the Refresh DATs note and the ingest folders.
  - `features.md`: the systems, disc sets (cue rule, `IncompleteSet`, archives, Review limits), sizes, and the Game page file list.
  - `zip-format.md`: v6 `files`, exact names for `FileSets` systems, and the GC/Wii header fields.
  - `adding-a-system.md`: the new members, with the "one file per release" limit replaced by `FileSets`.
  - `deployment.md`: the new ingest folders and the upgrade step (Refresh DATs).
  - `development.md`: project rows and the seed table.

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
- 2026-11-12 The v2 move record is for manual recovery only, like v1, capped at 64 KiB (reviewer: no replay exists).
- 2026-11-12 Same-folder move/merge leaves set files in place; a logical-name clash gives `NameTaken`, never ` (N)` (reviewer).
- 2026-11-12 Dismiss is blocked for `.cue`/`.bin` review items, like resolve (reviewer).

## Kickoff prompt
```text
/spec implement 2026-11-12-disc-systems.md
```
Starts the spec, or resumes it at the first unticked step.
