# RnRomManager — Initial Application Spec

> Status: **v0.6 — ready for breakdown into implementation steps.** Second gap review done 2026-11-04 (DATs, thumbnails and Pan Docs re-verified live). Confirmed decisions are listed in §21; open items in §20.

## 1. Purpose

A self-hosted web app for managing a personal backup collection of game ROM files. Every ROM enters through an **ingestion** pipeline that identifies it, normalises its name, groups it with its sibling releases, fetches media, and stores it as one ZIP per game in a well-known folder layout. A MySQL/MariaDB database mirrors the ZIP contents for fast browsing/searching, and a scheduled integrity checker keeps the two in sync.

**MVP scope:** Game Boy (`.gb`) and Game Boy Color (`.gbc`) only.

### Non-goals (MVP)
- Emulation / playing games in the browser.
- Downloading or distributing ROMs. The app only manages files the user already owns.
- Systems other than GB/GBC (the design must make adding them easy, though — §22).
- Multi-tenant / per-user libraries (all users see the same library).
- More than one app instance per database (the job lock is in-process, §3.1).
- Automatic ingestion (folder watch / schedule), image upload, SignalR, user self-registration.

## 2. Tech stack

| Concern | Choice |
|---|---|
| Backend | .NET 10 ASP.NET Core Web API (minimal APIs) |
| Data access | Dapper + MySqlConnector (`DateTimeKind=Utc` in the connection string; all times stored UTC) |
| DB | MariaDB 10.11+ (tested); MySQL 8.0+ best effort. External, shared instance; app uses its own database (e.g. `rnrommanager`), no table prefix |
| Migrations | DbUp (`dbup-mysql`) — embedded `.sql` scripts, applied on startup, journal table `schema_versions` (§14.3) |
| Logging | NLog (`NLog.Web.AspNetCore`) — console + rolling text file (§13) |
| Backend tests | NUnit (`Assert.That` constraints, no extra assertion lib) + NSubstitute |
| Integration tests | Testcontainers (MariaDB) for repository/migration tests; real temp dirs for file tests |
| Frontend | Svelte 5 — SvelteKit with `adapter-static` (SPA mode, `fallback: index.html`), TypeScript |
| CSS | Bootstrap 5.3 (native `data-bs-theme` dark/light) |
| Icons | `bootstrap-icons` (primary) + `@fortawesome/fontawesome-free` (gaps only, e.g. brand logos) |
| Archive input | `System.IO.Compression` (ZIP) + SharpCompress (7z) |
| Frontend tests | Vitest + @testing-library/svelte; Playwright smoke e2e + `@axe-core/playwright` |
| Container | Single image; API serves the built UI from `wwwroot` |
| CI/CD | GitHub Actions → Docker Hub `niemandr/rnrommanager` |

## 3. Architecture

```
Browser (Svelte SPA) ──HTTP/JSON──> ASP.NET Core API ──Dapper──> MariaDB
                                          │
                                          ├── /app/ingest  (input)
                                          ├── /app/roms    (library, ZIPs)
                                          ├── /app/logs
                                          ├── /app/cache   (DATs, work dirs)
                                          └── outbound HTTPS: raw.githubusercontent.com (DATs), thumbnails.libretro.com
```

### Solution layout
```
/src
  RnRomManager.Api/             # endpoints, auth, hosting, static UI, --healthcheck mode
  RnRomManager.Core/            # domain, services, ingestion, integrity, job runner, DAT parser, naming, ISystemHandler
  RnRomManager.Data/            # Dapper repositories, DbUp migrations (embedded .sql)
  RnRomManager.Systems.GameBoy/ # gb + gbc handlers, header extractor, lookup tables, default box art
  RnRomManager.Web/             # Svelte app
/tests
  RnRomManager.TestData/        # console + library: synthetic ROM builder, fixture DAT writer (§15.2)
  RnRomManager.Core.Tests/
  RnRomManager.Data.Tests/
  RnRomManager.Systems.GameBoy.Tests/
  RnRomManager.Api.Tests/       # WebApplicationFactory
  e2e/                          # Playwright project + docker-compose.e2e.yml
/scripts                        # ps1 dev tooling (§16)
/docs                           # §19
/specs                          # this file
```

### 3.1 Job runner
- One in-process **global job lock** (`SemaphoreSlim(1)`). Background jobs: `Ingest`, `Integrity` (Quick/Deep), `DatRefresh`. Library edits and review resolutions (§7.5, §11) are synchronous request handlers that also take the lock.
- Lock busy → manual start returns `409 JobRunning`; scheduled integrity is skipped and logged at `Info`.
- Jobs persist to `jobs` (§14); live progress is held in memory and read via `GET /api/jobs/current`.
- Cancel: sets a flag checked between ingest files (an archive counts as one file: all its entries finish) and between ZIPs; status `Cancelled`.

## 4. Configuration

All settings via `appsettings.json`, overridable by environment variables. Validated at startup (`ValidateOnStart`); any failure → log `Fatal` + exit code 1.

| Key | Env var | Default | Validation |
|---|---|---|---|
| ConnectionStrings:Default | `ConnectionStrings__Default` | *(required)* | non-empty; DB reachable within 60 s (retry every 3 s) |
| Paths:Roms | `Paths__Roms` | `/app/roms` | absolute; created if missing; write probe |
| Paths:Ingest | `Paths__Ingest` | `/app/ingest` | as above |
| Paths:Logs | `Paths__Logs` | `/app/logs` | as above |
| Paths:Cache | `Paths__Cache` | `/app/cache` | as above |
| Admin:InitialPassword | `Admin__InitialPassword` | *(empty)* | empty or meets §9 password policy; used only at first seed |
| Integrity:QuickCheckTime | `Integrity__QuickCheckTime` | `03:00` | `HH:mm` or empty (= disabled); container local time (`TZ`) |
| Logging:MinLevel | `Logging__MinLevel` | `Debug` | NLog level name; applies to `RnRomManager.*` |
| Logging:RetentionDays | `Logging__RetentionDays` | `14` | 1–365 |
| Sources:DatBaseUrl | `Sources__DatBaseUrl` | `https://raw.githubusercontent.com/libretro/libretro-database/master/metadat` | absolute http(s) URL |
| Sources:ThumbnailBaseUrl | `Sources__ThumbnailBaseUrl` | `https://thumbnails.libretro.com` | absolute http(s) URL, or empty = media fetch disabled (default box art only; used by e2e, §15.3) |

- The four paths must be distinct and none may be inside another.
- Write probe: create + delete `.rnrm-probe` in each path. Failure message names the path and the process UID/GID (§17.2).
- Behind a TLS reverse proxy set the built-in `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so `Secure` cookies and scheme work.
- Everything else (limits, auth numbers, HTTP timeouts) is a **constant** in code, listed where used.
- Paths being configurable is what lets tests and manual testing point ingestion at a temp directory (§15.4).

### 4.1 Startup sequence
1. Load + validate config → 2. NLog → 3. log version + redacted effective config (`Info`) → 4. create folders (§5) → 5. wait for DB → 6. DbUp migrate (failure = exit 1) → 7. crash recovery (§7.4) → 8. seed admin (§9) → 9. load cached DATs → 10. start integrity scheduler → 11. listen on `8080`.

## 5. File system layout

```
/app
  roms/
    gb/
      #/          # names whose first character isn't A–Z
      A/ … Z/
        Tetris.zip
        Legend of Zelda, The - Link's Awakening.zip   # in L/
    gbc/          # GBC-only and dual-mode carts (D1, D23)
  ingest/
    gb/              # user drops files here
      _duplicates/   # byte-identical dupes (logged)
      _failed/       # unusable files; each gets `<name>.error.txt` (reason, job ID, UTC time)
      _removed/      # ROMs removed from the library by an Admin (§11)
    gbc/
  logs/
  cache/
    dats/            # <datName>.dat + dats.json (§10.1)
    work/<jobId>/    # archive extraction, deleted at job end and on startup
```

- Letter folders are **uppercase** `A`–`Z`; `#` holds everything else (digits, symbols, non-ASCII).
- Bucket = first character of the ZIP file name. ZIP names keep the No-Intro article form (`Legend of Zelda, The - …` → `L/`) (D24).
- The ingest folder is a hint only: the final system comes from §7.2 step 4. A GBC ROM dropped into `ingest/gb/` is stored under `roms/gbc/` (logged at `Info`).
- All system, letter and `_*` folders are created on startup if missing. Ingestion ignores `_*` folders and dot-files.
- Moving a file into `_failed/`, `_duplicates/` or `_removed/` when the name is taken appends ` (2)`, ` (3)`, … before the extension.

## 6. ZIP format (one ZIP per game)

```
Tetris.zip
  info.json
  roms/
    Tetris (World) (Rev 1).gb
    Tetris (Japan) (En).gb
  images/
    boxart-01.png
    snap-01.png
```

### 6.1 Naming
| Item | Rule |
|---|---|
| Base title | DAT game name up to the first ` (` (No-Intro always puts region first). Grouping key (D23). |
| Tags | Parsed from the **DAT ROM name** stem, not `game.name`: in 1,171 entries the ROM name adds tags (e.g. `(Aftermarket) (Unl)`). See §6.1.1. |
| Game `title` | Base title of the release that created the game; Admin-editable. Never auto-changes. |
| `displayTitle` | `title` with a trailing article moved to the front: `^(.*?), (The\|A\|An)( - .*)?$` → `$2 $1$3`. Shown in the UI. |
| ZIP name | `Sanitize(title) + ".zip"` |
| ROM `fileName` | DAT ROM name (DAT-matched), else §7.5 rules |
| Sanitize | Replace `\ / : * ? " < > \|` and control chars with `_`; trim spaces/trailing dots; max 180 chars; Windows reserved names (`CON`, `NUL`, …) get a `_` prefix |
| ZIP name clash | Existing ZIP at the target path with a different `gameId` → append ` (2)`, ` (3)`, … |
| Entry name clash in a ZIP | Append ` (2)`… before the extension |
| Images | `images/boxart-01.png`, `images/snap-01.png`. Only one of each in MVP. |

- All releases (regions, revisions) **and** hacks/translations of the same game live in the same ZIP. Hacks/translations are flagged (`Hack`, `Translated`) and are never `isPrimary` (D9).
- **Primary release** (recomputed on every write; exactly one per game), ranked by: not Hack/Translated → no status flag (a flag starting with `Beta`, `Proto`, `Possible Proto`, `Demo`, `Sample`, `Pirate`, or `Unverified`) → region `World` > `USA` > `Europe` > other with `En` > `Japan` > other → highest revision (§6.1.1) → `fileName` ordinal.
- **Writes are atomic:** stream existing entries + changes into `<name>.zip.tmp` in the target folder → re-open the tmp file and verify every ROM entry's SHA1 against `info.json` → `File.Move(tmp, final, overwrite: true)`. Never edit a ZIP in place. If the path changes (title edit), write at the new path, commit the DB, then delete the old ZIP.
- Compression: Deflate, `CompressionLevel.Optimal`. `info.json` is UTF-8 without a BOM, indented, camelCase, nulls written.
- **Default box art:** one embedded 512×512 PNG per system handler (generic cartridge silhouette with the system name, made for this project, MIT). Stored as `boxart-01.png` with `isDefault: true`.

#### 6.1.1 Tag parsing (No-Intro names)
Each `(…)` group after the base title is classified left to right. Groups are never split except regions and languages.
| Group | Rule | Example |
|---|---|---|
| 1st group | regions, split on `, ` | `(USA, Europe)` |
| `^[A-Z][a-z](,[A-Z][a-z])*$` | languages, split on `,` | `(En,Fr,De)`, `(Ja)` |
| `^Rev [0-9A-Z.]+$` or `^v\d+(\.\d+)*[a-z]?$` | revision (first match only) | `(Rev 1)`, `(v1.0.3)` |
| anything else | one flag, verbatim | `(SGB Enhanced, GB Compatible)`, `(Proto 2)`, `(Aftermarket)` |

- **Revision order:** none < any. Compare the dot-separated parts left to right: numbers numerically, letters ordinally (`Rev 2` > `Rev 1`, `v1.10` > `v1.9`, `Rev B` > `Rev A`). `Rev` and `v` aren't mixed within one game in practice; if they are, `fileName` ordinal breaks the tie.
- App-added flags: `Hack`, `Translated`, `Unverified` (§7.5).

### 6.2 `info.json` schema (v1)

**Game level**
| Field | Type | Req | Notes |
|---|---|---|---|
| schemaVersion | int | ✓ | `1` |
| gameId | GUID string | ✓ | stable; DB primary key |
| system | string | ✓ | handler `Id` (`gb`, `gbc`) |
| title | string | ✓ | §6.1 |
| displayTitle | string | ✓ | §6.1 |
| releaseYear | int? | | from metadat (§10) |
| developer | string? | | |
| publisher | string? | | |
| genres | string[] | ✓ | may be empty |
| description | string? | | Admin-edited only in MVP |
| roms | Rom[] | ✓ | ≥1 |
| images | Image[] | ✓ | exactly one `BoxArt`; ≤1 `Snap` |
| metadata.createdAt / updatedAt | ISO-8601 UTC | ✓ | |
| metadata.appVersion | string | ✓ | app version that last wrote the file |

**Rom**
| Field | Type | Req | Notes |
|---|---|---|---|
| fileName | string | ✓ | entry name under `roms/` |
| originalFileName | string | ✓ | as found in ingest (archive entry name if from an archive) |
| baseTitle | string | ✓ | grouping key (§6.1) |
| regions | string[] | ✓ | §6.1.1; may be empty |
| languages | string[] | ✓ | §6.1.1; may be empty |
| revision | string? | | `Rev 1`, `v1.1`, … |
| flags | string[] | ✓ | §6.1.1 (`SGB Enhanced`, `Proto`, `Unl`, …) + `Hack` / `Translated` / `Unverified` |
| isPrimary | bool | ✓ | |
| identification.method | `Dat` \| `Manual` | ✓ | `Dat` only for an exact SHA1 match |
| identification.datName | string? | | DAT header `name`, e.g. `Nintendo - Game Boy`. Also set for an `Unverified` release (the entry it was based on). |
| identification.datVersion | string? | | DAT header `version`, e.g. `2026.10.07` (D25) |
| identification.datGameName | string? | | DAT `game.name` (thumbnail key — can differ from the ROM name) |
| header | object? | | handler-specific (§8.2); null when unreadable |
| metadata.sizeBytes | long | ✓ | |
| metadata.crc32 / md5 / sha1 | lowercase hex | ✓ | |
| metadata.dateAdded / dateModified | ISO-8601 UTC | ✓ | |
| metadata.sourcePath | string | ✓ | ingest path at the time of ingestion (the archive's path for an archive entry; the entry is `originalFileName`) |

**Image**
| Field | Type | Req | Notes |
|---|---|---|---|
| fileName | string | ✓ | entry name under `images/` |
| type | `BoxArt` \| `Snap` \| `TitleScreen` \| `Other` | ✓ | MVP fetches BoxArt + Snap only |
| source | `libretro-thumbnails` \| `default` | ✓ | |
| sourceUrl | string? | | |
| isDefault | bool | ✓ | |
| sha1 | hex | ✓ | |
| width / height | int | ✓ | read from the PNG IHDR (no image library) |
| dateAdded | ISO-8601 UTC | ✓ | |

- `info.json` is the **source of truth**; the DB is a rebuildable mirror (content only, no image bytes).
- **Schema versioning:** readers accept `schemaVersion ≤ current`, upgrading older versions in memory through a chain of `IInfoJsonUpgrader` (vN→vN+1). The file is rewritten in the new version only on the next normal write. `schemaVersion > current` → read-only: integrity reports `InfoJsonNewerSchema` and ingestion/edits refuse to touch that ZIP.

## 7. Ingestion pipeline

Started manually by an Admin from the UI (`POST /api/jobs/ingest`). Runs as a single background job (§3.1).

### 7.1 Job start
1. Take the job lock; create a `jobs` row (`Ingest`, `Running`).
2. DATs must be loaded for every system with an ingest folder, else fail the job with `DatsMissing` (no download attempt; the Admin uses "Refresh DATs").
3. Delete `ingest_review_items` rows still `Pending` whose source file no longer exists (log `Info`).
4. Snapshot the files in every `ingest/<system>/` (non-recursive). Skip files modified < **10 s** ago (still copying; outcome `Skipped`, file untouched). Files added later wait for the next run.

### 7.2 Per file (correlation scope `JobId` + `FileId`)
| # | Step | Failure → outcome, file fate |
|---|---|---|
| 1 | **Validate**: extension is a supported ROM ext of any handler, `.zip` or `.7z`; ROM files (and every extracted ROM entry) between the handler's `MinRomSizeBytes` and `MaxRomSizeBytes` (§22); archives are limited by §7.3 | `Failed` `UnsupportedType` / `TooSmall` / `TooLarge` → `_failed/` |
| 1a | **Unpack** archives into `cache/work/<jobId>/<fileId>/` (§7.3). Each ROM entry continues as its own item from step 2. | `Failed` `ArchiveUnsafe` / `ArchiveCorrupt` / `ArchiveEncrypted` → `_failed/` |
| 2 | **Hash**: CRC32, MD5, SHA1 of the raw bytes (one read pass) | IO error → `Error`, source untouched |
| 3 | **Global dedupe**: SHA1 already in `roms`. **Self-repair:** if that ROM's entry has an open `HashMismatch` / `EntryMissing` / `EntrySizeMismatch` issue (§12), rewrite the entry from this file (atomic write §6.1), mark the issue `Resolved`, outcome `Repaired`, delete the source. SHA1 equals a `Pending` review item from another source path → `Duplicate`. | `Duplicate` → `_duplicates/`, `Warn` with both paths + hashes |
| 4 | **Header + identify**: read the header with every handler of the folder's family (§22). Look up SHA1 in all of the family's DATs. Match → system = the matching DAT's system (D23). No match → system = header classification. | — |
| 5 | **No DAT match**: logo invalid **and** header checksum invalid → `Failed` `InvalidHeader` → `_failed/`. Otherwise → `Review`: upsert `ingest_review_items` (unique on SHA1) with hashes, header, top-5 suggestions (§10.3); file stays in ingest. | |
| 6 | **Group**: find a `roms` row with the same `system_id` + `base_title` (case-insensitive) → its game. Else a new game (`title` = base title). | |
| 7 | **Metadata** (new game only): year/developer/publisher/genres from metadat by CRC of this release. | missing data → nulls |
| 8 | **Media**: new game, or existing game whose box art `isDefault` → fetch box art + snap (§10.2). | not found / HTTP failure → default box art, no snap, `Warn` |
| 9 | **Package**: atomic ZIP write + verify (§6.1) | verify/IO failure → `Error`, tmp deleted, source untouched. Existing game's ZIP missing/unreadable → `Error` `TargetZipUnreadable`; newer schema → `Error` `InfoJsonNewerSchema` (source untouched) |
| 10 | **Mirror**: replace the game's DB rows in one transaction | DB failure → `Error`; ZIP stays; DB repaired by the next integrity run; source **not** deleted |
| 11 | **Clean up**: delete the source (archive: §7.3) | delete failure → `Warn` (next run sees a `Duplicate`) |

- **Outcomes:** `Ingested`, `Repaired`, `Duplicate`, `Review`, `Failed` (file problem, moved to `_failed/` with `.error.txt`), `Error` (environment/unexpected problem; source left in place so the next run retries), `Skipped`. For archive fate (§7.3) `Repaired` counts as `Ingested`.
- **Review rows on re-runs:** files behind `Pending` review items stay in ingest and are re-processed every run (`last_seen_job_id` updated). If one now gets another outcome (e.g. it DAT-matches after a DAT refresh): `Ingested` → row `Resolved` with `game_id`, `resolved_by` null; any other outcome → row deleted.
- **Re-ingesting a ROM already in the library** is a `Duplicate` (or `Repaired`, step 3). To replace a release on purpose: delete it (→ `_removed/`), then ingest.
- Every item writes a `job_items` row. The job summary holds counts per outcome.
- An unexpected exception in one item never aborts the job; it becomes `Error` and the next file continues.

### 7.3 Archives
- Limits (constants): ≤ **100** entries, ≤ **256 MiB** total uncompressed, each ROM entry ≤ handler `MaxRomSizeBytes`. Sizes are enforced on the actual bytes streamed (a limit stream), not only the declared sizes.
- Reject the whole archive (`ArchiveUnsafe`) on: an entry name that is absolute, contains `..` or resolves outside the work dir (§9.3), a symlink entry, or a limit breach.
- Non-ROM entries and nested archives are skipped and logged at `Info` (not extracted).
- **Archive fate** after all its entries are processed: any entry `Review` or `Error` → archive stays; else any `Failed` → `_failed/` (the `.error.txt` lists every entry's outcome); else all `Duplicate` → `_duplicates/`; else deleted.
- A review item from an archive stores `archive_path` + `archive_entry`; resolution re-extracts that entry.

### 7.4 Crash recovery (startup)
- `jobs` rows still `Running` → `Aborted` (log `Warn`).
- Delete `cache/work/*` and every `*.zip.tmp` under `Paths:Roms`.
- This is safe because sources are deleted only after a verified ZIP write and a DB commit: a crash before the rename leaves the source → re-ingested; a crash after the rename but before the DB commit → the integrity run repairs the DB and the next ingestion reports the source as a `Duplicate`.

### 7.5 Review resolution (Admin, synchronous, takes the job lock)
| Action | Result |
|---|---|
| Pick a suggestion / DAT search result | Hashes differ from that DAT entry, so it's recorded as **unverified** (D30): `method = Manual`, `identification.dat*` set to the chosen entry, base title/tags from its ROM name + flag `Unverified`; `fileName` = DAT ROM stem + ` (Unverified)` + ext. Continues at step 6. |
| Attach to an existing game as `Hack` or `Translated` | `method = Manual`; `fileName` = `Sanitize(original stem) + " (Hack\|Translated)" + ext`; joins the chosen game |
| Manual entry (title, regions, languages, revision, flags) | `method = Manual`; `fileName` = `{title} ({regions}){ (revision)}{ (flags)}` + ext; grouped by title (step 6) |
| Dismiss | File → `_failed/` with reason `Dismissed`; item `Dismissed` |

The ROM extension follows the resolved system's handler. A resolved item becomes `Resolved` with the `game_id`.

## 8. Game Boy / Game Boy Color header extractor

Verified against Pan Docs ("The Cartridge Header"), re-checked 2026-11-04. The header is at `0x0100–0x014F`; GB/GBC `MinRomSizeBytes` = `0x150`, so smaller files are rejected (§7.2).

### 8.1 Fields
| Offset | Size | Field | Decoding |
|---|---|---|---|
| 0x0104 | 48 | Nintendo logo | Byte compare to the known bitmap → `logoValid` |
| 0x0134 | 16 | Title | ASCII up to the first NUL; 15 bytes if CGB bit 7 is set; 11 bytes when a manufacturer code is detected; non-printable → `?`; trimmed |
| 0x013F | 4 | Manufacturer code | Only when CGB bit 7 is set and all 4 bytes are `A–Z0–9`, else null |
| 0x0143 | 1 | CGB flag | `0x80` dual-mode, `0xC0` CGB-only. **Classification: `(flag & 0x80) != 0` → `gbc`, else `gb`** (the CGB boot ROM checks bit 7) |
| 0x0144 | 2 | New licensee | 2 ASCII chars; only when 0x014B = `0x33` |
| 0x0146 | 1 | SGB flag | `0x03` = SGB support |
| 0x0147 | 1 | Cartridge type | §8.3 |
| 0x0148 | 1 | ROM size | `0x00–0x08` → `32 KiB << v`; anything else → null (0x52–0x54 are unofficial and unused) |
| 0x0149 | 1 | RAM size | `00`→0, `01`→0 (unused), `02`→8 KiB, `03`→32 KiB, `04`→128 KiB, `05`→64 KiB. MBC2 reports 0 here but has 512×4-bit built-in RAM (`ramSizeBytes = 512` for types 05/06). |
| 0x014A | 1 | Destination | `0x00` Japan, `0x01` Overseas |
| 0x014B | 1 | Old licensee | Pan Docs table (embedded resource); `0x33` → use the new licensee table |
| 0x014C | 1 | Mask ROM version | |
| 0x014D | 1 | Header checksum | `byte x = 0; for i in 0x134..0x14C: x = (byte)(x - rom[i] - 1)`; valid if `x == rom[0x14D]` (the boot ROM enforces this) |
| 0x014E | 2 | Global checksum | Big-endian. `(ushort)` sum of every byte except 0x14E/0x14F. Informational only (hardware doesn't check it). |

Unknown table values are reported as `Unknown (0xNN)`.

### 8.2 `header` object (info.json / `roms.header` JSON)
`title`, `manufacturerCode`, `cgbFlag` (hex string), `sgbFlag` (hex), `cartridgeType`, `romSizeBytes` (null if unknown), `ramSizeBytes`, `destination` (`Japan` | `Overseas`), `licensee`, `version`, `logoValid`, `headerChecksumValid`, `globalChecksumValid`, `sizeMatchesHeader`, `isMbc1Multicart`.

### 8.3 Cartridge types (Pan Docs)
`00` ROM ONLY · `01` MBC1 · `02` MBC1+RAM · `03` MBC1+RAM+BATTERY · `05` MBC2 · `06` MBC2+BATTERY · `08` ROM+RAM · `09` ROM+RAM+BATTERY · `0B` MMM01 · `0C` MMM01+RAM · `0D` MMM01+RAM+BATTERY · `0F` MBC3+TIMER+BATTERY · `10` MBC3+TIMER+RAM+BATTERY · `11` MBC3 · `12` MBC3+RAM · `13` MBC3+RAM+BATTERY · `19` MBC5 · `1A` MBC5+RAM · `1B` MBC5+RAM+BATTERY · `1C` MBC5+RUMBLE · `1D` MBC5+RUMBLE+RAM · `1E` MBC5+RUMBLE+RAM+BATTERY · `20` MBC6 · `22` MBC7+SENSOR+RUMBLE+RAM+BATTERY · `FC` POCKET CAMERA · `FD` BANDAI TAMA5 · `FE` HuC3 · `FF` HuC1+RAM+BATTERY

### 8.4 Edge cases
| Case | Handling |
|---|---|
| Size ≠ header ROM size (small 8/16 KiB carts, overdumps, trimmed dumps) | `sizeMatchesHeader = false`; hash the file as-is (no trimming or padding). No DAT match → review. |
| MBC1 multicart (MBC1M) | Size 1 MiB + MBC1 type + valid logo at `0x40104` → `isMbc1Multicart = true`; header fields come from bank 0. Identified by hash like any ROM. |
| Bad dump / garbage | Identified by hash only. Unmatched with an invalid logo **and** checksum → `InvalidHeader` (§7.2 step 5). |
| Copier header (512 bytes) | Not a GB convention; no stripping. Such files won't DAT-match → review. |
| Extension ≠ classification | DAT match: the DAT ROM name decides. Unmatched: renamed to the classified system's extension, `Warn`. |

- The header title is **not** trusted for naming; it feeds suggestions (§10.3) only.
- `MaxRomSizeBytes` = **16 MiB** (the current DATs hold 8 KiB–16 MiB ROMs, including aftermarket carts).

## 9. Accounts, auth & security

### 9.1 Accounts
- Local accounts only, stored in `users` and hashed with ASP.NET Core `PasswordHasher<T>` (PBKDF2).
- Roles: `Admin` (users, ingestion, review queue, library edits, integrity, DATs, logs, settings) and `User` (browse/search/download). Enforced server-side by authorization policies `AdminOnly` / `Authenticated`; the UI hides what the role can't use.
- **Admin seed** when `users` is empty: username `admin`, password from `Admin:InitialPassword`, else 20 random URL-safe chars printed once via `Console.Out` (deliberately **not** through NLog, so it never reaches the log file). `must_change_password = true`.
- **Usernames:** 3–64 chars `[A-Za-z0-9._-]`, unique case-insensitively (column collation); login matches case-insensitively.
- **Password policy:** 12–128 chars; must not equal the username. No composition rules.
- **Lockout:** 5 consecutive failures → locked for 15 min (per user); a successful login resets the count. The response is always a generic `401 InvalidCredentials`; lockouts are logged at `Warn`. Admin reset-password clears the lockout and sets `must_change_password`.
- **Login rate limit:** ASP.NET `RateLimiter`, fixed window 10 requests/min per client IP (after forwarded headers) on `/api/auth/login` → `429 TooManyRequests`. Stops one client locking out every account.
- The last enabled Admin can't be disabled or demoted (`409 LastAdmin`). Users are disabled, never deleted.
- While `must_change_password` is set, every API call except `me`, `change-password` and `logout` returns `403 PasswordChangeRequired`.

### 9.2 Session & CSRF
- Cookie auth: name `rnrm.auth`, HttpOnly, `SameSite=Strict`, `Secure` = same as request (§4 forwarded headers), sliding expiry **7 days**. API returns 401/403 instead of redirecting.
- `users.security_stamp` is stored in the cookie and re-checked on every request. It changes on password change/reset, disable and role change, so existing sessions die.
- **Anti-forgery for the SPA:** ASP.NET `IAntiforgery` with header `X-XSRF-TOKEN`. `GET /api/auth/antiforgery` sets the readable cookie `XSRF-TOKEN`; login and logout re-issue it (tokens are bound to the signed-in user). Every `POST/PUT/PATCH/DELETE` under `/api` (login included) must send the header, else `400 AntiforgeryInvalid`. The SPA fetch wrapper adds it automatically.
- **Data Protection keys** (they sign the auth and antiforgery cookies) are persisted in the DB table `data_protection_keys` through a small Dapper `IXmlRepository`, application name `RnRomManager`. Without this, every container restart would log everyone out. Keys aren't encrypted at rest (the DB is trusted).
- Response headers: `Content-Security-Policy: default-src 'self'; script-src 'self' <hashes>; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`. At startup the API computes a `'sha256-…'` hash of every inline `<script>` in `wwwroot/index.html` (SvelteKit's bootstrap) for `<hashes>`. `kit.csp` isn't used (it isn't reliable on the `adapter-static` fallback page). The theme script is an external `/theme.js` (§11).

### 9.3 File-path safety
- One helper, `SafePath.Resolve(root, relative)`: `Path.GetFullPath(Path.Combine(root, relative))` must start with `GetFullPath(root) + separator`, else it throws `UnsafePathException`. Every computed path goes through it: ZIP paths, ingest/_failed moves, archive extraction, work dirs, log download.
- The API never accepts a file path. Images and ROMs are addressed by `gameId` + DB id / `info.json` entry lookup, never by concatenating a name.
- Library ZIPs read by the app: `info.json` ≤ 1 MiB; entry sizes must match `info.json`; entries outside `roms/`, `images/` and `info.json` are reported (`EntryUnlisted`), never extracted.
- Archive input limits: §7.3.

## 10. External data sources (free, no API key)

Verified live 2026-11-04: all 10 DAT files return 200 (GB DAT `2026.10.07`, 2,115 games). Every game has exactly one ROM, there are no duplicate SHA1s across both DATs, and ROM sizes range from 8 KiB to 16 MiB. Thumbnail checks: `…Asteroids & Missile Command…` → 404, `…Asteroids _ Missile Command…` → 200, `Tetris (World)` → 404, `Tetris (World) (Rev 1)` → 200 (so the fallback order matters).

| Need | Source (`Sources:*` base URLs, §4) |
|---|---|
| Canonical name, region, revision, hashes | `{DatBaseUrl}/no-intro/<DatName>.dat` (`Nintendo - Game Boy`, `Nintendo - Game Boy Color`) |
| Year, developer, publisher, genre | `{DatBaseUrl}/{releaseyear,developer,publisher,genre}/<DatName>.dat` (matched by CRC) |
| Box art, snap | `{ThumbnailBaseUrl}/<DatName>/Named_Boxarts/<key>.png`, `…/Named_Snaps/<key>.png` |

### 10.1 DATs
- **Format:** clrmamepro text (not XML). The header `clrmamepro ( name, version, … )` gives `datName` + `datVersion`. Each `game ( name "…" [region "…"] [serial "…"] rom ( name "…" size N crc X md5 X sha1 X ) )`. Metadat files: `game ( comment "…" <field> "…" rom ( crc X ) )`. The parser accepts repeated field lines (none exist today). Metadat headers have **no `version`** (`dats.json` stores null); `identification.datVersion` always comes from the no-intro DAT.
- **No `cloneof`/parent data** is present, so grouping is name-based (§6.1, D23).
- Regions and languages are parsed from the ROM-name tags (§6.1.1). The DAT `region` field is ignored because it's missing for World releases.
- Parsed into memory per system at startup and after a refresh (≈2–3k entries each): SHA1 → entry, CRC → metadat values, plus a name list for search/suggestions.
- **Refresh DATs** (Admin, `DatRefresh` job): downloads all 10 files (2 systems × no-intro + 4 metadat) to a temp dir → parses each → swaps them into `cache/dats/` all at once → writes `dats.json` `{datName, version, downloadedAt, sha1}`. Any failure keeps the old set. No automatic refresh (D18).
- **Offline:** cached DATs keep working. With no cache, ingestion fails fast with `DatsMissing` (§7.1).
- The GitHub API isn't used (unauthenticated limit of 60/h). Raw downloads only.

### 10.2 Thumbnails
- **Key:** the DAT `game.name` with `& * / : \` < > ? \ | "` each replaced by `_`, then the path segment URL-encoded (`Uri.EscapeDataString`). Verified: `&` → 404, `_` → 200.
- **Lookup order:** the primary release's game name → other releases in primary-rank order (§6.1). The first 200 for each type wins.
- **Validation:** PNG signature, ≤ 5 MiB; dimensions read from IHDR.
- `Sources:ThumbnailBaseUrl` empty → no lookup; default box art, no snap (no `Warn`).
- "Retry media" (Admin, game detail) re-runs the lookup when the box art is the default or the snap is missing.

### 10.3 Suggestions & search (review queue)
- **Normalise:** uppercase; drop everything except `A–Z0–9` and spaces; collapse spaces.
- **Suggestions:** compare the header title to every DAT base title of the classified system. Score = max(token Jaccard, 1 − Levenshtein/maxLen on space-free strings), +0.1 when the DAT ROM size equals the file size. Keep scores ≥ 0.4, top 5, stored with their score.
- **DAT search:** case-insensitive substring over game names of a chosen system, 20 results.

### 10.4 HTTP, limits, attribution
- `IHttpClientFactory` + `Microsoft.Extensions.Http.Resilience`: 20 s timeout, 3 retries (exponential backoff from 1 s) on 5xx/408/timeouts; 404 is not retried. Requests are sequential (ingestion is serial).
- No published rate limits for either host. The app makes at most one request at a time; DATs are fetched only on Admin refresh.
- `User-Agent: RnRomManager/<version>`.
- **Attribution:** libretro-database is **CC BY-SA 4.0**, credited in README and on Admin → About. Box art/snaps from libretro-thumbnails remain the copyright of their owners and are stored for personal use only (README disclaimer).
- Optional later (needs a free user-supplied key): ScreenScraper, TheGamesDB or IGDB.

## 11. UI

- Svelte 5 SPA, Bootstrap 5.3. Theme toggle (Light/Dark/Auto) in the navbar, persisted in `localStorage` (D13). A blocking `<script src="/theme.js">` in `app.html` (from `static/`, so CSP `'self'` allows it) sets `data-bs-theme` before paint (no flash). Auto follows `prefers-color-scheme` live.
- **Routing/guards:** unauthenticated → `/login?returnUrl=…`; `mustChangePassword` → `/change-password`; Admin pages show a "Not allowed" page to `User`.
- **Polling:** `GET /api/jobs/current` every 1.5 s while a job is running (pages: Ingestion, Integrity, Admin → DATs); stop when idle.
- **Shared states:** loading = spinner/placeholder cards; empty = message + next action (Admin: link to Ingestion); error = dismissible alert with the ProblemDetails `title` + Retry; `409 JobRunning` = toast.

| Page | Route | Role | Behaviour |
|---|---|---|---|
| Login | `/login` | anon | username/password, generic error |
| Change password | `/change-password` | auth | current + new + confirm, policy hint |
| Library | `/` | auth | Grid (box art, 2/3/4/6 cols at xs/md/lg/xl) or list (table) toggle, remembered in `localStorage`. Filters: system, letter (`All`, `#`, `A–Z`), region (any release; options from `/api/systems` `regions`), search (debounced 300 ms, ≥2 chars). Sort: title / year / date added. 48 per page. Filters live in the URL query. |
| Game detail | `/games/{id}` | auth | Box art + snap, displayTitle, year/dev/publisher/genres. Releases table: fileName, region, revision, flags, primary badge, size, CRC32/SHA1 with copy buttons, expandable header. Download ZIP / single ROM. **Admin:** Edit metadata (title, year, developer, publisher, genres, description), Retry media, Move release (to an existing game via typeahead, or to a new game with a title), Delete release, Delete game — each behind a confirm modal. |
| Ingestion | `/ingestion` | Admin | Pending files per system (name, size, modified); Start / Cancel; progress bar (current/total + current file); last 10 runs → run detail with per-item outcomes (filter by outcome) |
| Review queue | `/review` | Admin | Pending list (file, system, size, header title, added). Detail: header + hashes, suggestions with scores, DAT search, attach-to-game (game typeahead + Hack/Translated), manual form, Dismiss. |
| Integrity | `/integrity` | Admin | Last quick/deep run, next scheduled, Run quick / Run deep, issues table (filter by status/type), history |
| Admin | `/admin/users`, `/admin/dats`, `/admin/settings`, `/admin/about` | Admin | Users (create, enable/disable, role, reset password); DAT versions + Refresh; effective settings (read-only, secrets masked) + Download logs; version, licences, attribution |

- **Library edits:** deleting a release/game moves the ROM bytes to `ingest/<system>/_removed/` (never hard-deleted) and rebuilds or deletes the ZIP. Moving a release rebuilds both ZIPs (an emptied game is deleted). The moved ROM keeps its `baseTitle`, so later releases with that base title follow it automatically (D23).
- **Responsive:** navbar collapses below `lg`; tables use `table-responsive`; usable from 360 px wide.
- **Accessibility:** WCAG 2.2 AA target. Every control is keyboard reachable with a visible focus ring. Labels on all inputs. Box art `alt` = "`displayTitle` box art". Progress region `aria-live="polite"`. Modals use Bootstrap focus trapping. Contrast checked in both themes. Playwright runs axe on each smoke page and fails on `serious`/`critical`.
- **Icons:** a single `Icon.svelte` maps semantic names (e.g. `download`, `ingest`, `review`, `integrity`, `system-gb`) to Bootstrap Icons classes. Font Awesome only where BI has no match (e.g. the GitHub brand on About). Decorative icons get `aria-hidden="true"`; icon-only buttons need an `aria-label`.

## 12. Integrity checker

`Integrity` job: daily quick check at `Integrity:QuickCheckTime`, plus "Run quick" / "Run deep" (Admin). Shares the job lock with ingestion (§3.1); skipped when busy (D20).

### 12.1 Rules
| Mode | Per ZIP under `roms/<system>/` |
|---|---|
| Quick | If a DB row exists with equal `zip_size_bytes` + `zip_mtime_utc` → only check location/name. Else open the ZIP, parse `info.json`, compare the entry list + sizes to `info.json`, check box art, repair the DB. |
| Deep | Open every ZIP; all quick checks + recompute the SHA1 of every ROM and image entry. |

Both modes also: list orphan files (anything that isn't a `.zip` in the library tree, stale `*.zip.tmp`), check every DB `games` row still has its ZIP, and detect duplicate `gameId`s and SHA1s.

### 12.2 Issue types
| Type | Severity | Auto-repair |
|---|---|---|
| `ZipUnreadable` | Error | — |
| `InfoJsonMissing` / `InfoJsonInvalid` | Error | — |
| `InfoJsonNewerSchema` | Warn | — |
| `EntryMissing` (listed, not in ZIP) / `EntrySizeMismatch` | Error | — |
| `EntryUnlisted` (in ZIP, not listed) | Warn | — |
| `HashMismatch` (deep) | Error | — |
| `MissingBoxArt` | Error | — |
| `DefaultBoxArt` | Info | — |
| `WrongLocation` (system/bucket/name ≠ §6.1) | Warn | — |
| `OrphanFile` | Warn | — |
| `DuplicateGameId` | Error | DB repair skipped for those ZIPs |
| `DuplicateSha1` (same ROM in two games) | Warn | DB repair skipped for the later ZIP (`zip_path` ordinal), since `roms.sha1` is unique |
| `DbRowInserted` / `DbRowUpdated` / `DbRowDeleted` | Info | yes → recorded as `AutoRepaired` |

- **Fields:** `type`, `severity`, `system_id`, `zip_path`, `game_id`, `entry_name`, `details` (text), `status` (`Open` | `AutoRepaired` | `Resolved`), `first_seen_job_id`, `last_seen_job_id`, `detected_at`, `resolved_at`.
- **Dedup:** an open issue with the same (`type`, `zip_path`, `entry_name`) is updated (`last_seen_job_id`), not duplicated. An open issue not seen again in a run of the same or deeper mode → `Resolved`.
- **DB auto-repair** (D10): game rows are replaced from `info.json` in one transaction. Rows whose ZIP is gone are deleted, unless the same `gameId` was found at another path (then `zip_path` is updated). File-side problems are reported only; the checker never moves or deletes library files. ZIPs with `ZipUnreadable` / `InfoJsonMissing` / `InfoJsonInvalid` / `InfoJsonNewerSchema` keep their existing DB rows unchanged.
- **Self-repair:** `HashMismatch` / `EntryMissing` / `EntrySizeMismatch` can be fixed by ingesting a byte-identical copy (§7.2 step 3).
- **Retention:** at the end of each integrity run, delete `jobs` (and their `job_items`) and non-`Open` issues older than **180 days**.

## 13. Logging

- `nlog.config` in the API project; `Paths:Logs`, `Logging:MinLevel` and `Logging:RetentionDays` are injected as NLog variables at startup.
- **Targets:** console (single line, for `docker logs`) and file `${logDir}/rnrommanager-${shortdate}.log` with `maxArchiveDays = RetentionDays`.
- **Layout:** `${date:universalTime=true:format=o}|${level:uppercase=true}|${logger}|${threadid}|${scopeproperty:JobId}|${scopeproperty:FileId}|${aspnet-TraceIdentifier}|${message}${onexception:${newline}${exception:format=tostring}}` (`tostring` includes inner exceptions and stack traces).
- **Correlation:** `JobId` (every job), `FileId` (per ingestion item, a GUID also stored in `job_items`), HTTP `TraceIdentifier` (returned as `traceId` in ProblemDetails). Set with `ILogger.BeginScope`.
- Levels: `Logging:MinLevel` for `RnRomManager.*`; `Warn` for `Microsoft.*`/`System.*`, except `Microsoft.Hosting.Lifetime` at `Info`.

| Level | Events |
|---|---|
| Debug | each pipeline step + its structured properties (path, size, hashes, header fields, DAT result, chosen game, target ZIP); outbound HTTP (URL, status, duration); thumbnail 404s |
| Info | startup/version/redacted config; migrations applied; job start/end + counts; per-item outcome; system-folder mismatch; DB auto-repair changes; login success; user changes; library edits; DAT refresh result |
| Warn | duplicates; extension rename; failed logins + lockouts; HTTP retries; media fallback to default; integrity file issues; `Aborted` jobs at startup |
| Error | item `Error`s; ZIP verify failure; DB write failure after a ZIP rename; DAT download failure; unhandled request exceptions |
| Fatal | config/migration/startup failures |

- **Redaction:** passwords, cookies, antiforgery tokens and the `Admin:InitialPassword` value are never logged. Connection strings are only logged via `MySqlConnectionStringBuilder` with `Password` removed. Request/response bodies aren't logged.
- **Download logs** (`GET /api/admin/logs`, Admin): a ZIP of every `*.log` in `Paths:Logs` plus `settings.json` (redacted effective config) and `version.txt`. Built with `SafePath` and streamed.

## 14. Database

All tables InnoDB, `utf8mb4` / `utf8mb4_unicode_ci`. Times are `DATETIME(6)` UTC. GUIDs are `CHAR(36)`. JSON columns use the `JSON` type (stored as text; no JSON functions are queried). Column names avoid MySQL 8 reserved words (hence `system_id`, `trigger_source`).

### 14.1 Tables
**users** — `id` INT PK AI · `username` VARCHAR(64) UNIQUE · `password_hash` VARCHAR(255) · `role` VARCHAR(16) (`Admin`/`User`) · `is_disabled` BOOL · `must_change_password` BOOL · `failed_login_count` INT · `lockout_until` DATETIME NULL · `security_stamp` CHAR(32) · `created_at`, `updated_at`, `last_login_at` NULL

**games** — `id` CHAR(36) PK · `system_id` VARCHAR(16) · `title` VARCHAR(255) · `display_title` VARCHAR(255) · `zip_path` VARCHAR(512) UNIQUE (relative to `Paths:Roms`, `/` separators) · `bucket` CHAR(1) · `release_year` SMALLINT NULL · `developer`, `publisher` VARCHAR(255) NULL · `genres` JSON · `description` TEXT NULL · `has_default_boxart` BOOL · `zip_size_bytes` BIGINT · `zip_mtime_utc` DATETIME · `info_schema_version` INT · `created_at`, `updated_at`
Indexes: (`system_id`, `bucket`, `title`), (`title`), (`created_at`)

**roms** — `id` BIGINT PK AI · `game_id` FK→games ON DELETE CASCADE · `system_id` VARCHAR(16) · `file_name` VARCHAR(255) · `original_file_name` VARCHAR(255) · `base_title` VARCHAR(255) · `regions`, `languages`, `flags` JSON · `region_key` VARCHAR(255) (`|USA|Europe|`, with leading and trailing `|`; filter `LIKE '%|USA|%'`) · `revision` VARCHAR(32) NULL · `is_primary` BOOL · `id_method` VARCHAR(16) · `dat_name`, `dat_version`, `dat_game_name` NULL · `size_bytes` BIGINT · `crc32` CHAR(8) · `md5` CHAR(32) · `sha1` CHAR(40) UNIQUE · `header` JSON NULL · `date_added`, `date_modified`
Indexes: (`game_id`), (`system_id`, `base_title`), (`crc32`)

**images** — `id` BIGINT PK AI · `game_id` FK CASCADE · `file_name` VARCHAR(255) · `type` VARCHAR(16) · `source` VARCHAR(32) · `source_url` VARCHAR(1024) NULL · `is_default` BOOL · `sha1` CHAR(40) · `width`, `height` INT · `date_added` · UNIQUE (`game_id`, `file_name`)

**jobs** — `id` CHAR(36) PK · `type` VARCHAR(16) (`Ingest`/`Integrity`/`DatRefresh`) · `mode` VARCHAR(16) NULL (`Quick`/`Deep`) · `trigger_source` VARCHAR(16) (`Manual`/`Schedule`) · `status` VARCHAR(16) (`Running`/`Completed`/`Cancelled`/`Failed`/`Aborted`) · `requested_by` INT NULL FK→users · `started_at`, `finished_at` NULL · `summary` JSON NULL (counts) · `error` TEXT NULL · index (`type`, `started_at`)

**job_items** — `id` BIGINT PK AI · `job_id` FK CASCADE · `file_id` CHAR(36) · `system_id` VARCHAR(16) NULL · `source_path` VARCHAR(1024) · `archive_entry` VARCHAR(512) NULL · `outcome` VARCHAR(16) · `reason` VARCHAR(64) NULL (code) · `details` TEXT NULL · `game_id` CHAR(36) NULL · `sha1` CHAR(40) NULL · `created_at` · index (`job_id`, `outcome`)

**ingest_review_items** — `id` BIGINT PK AI · `system_id` VARCHAR(16) · `source_path` VARCHAR(1024) · `archive_path` VARCHAR(1024) NULL · `archive_entry` VARCHAR(512) NULL · `size_bytes` · `crc32`, `md5`, `sha1` (UNIQUE) · `header` JSON NULL · `suggestions` JSON · `status` VARCHAR(16) (`Pending`/`Resolved`/`Dismissed`) · `game_id` NULL · `created_at`, `last_seen_job_id`, `resolved_at` NULL, `resolved_by` INT NULL

**integrity_issues** — fields per §12.2 · `id` BIGINT PK AI · index (`status`, `type`), (`zip_path`)

**data_protection_keys** — `id` INT PK AI · `friendly_name` VARCHAR(255) · `xml` TEXT · `created_at` (§9.2)

**schema_versions** — DbUp journal.

### 14.2 Search & sync
- **Search:** `display_title LIKE CONCAT('%', @q, '%') OR title LIKE …` (with `%`/`_` escaped). This replaces the earlier FULLTEXT plan (D26).
- **Sync rule:** every library write = ZIP first (§6.1), then one DB transaction that deletes and re-inserts the game's `roms`/`images` and upserts `games`. The DB is never written without a ZIP that matches it. Gaps are healed by integrity (§12) and crash recovery (§7.4).

### 14.3 Migrations
- Scripts `src/RnRomManager.Data/Migrations/NNNN_description.sql`, embedded, applied in name order at startup. Forward-only; applied scripts are never edited. One logical change per script (MariaDB DDL commits implicitly, so transactions can't roll DDL back).
- Failure → `Fatal` + exit 1; the container restarts and retries. Back up the DB before upgrading (docs/deployment.md). Downgrades aren't supported.

## 15. Testing

### 15.1 What must be tested
| Component | Tests |
|---|---|
| GB header extractor | every field, both checksums (valid/invalid), CGB/SGB variants, unknown table values, MBC2 RAM, MBC1 multicart detection, size mismatch, < 0x150 bytes, random garbage |
| DAT parser | header/version, games with/without region, quoted names containing `(`, metadat repeatable fields, malformed input |
| Naming | base title, displayTitle article rule, sanitize (every illegal char, reserved names, length), bucket, clash suffixes, §6.1.1 tag parsing (ROM name ≠ game name, `(SGB Enhanced, GB Compatible)`, `(Proto 2)`, `(v1.0.3)`), revision order, primary ranking incl. `Unverified` |
| Thumbnail key | escaping of every special char + URL encoding |
| Suggestions | scoring, threshold, size boost, top-5 |
| Ingestion pipeline (temp dirs) | every outcome row of §7.2 incl. `Repaired`, archive fates + limits + traversal, min file age, global dedupe, review-row lifecycle on re-runs, new release into an existing ZIP, unreadable/newer-schema target ZIP, cancellation mid-archive, crash-recovery cleanup, review resolution actions (suggestion → `Unverified`) |
| ZIP writer | atomic write, verify failure leaves the original intact, `info.json` round-trip, schema upgrade chain, newer-schema refusal |
| Integrity | each issue type, quick-vs-deep, DB repair (skips `DuplicateGameId`/`DuplicateSha1`), issue dedup/resolve, retention purge |
| Auth | lockout + reset, login rate limit, last-admin rule, security stamp invalidation, must-change gate, antiforgery required, Data Protection keys survive a restart (Testcontainers) |
| CSP | inline-script hashes computed from a sample `index.html` |
| Repositories (Testcontainers) | migrations from scratch, CRUD, unique constraints, search escaping |
| API (`WebApplicationFactory`) | role matrix for every endpoint (401/403), validation errors, pagination, downloads (headers), 409 job lock |
| UI (Vitest) | library filters ↔ URL, theme toggle, fetch wrapper (XSRF header, 401 redirect), review form validation |
| E2E (Playwright + axe) | login + forced password change, ingestion of a seeded set, browse/search/filter, game detail + download, review resolve, Admin edit/move/delete |

- Rule: every non-trivial class has unit tests; behaviour changes need a test before merge. Coverage reported in CI without a gate (D21).

### 15.2 Synthetic ROMs & fixture DAT (no copyrighted ROMs)
- `RnRomManager.TestData` holds `SyntheticRomBuilder`: deterministic bytes from a seed; sets title, CGB/SGB flags, cart type, ROM/RAM size, licensee, destination and the Nintendo logo (48-byte bitmap from Pan Docs, needed for `logoValid`); computes both checksums; options to corrupt either checksum, the logo or the size.
- `FixtureDatWriter` writes clrmamepro DATs (no-intro + the 4 metadat) for a named ROM set, using the real hashes of the generated ROMs, with versions like `fixture-1`.
- **Built-in sets:** `Basic` (3 GB + 2 GBC games, multiple regions/revs, one article title, one dual-mode cart in `ingest/gb/`, one duplicate, one unmatched → review, one invalid header, one zip + one 7z archive) and `Full` (`Basic` + 200 generated games for paging).
- CLI: `dotnet run --project tests/RnRomManager.TestData -- --ingest <dir> --cache <dir> --set Basic|Full`.

### 15.3 CI integration
- Testcontainers runs on GitHub's `ubuntu-latest` (Docker preinstalled); image `mariadb:11.4`.
- E2E: `scripts/test-e2e.ps1` builds the image (local arch), seeds `Basic` into temp ingest/cache dirs, starts `tests/e2e/docker-compose.e2e.yml` (mariadb + app with those dirs mounted, `Admin__InitialPassword` set, `Sources__ThumbnailBaseUrl` empty), waits for `/health/ready`, runs Playwright, and always tears down. With media fetch disabled the default-art path is exercised and nothing hits the network. Thumbnail HTTP behaviour is covered by API tests with a stub `HttpMessageHandler`. Traces/screenshots are uploaded as artifacts on failure.

### 15.4 Manual test-ingest workflow
`seed-ingest.ps1` → files land in `./.dev/ingest` and fixture DATs in `./.dev/cache/dats` → `start-dev.ps1` (paths point at `./.dev/*`) → Ingestion page → Start.

## 16. Dev tooling (`/scripts`, PowerShell 7, cross-platform)

All scripts: `$ErrorActionPreference = 'Stop'`, non-zero exit on failure, run from any directory (resolve the repo root from `$PSScriptRoot`).

| Script | Parameters | Does |
|---|---|---|
| `setup-dev.ps1` | `-SkipDb` | Checks .NET 10 SDK, Node 24 LTS, Docker. `dotnet restore`, `npm ci` (Web), Playwright browsers. Starts MariaDB via `docker-compose.dev.yml` (port 3306, db/user `rnrommanager`). Creates `./.dev/{roms,ingest,logs,cache}` and `appsettings.Development.json` pointing there (git-ignored). |
| `start-dev.ps1` | `-ApiOnly`, `-WebOnly` | `dotnet watch` API on :5080 + Vite dev server (proxy `/api`, `/health` → API); Ctrl+C stops both |
| `test-all.ps1` | `-SkipIntegration` (no Testcontainers), `-SkipWeb`, `-Coverage` | `dotnet test` (+ coverlet + ReportGenerator HTML into `./.dev/coverage`), `npm run lint`, `npm run check`, `npm test` |
| `test-e2e.ps1` | `-SkipBuild`, `-Headed` | §15.3 |
| `build-docker.ps1` | `-Tag` (default `dev`), `-Platform` (default local), `-Version` (default `minver` output) | `docker buildx build --load -t niemandr/rnrommanager:<Tag>` |
| `seed-ingest.ps1` | `-Path` (default `./.dev/ingest`), `-CachePath` (default `./.dev/cache`), `-Set Basic\|Full`, `-Clean` | Runs the TestData CLI (§15.2); `-Clean` empties the ingest folder first |

## 17. Container

### 17.1 Image
- Multi-stage Dockerfile. `node:24` and `sdk:10.0` stages run on `$BUILDPLATFORM`: UI build → `dotnet publish -a <arch> -p:Version=$VERSION` (UI copied into `wwwroot`), where `<arch>` maps Docker's `$TARGETARCH` to .NET's names: `amd64` → `x64`, `arm64` → `arm64` (.NET rejects `amd64`). Runtime = `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` (D17). Cross-compiling instead of building under QEMU.
- The runtime stage creates `/app/{roms,ingest,logs,cache}` owned by `1654:1654` (`COPY --chown`), so new named volumes inherit writable ownership.
- `USER 1654`, `EXPOSE 8080`, `ENTRYPOINT ["/app/RnRomManager.Api"]`, `VOLUME` the four paths.
- `HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD ["/app/RnRomManager.Api", "--healthcheck"]`. That mode is handled first in `Main`, before the host is built (no config validation, NLog or DB): it GETs `http://127.0.0.1:8080/health/live` with a 3 s timeout and exits 0/1 (no shell or curl needed).
- `/health/live` (process up, anonymous) and `/health/ready` (DB reachable + paths writable; 503 otherwise).
- `.dockerignore` excludes `.git`, `node_modules`, `bin/obj`, `.dev`, tests.

### 17.2 Volumes & permissions
- Named volumes: work out of the box (pre-owned dirs).
- Bind mounts: the host dirs must be writable by the container user. Either `chown -R 1654:1654 <dirs>` or run as your own user with `user: "1000:1000"`. The startup write probe fails fast, naming the path and UID (§4).
- `TZ` controls the schedule time zone (tzdata is included in `-extra`).

### 17.3 Example `docker-compose.yml` (goes in README + docs/deployment.md)
```yaml
services:
  rnrommanager:
    image: niemandr/rnrommanager:latest
    restart: unless-stopped
    ports: ["8080:8080"]
    environment:
      ConnectionStrings__Default: "Server=mariadb;Database=rnrommanager;User=rnrommanager;Password=${DB_PASSWORD}"
      Admin__InitialPassword: "${ADMIN_PASSWORD}"
      TZ: "Europe/London"
    volumes:
      - ./data/roms:/app/roms
      - ./data/ingest:/app/ingest
      - ./data/logs:/app/logs
      - ./data/cache:/app/cache
    user: "1000:1000"   # owner of ./data
    depends_on: [mariadb]
  mariadb:
    image: mariadb:11.4
    restart: unless-stopped
    environment:
      MARIADB_DATABASE: rnrommanager
      MARIADB_USER: rnrommanager
      MARIADB_PASSWORD: "${DB_PASSWORD}"
      MARIADB_RANDOM_ROOT_PASSWORD: "1"
    volumes: ["./data/db:/var/lib/mysql"]
```

### 17.4 Upgrades
Pull the new tag and restart. DbUp applies new migrations on start; `info.json` files upgrade lazily (§6.2). Back up the DB and `roms/` first; there's no downgrade path.

## 18. CI/CD (GitHub Actions)

**`ci.yml`** — on `pull_request`, `push` (`branches-ignore: [main]`, no tags; `main` and tags are tested through `release.yml`, so nothing runs twice) and `workflow_call`:
| Job | Steps |
|---|---|
| backend | checkout (`fetch-depth: 0` for MinVer) → setup-dotnet 10 → `dotnet restore` → `dotnet build -c Release --no-restore -warnaserror` → `dotnet test --no-build --collect "XPlat Code Coverage"` (incl. Testcontainers) → ReportGenerator summary to the job summary |
| web | setup-node 24 + npm cache → `npm ci` → `npm run lint` (eslint + prettier check) → `npm run check` (svelte-check) → `npm test` → `npm run build` |
| e2e | needs backend + web → `pwsh scripts/test-e2e.ps1` → upload Playwright report on failure |

**`release.yml`** — on `push` to `main` and tags `v*`:
1. `uses: ./.github/workflows/ci.yml` (all tests must pass).
2. checkout (`fetch-depth: 0`) → `dotnet tool run minver` (tool manifest) → `VERSION`.
3. `setup-buildx-action` (no QEMU: the runtime stage only copies files) → `login-action` (Docker Hub).
4. `docker/metadata-action` tags: main → `edge`, `sha-<7>`; `vX.Y.Z` tag → `X.Y.Z`, `X.Y`, `latest` (D27).
5. `build-push-action`: `platforms: linux/amd64,linux/arm64`, `build-args: VERSION`, `cache-from/to: type=gha`.
6. Tag builds only: `peter-evans/dockerhub-description` pushes `README.md` as the Docker Hub description.

- **Secrets:** `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN` (Docker Hub PAT with Read/Write/Delete; the description update needs it).
- **Versioning:** MinVer, `MinVerTagPrefix=v`. Untagged commits get `X.Y.Z-alpha.0.N` (logged at startup, shown on About). Releases are made by pushing an annotated `vX.Y.Z` tag on `main`.

## 19. Documentation

| Doc | Content | Updated when |
|---|---|---|
| `README.md` | what it is, screenshots, quick start (compose §17.3), legal disclaimer, attribution | user-visible feature or deployment change |
| `docs/deployment.md` | compose, volumes/permissions, reverse proxy + forwarded headers, TZ, backups, upgrades | §17 changes |
| `docs/configuration.md` | the §4 table | any config key added/changed |
| `docs/features.md` | user guide per page | UI behaviour changes |
| `docs/zip-format.md` | §6 incl. the `info.json` field tables + version history | any schema change (bump `schemaVersion` + upgrader) |
| `docs/adding-a-system.md` | §22 contract walkthrough | contract changes |
| `docs/development.md` | scripts (§16), test data (§15.2), architecture | tooling changes |
| `CONTRIBUTING.md` | branch/PR rules, test rule, doc rule | rarely |
| `LICENSE` | MIT | — |

- Rule: the PR that implements or changes a behaviour updates its doc(s); the PR author owns that. Spec changes go into this file + §21.

## 20. Open questions

None. New questions found during implementation get added here.

## 21. Decisions log

| # | Date | Decision |
|---|---|---|
| D1 | 2026-11-04 | GBC-only (`0xC0`) and dual-mode (`0x80`) carts are stored under `roms/gbc/`; original GB under `roms/gb/`. Header CGB flag decides, not the extension. *(Refined by D23.)* |
| D2 | 2026-11-04 | ROMs with no DAT match go to an Admin **Review queue** in the UI (file stays in ingest until resolved). |
| D3 | 2026-11-04 | Source files are **deleted** after the ZIP is written and hash-verified. |
| D4 | 2026-11-04 | Roles: `Admin` + `User`. |
| D5 | 2026-11-04 | Per-system ingest folders; header overrides the folder if they disagree. |
| D6 | 2026-11-04 | `.zip` and `.7z` archives in ingest are unpacked and each ROM inside ingested (SharpCompress for 7z). |
| D7 | 2026-11-04 | Uppercase `A`–`Z` + `#` buckets; bucket = first char of ZIP name. *(Article handling: D24.)* |
| D8 | 2026-11-04 | Bootstrap Icons primary, Font Awesome Free for gaps. |
| D9 | 2026-11-04 | Hacks/translations go into the parent game ZIP, flagged in `info.json`. |
| D10 | 2026-11-04 | Integrity: DB auto-repaired from `info.json`; file problems reported only. Quick scheduled check + deep re-hash on demand. |
| D11 | 2026-11-04 | Docker image `linux/amd64` + `linux/arm64`, non-root, no PUID/PGID. |
| D12 | 2026-11-04 | MinVer (git-tag versions) + MIT license. |
| D13 | 2026-11-04 | Theme preference per browser (`localStorage`), default auto. |
| D14 | 2026-11-04 | App uses its own database on the shared MariaDB, no table prefix. |
| D15 | 2026-11-04 | DbUp, SvelteKit static SPA, Testcontainers MariaDB accepted. |
| D16 | 2026-11-04 | UI tests: Vitest + Playwright smoke tests in CI. |
| D17 | 2026-11-04 | Runtime base `aspnet:10.0-noble-chiseled-extra`. |
| D18 | 2026-11-04 | DATs from libretro-database `master`, manual "Refresh DATs" by Admin; DAT version recorded per ROM. |
| D19 | 2026-11-04 | Logs kept 14 days (configurable); Admin "Download logs" button. |
| D20 | 2026-11-04 | Quick integrity check daily at 03:00 (configurable); never concurrent with ingestion. |
| D21 | 2026-11-04 | C# tests: NUnit constraints, real temp dirs, coverage reported without a gate. |
| D22 | 2026-11-04 | `info.json` schema v1, solution layout and progress polling accepted as drafted. *(v1 fields finalised in §6.2 before any release.)* |
| D23 | 2026-11-04 | libretro DATs have no parent/clone data, so grouping = same system + base title (name before the first ` (`). Admin can move a release to another or a new game; the moved ROM keeps its base title so later releases follow it. A DAT match sets the system (refines D1; the header decides only for unmatched files; CGB rule = bit 7). |
| D24 | 2026-11-04 | ZIP names keep the No-Intro article form (`Legend of Zelda, The - …` → `L/`); the UI shows `displayTitle` with the article moved to the front. |
| D25 | 2026-11-04 | `datVersion` = DAT header `version`, not a commit SHA; DATs come from raw.githubusercontent.com (no GitHub API). |
| D26 | 2026-11-04 | Search uses `LIKE` on titles instead of FULLTEXT (small table; avoids min-token/stopword settings we can't change on a shared server). Header stored as JSON in `roms.header` instead of flattened columns (keeps systems pluggable). `ingest_runs`/`integrity_runs` merged into `jobs` (one job lock and status endpoint). |
| D27 | 2026-11-04 | Docker tags: `main` → `edge` + `sha-<7>`; `vX.Y.Z` tags → `X.Y.Z`, `X.Y`, `latest`. |
| D28 | 2026-11-04 | MVP library edits (Admin): edit metadata, move release, delete release/game. Removed ROMs go to `ingest/<system>/_removed/`, never hard-deleted. |
| D29 | 2026-11-04 | Defaults applied in the gap review: global SHA1 dedupe; 10 s min file age; archive limits 100 entries / 256 MiB; ROM max 16 MiB; `Error` outcome leaves the source for retry; password 12–128 chars, lockout 5/15 min, 7-day sliding session, security stamp; SPA antiforgery via `XSRF-TOKEN` cookie + `X-XSRF-TOKEN` header; generated admin password goes to the console only; `--healthcheck` mode + `/health/live`/`/health/ready`; cross-compiled multi-arch build; lazy `info.json` schema upgrades. |
| D30 | 2026-11-04 | Picking a DAT suggestion for a file whose hashes don't match records the release as **unverified**: `method = Manual`, flag `Unverified`, `fileName` + ` (Unverified)`, never ranked above a verified dump. Ingesting a byte-identical copy of a library ROM with an open `HashMismatch`/`EntryMissing`/`EntrySizeMismatch` issue **repairs** the entry (outcome `Repaired`). |
| D31 | 2026-11-04 | Defaults applied in the second gap review: tags parsed from the DAT ROM name (§6.1.1) with defined revision order; `MinRomSizeBytes` on `ISystemHandler`; Data Protection keys in DB table `data_protection_keys`; CSP `script-src` hashes computed by the API from `index.html`, theme script moved to `/theme.js`; Docker `TARGETARCH` → .NET arch mapping; `--healthcheck` handled before host build; integrity DB repair skips `DuplicateSha1` ZIPs; review rows auto-resolve/delete on re-runs; `TargetZipUnreadable` / `InfoJsonNewerSchema` → `Error`; cancel waits for a whole archive; 180-day retention for jobs + closed issues; usernames `[A-Za-z0-9._-]{3,64}` case-insensitive; login rate limit 10/min/IP; admin reset clears lockout + forces change; `region_key` pipe-delimited both ends; `regions` on `/api/systems`; `Sources:DatBaseUrl` / `Sources:ThumbnailBaseUrl` config (empty thumbnails = offline e2e); `ci.yml` skips `main` pushes; single instance per DB; `info.json` field `region` → `regions`. |
## 22. Extensibility — `ISystemHandler`

```csharp
public interface ISystemHandler
{
    string Id { get; }                       // "gb" — folder name, info.json `system`, DB value
    string DisplayName { get; }              // "Game Boy"
    string Family { get; }                   // "gameboy" — handlers sharing ingest folders/classification
    IReadOnlyList<string> RomExtensions { get; } // [".gb"]
    long MinRomSizeBytes { get; }            // 0x150 for GB/GBC (end of header)
    long MaxRomSizeBytes { get; }
    string DatName { get; }                  // "Nintendo - Game Boy" — DAT file names + thumbnail folder
    ReadOnlyMemory<byte> DefaultBoxArtPng { get; }
    RomHeader? ReadHeader(ReadOnlySpan<byte> rom);   // null = unreadable
    ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => rom; // e.g. SNES copier-header strip
}

public sealed record RomHeader(
    string? ClassifiedSystemId,              // which handler Id the header says (gb/gbc)
    string? Title,                           // for suggestions
    bool IsPlausible,                        // false → InvalidHeader when unmatched
    IReadOnlyDictionary<string, object?> Fields); // serialised to info.json `header` / roms.header
```

- Registered with `services.AddSystemHandler<T>()`. Core enumerates handlers for folders, DATs, extensions and limits; nothing in Core/Data/UI is GB-specific. DB `system_id` is a `VARCHAR` (no enum migrations), and `/api/systems` drives the UI filters.
- Adding SNES/GBA = a new `RnRomManager.Systems.X` project + registration + docs/tests; no core changes. Extracted ROMs are hashed after `NormalizeForHashing`.

## 23. API

JSON, camelCase. Errors are RFC 7807 ProblemDetails with an extra `code` (e.g. `JobRunning`, `DatsMissing`, `LastAdmin`, `ValidationFailed`) and `traceId`; validation errors include `errors{field:[…]}`. Lists use `?page=1&pageSize=48` (max 200) → `{ items, page, pageSize, total }`. Every `/api` route except login/antiforgery needs auth; mutations need the XSRF header (§9.2).

| Method | Route | Role | Request → Response |
|---|---|---|---|
| GET | `/api/auth/antiforgery` | anon | → 204, sets `XSRF-TOKEN` |
| POST | `/api/auth/login` | anon | `{username,password}` → `{user}` · 401 `InvalidCredentials` · 429 `TooManyRequests` |
| POST | `/api/auth/logout` | auth | → 204 |
| GET | `/api/auth/me` | auth | → `{id,username,role,mustChangePassword}` |
| POST | `/api/auth/change-password` | auth | `{currentPassword,newPassword}` → 204 · 400 |
| GET | `/api/users` | Admin | → `[{id,username,role,isDisabled,lastLoginAt}]` |
| POST | `/api/users` | Admin | `{username,role,password}` → 201 `{user}` (must change on first login) · 409 `UsernameTaken` |
| PATCH | `/api/users/{id}` | Admin | `{role?,isDisabled?}` → `{user}` · 409 `LastAdmin` |
| POST | `/api/users/{id}/reset-password` | Admin | `{newPassword}` → 204 |
| GET | `/api/systems` | auth | → `[{id,displayName,gameCount,romCount,regions[]}]` (`regions` = distinct ROM regions, sorted) |
| GET | `/api/games` | auth | `?system&letter&region&q&sort=title\|year\|added&dir=asc\|desc&page&pageSize` → page of `{id,system,displayTitle,releaseYear,regions[],romCount,boxArtUrl}` |
| GET | `/api/games/{id}` | auth | → game incl. `roms[]` (with `header`) and `images[]` (with `url`) |
| GET | `/api/games/{id}/images/{imageId}` | auth | → `image/png`, `ETag` = sha1, `Cache-Control: private, max-age=86400` |
| GET | `/api/games/{id}/download` | auth | → ZIP file (range support, `Content-Disposition` with RFC 5987 `filename*`) |
| GET | `/api/games/{id}/roms/{romId}/download` | auth | → ROM bytes streamed from the ZIP |
| PATCH | `/api/games/{id}` | Admin | `{title?,releaseYear?,developer?,publisher?,genres?,description?}` → game · 409 |
| DELETE | `/api/games/{id}` | Admin | → 204 (ROMs → `_removed/`) |
| DELETE | `/api/games/{id}/roms/{romId}` | Admin | → 204 (last ROM → game deleted) |
| POST | `/api/games/{id}/roms/{romId}/move` | Admin | `{targetGameId}` or `{newGameTitle}` → `{targetGameId}` |
| POST | `/api/games/{id}/media/retry` | Admin | → game |
| GET | `/api/ingest/pending` | Admin | → `[{system,files:[{name,sizeBytes,modifiedAt}]}]` |
| POST | `/api/jobs/ingest` | Admin | → 202 `{jobId}` · 409 `JobRunning` / `DatsMissing` |
| POST | `/api/jobs/integrity` | Admin | `{mode:"Quick"\|"Deep"}` → 202 `{jobId}` · 409 |
| POST | `/api/jobs/dat-refresh` | Admin | → 202 `{jobId}` · 409 |
| GET | `/api/jobs/current` | Admin | → `{id,type,mode,status,current,total,currentItem,startedAt}` · 204 when idle |
| GET | `/api/jobs` | Admin | `?type&page&pageSize` → page of jobs |
| GET | `/api/jobs/{id}` | Admin | → job incl. `summary` |
| GET | `/api/jobs/{id}/items` | Admin | `?outcome&page&pageSize` → page of `job_items` |
| POST | `/api/jobs/{id}/cancel` | Admin | → 202 · 404 if not running |
| GET | `/api/review` | Admin | `?status=Pending&page&pageSize` → page of review items |
| GET | `/api/review/{id}` | Admin | → item incl. header, hashes, suggestions |
| POST | `/api/review/{id}/resolve` | Admin | `{action:"Dat",datSystem,datGameName}` (→ `Unverified`, §7.5) · `{action:"Attach",targetGameId,flag:"Hack"\|"Translated"}` · `{action:"Manual",title,regions[],languages[],revision?,flags[]}` → `{gameId}` · 409 |
| POST | `/api/review/{id}/dismiss` | Admin | → 204 |
| GET | `/api/dat/search` | Admin | `?system&q&limit=20` → `[{gameName,romName,sizeBytes,crc32}]` |
| GET | `/api/integrity/status` | Admin | → `{lastQuick,lastDeep,nextScheduledAt}` |
| GET | `/api/integrity/issues` | Admin | `?status&type&page&pageSize` → page of issues |
| GET | `/api/admin/dats` | Admin | → `[{datName,version,downloadedAt}]` |
| GET | `/api/admin/settings` | Admin | → effective config, secrets masked |
| GET | `/api/admin/logs` | Admin | → ZIP (§13) |
| GET | `/api/about` | auth | → `{version,licenses[]}` |
| GET | `/health/live`, `/health/ready` | anon | → 200 / 503 |

- Unknown `/api/*` → 404 ProblemDetails; any other unknown path → `index.html` (SPA fallback).
