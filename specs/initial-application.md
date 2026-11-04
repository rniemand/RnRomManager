# RnRomManager — Initial Application Spec

> Status: **v0.11 — broken down into implementation steps in [initial-application-impl.md](initial-application-impl.md).** Seventh review done 2026-11-04 (consistency fixes only, D40); implementation decisions from D41 on. Confirmed decisions are listed in §21; open items in §20.

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
| Data access | Dapper + MySqlConnector. The app forces `DateTimeKind=Utc` and `GuidFormat=Char36` on the configured connection string via `MySqlConnectionStringBuilder` (users don't add them); all times stored UTC |
| DB | MariaDB 10.11+ (CI tests 11.4); MySQL 8.0+ best effort. External, shared instance; app uses its own database (e.g. `rnrommanager`), no table prefix. The database must already exist (the app never creates it); the app user needs `ALL` on that database only |
| Migrations | DbUp (`dbup-mysql` 6.x, which uses MySqlConnector) — embedded `.sql` scripts, applied on startup, journal table `schema_versions` (§14.3) |
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
- One in-process **global job lock** (`SemaphoreSlim(1)`). Background jobs: `Ingest`, `Integrity` (Quick/Deep), `DatRefresh`, `MediaRetry` (§10.2). Library edits and review resolutions (§7.5, §11) are synchronous request handlers that also take the lock.
- Lock busy → manual start returns `409 JobRunning`; scheduled integrity is skipped and logged at `Info`.
- Jobs persist to `jobs` (§14); live progress is held in memory and read via `GET /api/jobs/current`.
- Cancel: sets a flag checked between ingest files (an archive counts as one file: all its entries finish), between ZIPs (Integrity), between games (MediaRetry) and between downloads (DatRefresh, which then keeps the old set); status `Cancelled`. Work already done is kept; a cancelled Integrity run resolves no issues (§12.2).
- Status `Failed` = a job-level exception outside per-item handling (e.g. DAT download/parse failure, DB unreachable), stored in `jobs.error` and logged at `Error`. Per-item problems never fail the job (§7.2), except `DbWriteFailed` (§7.2 step 10). If the final job-row update itself fails (DB down), it's logged and the row stays `Running` until the next startup marks it `Aborted`; the in-memory lock is released either way.
- Progress: `total` = files in the snapshot (Ingest), ZIPs found (Integrity), candidate games (MediaRetry) or files to download (DatRefresh); `current` = items finished; `currentItem` = file name (`archive.zip › entry.gb` for archive entries; `Syncing library` during the ingest DB sync pre-pass, §7.1).
- **Shutdown (SIGTERM):** the running job's cancel flag is set; `HostOptions.ShutdownTimeout` = 30 s. A job that stops in time is `Cancelled`; otherwise the process exits and the row becomes `Aborted` at the next start (§7.4). The compose example sets `stop_grace_period: 35s` (Docker's default is 10 s).
- **Library ZIP replacement vs readers:** downloads and integrity open library ZIPs with `FileShare.Read | FileShare.Delete`, so the atomic `File.Move` (§6.1) also works on Windows dev machines while a download is streaming (Linux keeps the old inode). A move that still fails with `IOException` is retried 3× at 500 ms, then the item is `Error`.

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
| Integrity:QuickCheckTime | `Integrity__QuickCheckTime` | `03:00` | `HH:mm` or empty (= disabled); container local time (`TZ`). A run missed while the app was down is not caught up. DST: a skipped local time runs at the first valid minute after it; a repeated one runs once |
| Logging:MinLevel | `Logging__MinLevel` | `Debug` | NLog level name; applies to `RnRomManager.*` |
| Logging:RetentionDays | `Logging__RetentionDays` | `14` | 1–365 |
| Sources:DatBaseUrl | `Sources__DatBaseUrl` | `https://raw.githubusercontent.com/libretro/libretro-database/master/metadat` | absolute http(s) URL |
| Sources:ThumbnailBaseUrl | `Sources__ThumbnailBaseUrl` | `https://thumbnails.libretro.com` | absolute http(s) URL, or empty = media fetch disabled (default box art only; used by e2e, §15.3) |

- The four paths must be distinct and none may be inside another.
- `ValidateOnStart` (§4.1 step 1) checks only the static rules (required, absolute, distinct, ranges, formats). Folder creation and the write probe run in §4.1 step 4 with the same `Fatal` + exit 1 on failure.
- Write probe: create + delete `.rnrm-probe` in each path. Failure message names the path and the process UID/GID (§17.2).
- Behind a TLS reverse proxy set the built-in `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so `Secure` cookies and scheme work. It trusts `X-Forwarded-For` from any sender, so set it **only** when the port isn't reachable except through the proxy (otherwise the login rate limit, §9.1, can be bypassed). docs/deployment.md says so.
- The listen port is the ASP.NET default `ASPNETCORE_HTTP_PORTS` (image default `8080`); `--healthcheck` reads the first port from the same variable (§17.1).
- Everything else (limits, auth numbers, HTTP timeouts) is a **constant** in code, listed where used.
- Paths being configurable is what lets tests and manual testing point ingestion at a temp directory (§15.4).

### 4.1 Startup sequence
1. Load + validate config → 2. NLog → 3. log version + redacted effective config (`Info`) → 4. create folders (§5) + write probe (§4) → 5. wait for DB (unknown database or access denied → `Fatal` naming the database, exit 1) → 6. DbUp migrate (failure = exit 1) → 7. crash recovery (§7.4) → 8. seed admin (§9) → 9. load cached DATs (missing/corrupt file or `dats.json` → `Error`, that system counts as having no DATs) → 10. start the scheduler (integrity + daily log purge, §13) → 11. listen on `ASPNETCORE_HTTP_PORTS` (default `8080`).

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
- Bucket = first character of the ZIP file name, upper-cased if it is an ASCII letter (`seaQuest DSV.zip` → `S/`, `uCity.zip` → `U/`). ZIP names keep the No-Intro article form (`Legend of Zelda, The - …` → `L/`) (D24).
- The ingest folder is a hint only: the final system comes from §7.2 step 4. A GBC ROM dropped into `ingest/gb/` is stored under `roms/gbc/` (logged at `Info`).
- All system, letter and `_*` folders are created on startup if missing. Ingestion ignores subfolders (including `_*`), dot-files and OS junk files (`Thumbs.db`, `desktop.ini`, case-insensitive). Extensions match case-insensitively (`.GB` = `.gb`).
- **File-name comparisons are case-insensitive** everywhere (clash checks, `WrongLocation`, `zip_path`), matching the DB collation and keeping the library portable to Windows/macOS file systems.
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
| Base title | DAT game name up to the **region group**: the first ` (…)` group whose comma-separated parts are all known No-Intro regions (embedded list: `World`, `USA`, `Europe`, `Japan`, `Asia`, `Australia`, `Brazil`, `Canada`, `China`, `France`, `Germany`, `Hong Kong`, `Italy`, `Korea`, `Netherlands`, `Spain`, `Sweden`, `Taiwan`, `United Kingdom`, `Unknown`, plus the other No-Intro country names). Earlier groups belong to the title (`Yelu Wangzi (Prince Yeh Rude) (Taiwan)` → `Yelu Wangzi (Prince Yeh Rude)`). No region group → up to the first ` (`. Grouping key (D23). |
| Tags | Parsed from the **DAT ROM name** stem, not `game.name`: in 1,171 entries the ROM name adds tags (e.g. `(Aftermarket) (Unl)`). See §6.1.1. |
| Game `title` | Base title of the release that created the game; Admin-editable (1–255 chars after trim, must not sanitize to empty). Never auto-changes. |
| `displayTitle` | `title` with a trailing article moved to the front: `^(.*?), (The\|A\|An\|Die\|Der\|Das\|Le\|La\|Les\|El\|Il)( - .*)?$` → `$2 $1$3` (the DATs hold 212 English and ~15 German/French/Italian cases, e.g. `Maus, Die`). Shown in the UI. |
| ZIP name | `Sanitize(title) + ".zip"` |
| ROM `fileName` | DAT ROM name (DAT-matched), else §7.5 rules |
| Sanitize | Unicode NFC-normalise; replace `\ / : * ? " < > \|` and control chars with `_`; trim spaces/trailing dots; max 180 chars; Windows reserved names (`CON`, `NUL`, …) get a `_` prefix |
| ZIP name clash | Existing ZIP at the target path (case-insensitive) with a different `gameId` → append ` (2)`, ` (3)`, … |
| Entry name clash in a ZIP | Append ` (2)`… before the extension |
| Images | `images/boxart-01.png`, `images/snap-01.png`. Only one of each in MVP. |

- All releases (regions, revisions) **and** hacks/translations of the same game live in the same ZIP. Hacks/translations are flagged (`Hack`, `Translated`) and are never `isPrimary` (D9).
- **Known grouping limits (D35):** different games that share a base title are grouped together (e.g. nine Sachen `4 in 1 (Europe) (4B-00N, …)` multicarts); the Admin splits them with Move release → new game. The same game under different regional names (`Pocket Monsters - Aka` / `Pokemon - Red Version`) becomes separate games; the Admin combines them with **Merge into…** (§11). Since `title` never auto-changes, the merge target's title (normally the English one) is the one that's kept.
- **Primary release** (recomputed on every write; exactly one per game), ranked by: not Hack/Translated → no status flag (a flag starting with `Beta`, `Proto`, `Possible Proto`, `Demo`, `Sample`, `Pirate`, or `Unverified`) → region `World` > `USA` > `Europe` > other with `En` > `Japan` > other → highest revision (§6.1.1) → `fileName` ordinal.
- **Every write starts from the ZIP:** the current `info.json` and entries are read from the existing ZIP, never rebuilt from DB rows (the DB may be stale).
- **Writes are atomic:** stream existing entries + changes into `<name>.zip.tmp` in the target folder → re-open the tmp file and verify every ROM entry's SHA1 against `info.json` → `File.Move(tmp, final, overwrite: true)`. Never edit a ZIP in place. If the path changes (title edit), write at the new path, commit the DB, then delete the old ZIP.
- Compression: Deflate, `CompressionLevel.Optimal`. `info.json` is UTF-8 without a BOM, indented, camelCase, nulls written.
- **Default box art:** one embedded 512×512 PNG per system handler (generic cartridge silhouette with the system name, made for this project, MIT). Stored as `boxart-01.png` with `isDefault: true`.

#### 6.1.1 Tag parsing (No-Intro names)
Each `(…)` group after the base title is classified left to right. Groups are never split except regions and languages.
| Group | Rule | Example |
|---|---|---|
| region group (§6.1) | regions, split on `, ` | `(USA, Europe)` |
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
| developers | string[] | ✓ | may be empty; distinct, DAT order (§10.1) |
| publishers | string[] | ✓ | as above |
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
| sizeBytes | long | ✓ | entry size (checked by §9.3 / §12.1) |
| sha1 | hex | ✓ | |
| width / height | int | ✓ | read from the PNG IHDR (no image library) |
| dateAdded | ISO-8601 UTC | ✓ | |

- `info.json` is the **source of truth**; the DB is a rebuildable mirror (content only, no image bytes).
- Hashes are lowercase hex everywhere (DAT values are upper-case and are lower-cased when parsed).
- `roms[].metadata.dateModified` = last time that entry's bytes were (re)written (ingest or self-repair); `createdAt`/`updatedAt` = first / latest write of the ZIP.
- **Schema versioning:** readers accept `schemaVersion ≤ current`, upgrading older versions in memory through a chain of `IInfoJsonUpgrader` (vN→vN+1). The file is rewritten in the new version only on the next normal write. `schemaVersion > current` → read-only: integrity reports `InfoJsonNewerSchema` and ingestion/edits refuse to touch that ZIP.
- **Validation (`InfoJsonInvalid`)**, checked on every read after upgrading: valid UTF-8 JSON ≤ 1 MiB (§9.3); every ✓ field present with the right type; `gameId` a GUID; `system` a registered handler `Id`; `roms` ≥ 1 with exactly one `isPrimary`; `fileName`s unique case-insensitively within `roms`/`images` and free of `/`, `\` and `..`; hashes are lowercase hex of the right length; exactly one `BoxArt` image (none → `MissingBoxArt` instead) and ≤ 1 `Snap`. Unknown properties are ignored on read and dropped on the next write.

## 7. Ingestion pipeline

Started manually by an Admin from the UI (`POST /api/jobs/ingest`). Runs as a single background job (§3.1).

### 7.1 Job start
1. Take the job lock (busy → `409 JobRunning`).
2. DATs must be loaded for every registered system, else release the lock and return `409 DatsMissing`; no job row is created and nothing is downloaded (the Admin uses "Refresh DATs"; the Ingestion page shows this as its empty state).
3. Create a `jobs` row (`Ingest`, `Running`) and return `202`; the rest runs in the background.
4. **DB sync pre-pass:** for every library ZIP with no DB row or a different `zip_size_bytes`/`zip_mtime_utc`, and every `games` row whose ZIP is gone, apply the §12.2 DB auto-repair (same code, same skip rules). No issue rows are written; changes are logged at `Info`. This heals a DB left stale by a crash or a `DbWriteFailed` (§7.4), so grouping and dedupe never work from stale rows.
5. Delete `ingest_review_items` rows still `Pending` whose source file no longer exists (log `Info`).
6. Snapshot the files in every `ingest/<system>/` (non-recursive). Skip files modified < **10 s** ago (still copying; outcome `Skipped` `TooRecent`, file untouched). Files added later wait for the next run.

### 7.2 Per file (correlation scope `JobId` + `FileId`)
| # | Step | Failure → outcome, file fate |
|---|---|---|
| 1 | **Validate**: extension is a ROM ext of a handler in the folder's family (§22), `.zip` or `.7z`; ROM files (and every extracted ROM entry) between the handler's `MinRomSizeBytes` and `MaxRomSizeBytes` (§22); archives are limited by §7.3 | `Failed` `UnsupportedType` / `TooSmall` / `TooLarge` → `_failed/` |
| 1a | **Unpack** archives into `cache/work/<jobId>/<fileId>/` (§7.3). Each ROM entry continues as its own item from step 2. | `Failed` `ArchiveUnsafe` / `ArchiveCorrupt` / `ArchiveEncrypted` → `_failed/` |
| 2 | **Hash**: `NormalizeForHashing` (§22; identity for GB/GBC), then CRC32, MD5, SHA1 of the normalised bytes (one read pass). The normalised bytes are what gets stored. | IO error → `Error`, source untouched |
| 3 | **Global dedupe**: SHA1 already in `roms`. **Self-repair:** if that ROM's entry has an open `HashMismatch` / `EntryMissing` / `EntrySizeMismatch` issue (§12), rewrite the entry from this file (atomic write §6.1), mark the issue `Resolved`, outcome `Repaired`, delete the source. SHA1 equals a `Pending` review item from another source path → `Duplicate` (same source path → continues at step 4, the normal re-run). SHA1 equals a `Dismissed` review item → loose file: the row is reset to `Pending` with the new paths and continues at step 4; archive entry from the same `archive_path` → `Failed` `Dismissed` (§7.3). | `Duplicate` → `_duplicates/`, `Warn` with both paths + hashes |
| 4 | **Header + identify**: read the header from the normalised bytes with every handler of the folder's family (§22). Look up SHA1 in all of the family's DATs. Match → system = the matching DAT's system (D23). No match → system = header classification; if every `ReadHeader` returned null (headerless systems) → the ingest folder's handler, treated as plausible. | — |
| 5 | **No DAT match**: `RomHeader.IsPlausible == false` (GB/GBC: logo invalid **and** header checksum invalid) → `Failed` `InvalidHeader` → `_failed/`. Otherwise → `Review` `NoDatMatch`: upsert `ingest_review_items` (unique on SHA1) with paths, hashes, header, top-5 suggestions (§10.3); an existing row in any status is reset to `Pending` (`game_id`, `resolved_at`, `resolved_by` cleared); file stays in ingest. | |
| 6 | **Group**: games owning a `roms` row with the same `system_id` + `base_title` (case-insensitive). One → that game. None → a new game (`title` = base title). **More than one** (possible after a partial move, §11) → `Review` `AmbiguousGroup`: the review item stores the DAT identification and the candidate game IDs; the file stays in ingest (D32). | |
| 7 | **Metadata**: year/developers/publishers/genres from metadat by the CRC of the DAT entry the release is identified with (for `Unverified`, the chosen entry's CRC, §7.5). New game → all fields; existing game → only fields still null/empty (never overwrites a value). Manual releases get none. | missing data → nulls |
| 8 | **Media**: new game, or existing game whose box art `isDefault` → fetch box art + snap (§10.2, incl. the circuit breaker). | not found / HTTP failure → default box art, no snap, `Warn` |
| 9 | **Package**: atomic ZIP write + verify (§6.1) | verify/IO failure → `Error` (`VerifyFailed` / `IoError`), tmp deleted, source untouched. Verify covers copied entries too, so a corrupt existing entry blocks new releases for that game; `details` names the entry (fix: self-repair §7.2 step 3, or delete that release). Existing game's ZIP missing/unreadable → `Error` `TargetZipUnreadable`; newer schema → `Error` `InfoJsonNewerSchema` (source untouched) |
| 10 | **Mirror**: replace the game's DB rows in one transaction | DB failure → `Error` `DbWriteFailed`; ZIP stays; source **not** deleted; the job stops as `Failed` (remaining files untouched, so no later item groups against the stale DB). The next ingest pre-pass (§7.1 step 4) or integrity run repairs the DB |
| 11 | **Clean up**: delete the source (archive: §7.3) | delete failure → `Warn` (next run sees a `Duplicate`) |

- **Outcomes:** `Ingested`, `Repaired`, `Duplicate`, `Review`, `Failed` (file problem, moved to `_failed/` with `.error.txt`), `Error` (environment/unexpected problem; source left in place so the next run retries), `Skipped`. For archive fate (§7.3) `Repaired` counts as `Ingested`.
- **Review rows on re-runs:** files behind `Pending` review items stay in ingest and are re-processed every run (`last_seen_job_id` updated). If one now gets another outcome (e.g. it DAT-matches after a DAT refresh): `Ingested` → row `Resolved` with `game_id`, `resolved_by` null; any other outcome → row deleted.
- **Re-ingesting a ROM already in the library** is a `Duplicate` (or `Repaired`, step 3). To replace a release on purpose: delete it (→ `_removed/`), then ingest.
- Every item writes a `job_items` row. The job summary holds counts per outcome.
- An unexpected exception in one item never aborts the job; it becomes `Error` `Unexpected` and the next file continues.

### 7.3 Archives
- Limits (constants): ≤ **100** entries, ≤ **256 MiB** total uncompressed, each ROM entry ≤ handler `MaxRomSizeBytes`. Sizes are enforced on the actual bytes streamed (a limit stream), not only the declared sizes.
- Reject the whole archive (`ArchiveUnsafe`) on: an entry name that is absolute, contains `..` or resolves outside the work dir (§9.3), a symlink entry, or a limit breach.
- Non-ROM entries and nested archives are skipped and logged at `Info` (not extracted). Entries in subfolders are allowed: `archive_entry` keeps the full entry path, `originalFileName` is the file-name part.
- **Archive fate** after all its entries are processed: any entry `Review` or `Error` → archive stays; else any `Failed` → `_failed/` (the `.error.txt` lists every entry's outcome); else all `Duplicate` → `_duplicates/`; else deleted. A `Duplicate` whose SHA1 belongs to a review item `Resolved` from the same `archive_path` counts as `Ingested` here, so a fully resolved archive is deleted on the next run.
- A review item from an archive stores `archive_path` + `archive_entry`; resolution re-extracts that entry. Resolving or dismissing an archive item never moves the archive itself; the next ingest run re-processes the archive and applies the fate above (a dismissed entry becomes `Failed` `Dismissed` → archive to `_failed/`).

### 7.4 Crash recovery (startup)
- `jobs` rows still `Running` → `Aborted` (log `Warn`).
- Delete `cache/work/*` and every `*.zip.tmp` under `Paths:Roms`.
- This is safe because sources are deleted only after a verified ZIP write and a DB commit: a crash before the rename leaves the source → re-ingested; a crash after the rename but before the DB commit → the next ingestion's DB sync pre-pass (§7.1 step 4) or integrity run repairs the DB, and the source is then a `Duplicate`.

### 7.5 Review resolution (Admin, synchronous, takes the job lock)
| Action | Result |
|---|---|
| Pick a suggestion / DAT search result (`NoDatMatch` items) | Hashes differ from that DAT entry, so it's recorded as **unverified** (D30): `method = Manual`, `identification.dat*` set to the chosen entry, base title/tags from its ROM name + flag `Unverified`; `fileName` = DAT ROM stem + ` (Unverified)` + ext. Continues at step 6. |
| Attach to an existing game as `Hack` or `Translated` | `method = Manual`; `fileName` = `Sanitize(original stem) + " (Hack\|Translated)" + ext`; joins the chosen game |
| Attach as `None` (`AmbiguousGroup` items only; target must be one of the candidates) | Keeps the DAT identification (`method = Dat`, DAT `fileName`); joins the chosen game; continues at step 7 (fills only empty metadata fields) |
| Manual entry (title, regions, languages, revision, flags) | `method = Manual`; `fileName` = `Sanitize({title}{ ({regions joined ", "})}{ ({revision})}{ ({flag})…})` + ext, empty groups omitted; `baseTitle` = title; grouped by title (step 6; several candidates → the request fails `409 AmbiguousGroup`, use Attach) |
| Dismiss | Loose file → `_failed/` with reason `Dismissed`; archive entry → archive untouched (§7.3). Item `Dismissed` |

Before any action the source file (loose) or archive (entry) must still exist, else `409 SourceMissing` and the row is deleted. The ROM extension follows the resolved system's handler (`RomExtensions[0]`). A resolved item becomes `Resolved` with the `game_id`. Resolution runs steps 6–10 of §7.2 synchronously (step 6 is skipped for Attach, since the target game is chosen); on success the loose source file is deleted (step 11).

- **Known limit:** a `Manual`/`Unverified` release whose SHA1 appears in a later DAT is not upgraded automatically (its SHA1 is in `roms`, so re-ingesting is a `Duplicate`). Fix: delete the release (→ `_removed/`), move the file back into `ingest/<system>/`, ingest.

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
| Size ≠ header ROM size (small 8/16 KiB carts, overdumps, trimmed dumps) | `sizeMatchesHeader = false`; hash the file as-is (no trimming or padding, and no "try the trimmed prefix" lookup in MVP). No DAT match → review. |
| Header ROM size byte can't express the size (16 MiB aftermarket carts) | `romSizeBytes = null`, `sizeMatchesHeader = false`; identified by hash. |
| MBC1 multicart (MBC1M) | Size 1 MiB + MBC1 type + valid logo at `0x40104` → `isMbc1Multicart = true`; header fields come from bank 0. Identified by hash like any ROM. |
| Bad dump / garbage | Identified by hash only. Unmatched with an invalid logo **and** checksum → `InvalidHeader` (§7.2 step 5). |
| Copier header (512 bytes) | Not a GB convention; no stripping. Such files won't DAT-match → review. |
| Extension ≠ classification | DAT match: the DAT ROM name decides. Unmatched: renamed to the classified handler's `RomExtensions[0]`, `Warn`. |

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
- **Password recovery (CLI, D37):** `docker exec <container> /app/RnRomManager.Api --reset-password <username>` (no shell needed). Handled in `Main` before the web host: loads + validates config, waits for the DB (§4.1 steps 1, 2 and 5; no migrations), then sets a random 20-char password printed once via `Console.Out`, clears the lockout, sets `must_change_password`, rotates `security_stamp` and re-enables the user. If no enabled Admin would remain otherwise, the user is also made `Admin`. Unknown user or DB failure → message on stderr, exit 1; success → exit 0. Logged at `Warn` (username only). Works while the app is running.
- While `must_change_password` is set, every API call except `me`, `change-password`, `logout`, `antiforgery` and `login` returns `403 PasswordChangeRequired` (D42: the SPA must still be able to fetch an XSRF token after a browser restart).

### 9.2 Session & CSRF
- Cookie auth: name `rnrm.auth`, HttpOnly, `SameSite=Strict`, `Secure` = same as request (§4 forwarded headers), persistent, sliding expiry **7 days**, no absolute limit. API returns 401/403 instead of redirecting.
- `users.security_stamp` is stored in the cookie and re-checked on every request. It changes on password change/reset, disable and role change, so existing sessions die. `change-password` then re-signs the **current** session with the new stamp (other sessions die) and re-issues the antiforgery token.
- **Anti-forgery for the SPA:** ASP.NET `IAntiforgery` with header `X-XSRF-TOKEN`. The SPA calls `GET /api/auth/antiforgery` once on boot; it sets the readable cookie `XSRF-TOKEN`; login, logout and change-password re-issue it (tokens are bound to the signed-in user). Every `POST/PUT/PATCH/DELETE` under `/api` (login included) must send the header, else `400 AntiforgeryInvalid`. The SPA fetch wrapper adds it automatically.
- **Data Protection keys** (they sign the auth and antiforgery cookies) are persisted in the DB table `data_protection_keys` through a small Dapper `IXmlRepository`, application name `RnRomManager`. Without this, every container restart would log everyone out. Keys aren't encrypted at rest (the DB is trusted).
- Response headers: `Content-Security-Policy: default-src 'self'; script-src 'self' <hashes>; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`. At startup the API computes a `'sha256-…'` hash of every inline `<script>` in `wwwroot/index.html` (SvelteKit's bootstrap) for `<hashes>`. `kit.csp` isn't used (it isn't reliable on the `adapter-static` fallback page). The theme script is an external `/theme.js` (§11).

### 9.3 File-path safety
- One helper, `SafePath.Resolve(root, relative)`: `Path.GetFullPath(Path.Combine(root, relative))` must start with `GetFullPath(root) + separator`, else it throws `UnsafePathException`. Every computed path goes through it: ZIP paths, ingest/_failed moves, archive extraction, work dirs, log download.
- The API never accepts a file path. Images and ROMs are addressed by `gameId` + DB id / `info.json` entry lookup, never by concatenating a name.
- Library ZIPs read by the app: `info.json` ≤ 1 MiB and every entry read through the same limit stream as §7.3 (actual bytes, not declared sizes); entry sizes must match `info.json`; entries outside `roms/`, `images/` and `info.json` are reported (`EntryUnlisted`), never extracted.
- Archive input limits: §7.3.

## 10. External data sources (free, no API key)

Verified live 2026-11-04: all 10 DAT files return 200 (GB DAT `2026.10.07`, 2,115 games; GBC DAT 2,433 games). The GB DAT holds only `.gb` and the GBC DAT only `.gbc` ROM names; all names are ASCII with no filesystem-illegal characters; 5 base titles start with a lowercase letter (`seaQuest`, `uCity`, …). Every game has exactly one ROM, there are no duplicate SHA1s across both DATs, and ROM sizes range from 8 KiB to 16 MiB. Thumbnail checks: `…Asteroids & Missile Command…` → 404, `…Asteroids _ Missile Command…` → 200, `Tetris (World)` → 404, `Tetris (World) (Rev 1)` → 200 (so the fallback order matters).

| Need | Source (`Sources:*` base URLs, §4) |
|---|---|
| Canonical name, region, revision, hashes | `{DatBaseUrl}/no-intro/<DatName>.dat` (`Nintendo - Game Boy`, `Nintendo - Game Boy Color`) |
| Year, developer, publisher, genre | `{DatBaseUrl}/{releaseyear,developer,publisher,genre}/<DatName>.dat` (matched by CRC) |
| Box art, snap | `{ThumbnailBaseUrl}/<DatName>/Named_Boxarts/<key>.png`, `…/Named_Snaps/<key>.png` |

### 10.1 DATs
- **Format:** clrmamepro text (not XML). The header `clrmamepro ( name, version, … )` gives `datName` + `datVersion`. Each `game ( name "…" [region "…"] [serial "…"] rom ( name "…" size N crc X md5 X sha1 X ) )`. Metadat files: `game ( comment "…" <field> "…" rom ( crc X ) )`. Unknown fields are ignored (`serial`, `releaseyear/month/day` in no-intro; `franchise`, `name`, `description` in metadat). **Several metadat blocks can share one CRC** (e.g. Pokémon: developers `Nintendo` + `Game Freak`): values are merged distinct in file order into `developers[]` / `publishers[]` / `genres[]`; for `releaseyear` the first wins. A metadat value is never split (`Music / Dancing`, `Hiro Co., Ltd.` stay whole). Metadat CRCs with no no-intro entry are ignored. Metadat headers have **no `version`** (`dats.json` stores null); `identification.datVersion` always comes from the no-intro DAT.
- **No `cloneof`/parent data** is present, so grouping is name-based (§6.1, D23).
- Regions and languages are parsed from the ROM-name tags (§6.1.1). The DAT `region` field is ignored because it's missing for World releases.
- Parsed into memory per system at startup and after a refresh (≈2–3k entries each): SHA1 → entry, CRC → metadat values, plus a name list for search/suggestions.
- **Refresh DATs** (Admin, `DatRefresh` job): downloads all 10 files (2 systems × no-intro + 4 metadat) to a temp dir → parses each → swaps them into `cache/dats/` all at once → writes `dats.json` (one entry per file: `{file, datName, version, downloadedAt, sha1}`). Any failure keeps the old set. On load each file's SHA1 is recomputed; a missing file, SHA1 mismatch or parse error = corrupt (§4.1 step 9). No automatic refresh (D18).
- **Offline:** cached DATs keep working. With no cache, ingestion fails fast with `DatsMissing` (§7.1).
- The GitHub API isn't used (unauthenticated limit of 60/h). Raw downloads only.

### 10.2 Thumbnails
- **Key:** the DAT `game.name` with each of these 11 characters replaced by `_`: ``&`` ``*`` ``/`` ``:`` `` ` `` ``<`` ``>`` ``?`` ``\`` ``|`` ``"``; then the path segment URL-encoded (`Uri.EscapeDataString`). Verified: `&` → 404, `_` → 200.
- **Lookup order:** the primary release's `datGameName` → other releases in primary-rank order (§6.1); releases without `datGameName` (Manual) are skipped, so an all-Manual game gets the default box art. The first 200 for each type wins.
- **Validation:** PNG signature, ≤ 5 MiB; dimensions read from IHDR.
- `Sources:ThumbnailBaseUrl` empty → no lookup; default box art, no snap (no `Warn`).
- **Circuit breaker:** after **3** consecutive network failures in one job (timeout, connection error, or 5xx/408 after the retries; a 404 doesn't count and any 200 resets the count), media lookups are skipped for the rest of that job: default box art, no snap, one `Warn`. A `MediaRetry` job whose breaker trips ends `Failed` (`error` = thumbnail host unreachable).
- "Retry media" (Admin, game detail) re-runs the lookup when the box art is the default or missing (`MissingBoxArt`) or the snap is missing.
- **`MediaRetry` job** (Admin, "Retry missing media" on the Integrity page; D38): the same lookup for every game with `has_default_boxart`, no `BoxArt` image, or no `Snap`, ordered by `title`. Games where every release is `Manual` are skipped. A game is rewritten (atomic write §6.1 + DB) only when something new was found. No `job_items`; `summary` = `{updated, notFound, skipped, error}`; per-game errors are logged at `Error` and the job continues. Takes the job lock (§3.1).

### 10.3 Suggestions & search (review queue)
- **Normalise:** uppercase; drop everything except `A–Z0–9` and spaces; collapse spaces.
- **Suggestions:** compare two inputs to every DAT base title of the classified system: the header title and the original file-name stem cut at the first ` (` or ` [` (often a No-Intro name). Score per input = max(token Jaccard, 1 − Levenshtein/maxLen on space-free strings); take the higher input, +0.1 when the DAT ROM size equals the file size. Empty inputs are skipped. Keep scores ≥ 0.4, top 5, stored with their score.
- **DAT search:** case-insensitive substring over game names of a chosen system, 20 results.
- Suggestions are computed when the item is created or re-seen by an ingest run, so they reflect the DATs loaded at that time.

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
- **Game typeahead** (move, merge, attach): `GET /api/games?q=…&system=<source system>&pageSize=10`, debounced 300 ms, ≥ 2 chars; the source game is excluded client-side.
- **Shared states:** loading = spinner/placeholder cards; empty = message + next action (Admin: link to Ingestion); error = dismissible alert with the ProblemDetails `title` + Retry; `409 JobRunning` = toast.
- **Navbar:** Library, plus for Admin: Ingestion, Review (badge = pending count), Integrity, Admin. User menu: Change password, Logout. Theme toggle.
- Times are shown in the browser's local time zone; sizes in KiB/MiB.
- Unknown route → "Page not found" with a link to the Library.

| Page | Route | Role | Behaviour |
|---|---|---|---|
| Login | `/login` | anon | username/password, generic error |
| Change password | `/change-password` | auth | current + new + confirm, policy hint |
| Library | `/` | auth | Grid (box art, 2/3/4/6 cols at xs/md/lg/xl) or list (table) toggle, remembered in `localStorage`. Filters: system, letter (`All`, `#`, `A–Z`), region (any release; options from `/api/systems` `regions`), search (debounced 300 ms, ≥2 chars). Sort: title (default, asc) / year (nulls last) / date added; tie-break `id`. 48 per page. Filters live in the URL query. |
| Game detail | `/games/{id}` | auth | Box art + snap, displayTitle, year/developers/publishers/genres. Releases table: fileName, region, revision, flags, primary badge, size, CRC32/SHA1 with copy buttons, expandable header. Download ZIP / single ROM. The header is shown as a generic key/value table from `header` (no system-specific UI). **Admin:** Edit metadata (title, year, developers, publishers, genres, description), Retry media, Move release (to an existing game via typeahead, or to a new game with a title), Merge into… (game typeahead), Delete release, Delete game — each behind a confirm modal. |
| Ingestion | `/ingestion` | Admin | Pending files per system (name, size, modified); Start / Cancel; progress bar (current/total + current file); last 10 runs → run detail. No DATs loaded → empty state with a link to Admin → DATs instead of Start. |
| Job detail | `/jobs/{id}` | Admin | Any job type: status, times, summary counts, error; ingest items table (filter by outcome) |
| Review queue | `/review` | Admin | Pending list (file, system, size, header title, reason, added). Detail: header + hashes; `NoDatMatch`: suggestions with scores, DAT search, attach-to-game (game typeahead + Hack/Translated), manual form; `AmbiguousGroup`: the DAT identity + candidate games, attach as a normal release; Dismiss. |
| Integrity | `/integrity` | Admin | Last quick/deep run, next scheduled, Run quick / Run deep / Retry missing media (§10.2), issues table (filter by status/type), history |
| Admin | `/admin/users`, `/admin/dats`, `/admin/settings`, `/admin/about` | Admin | Users (create, enable/disable, role, reset password); DAT versions + Refresh; effective settings (read-only, secrets masked) + Download logs; version, licences, attribution |

- **Library edits:** deleting a release/game moves the ROM bytes to `ingest/<system>/_removed/` (never hard-deleted) and rebuilds or deletes the ZIP. Moving a release rebuilds both ZIPs (an emptied game is deleted). The moved ROM keeps its `baseTitle`, so later releases with that base title follow it automatically when it's the only game with that base title (D23); if both games keep that base title, later releases go to review as `AmbiguousGroup` (D32). Moving to a new game runs §7.2 steps 7–8 for it (metadata + media from the moved ROM).
- **Merge into…** (D35): moves every release of the source game into the target in one write (target ZIP written + DB committed, then the source ZIP is deleted). Releases keep their `baseTitle`, so later releases with either base title join the target. The target keeps its title, metadata and images, except that empty target fields are filled from the source and a default target box art is replaced by a real source box art (the source snap is used only when the target has none). Move and merge targets must have the same `system_id` (else `400 ValidationFailed`).
- `displayTitle` is always derived from `title` (§6.1) and isn't editable on its own.
- Rom and image IDs change on every rewrite of a game (§14.2). A `404` on a rom/image action or download → the UI reloads the game detail and shows "This game changed, please retry".
- **Static files:** `/_app/immutable/*` → `Cache-Control: public, max-age=31536000, immutable`; `index.html` and other files → `no-cache`.
- **Responsive:** navbar collapses below `lg`; tables use `table-responsive`; usable from 360 px wide.
- **Accessibility:** WCAG 2.2 AA target. Every control is keyboard reachable with a visible focus ring. Labels on all inputs. Box art `alt` = "`displayTitle` box art". Progress region `aria-live="polite"`. Modals use Bootstrap focus trapping. Contrast checked in both themes. Playwright runs axe on each smoke page and fails on `serious`/`critical`.
- **Icons:** a single `Icon.svelte` maps semantic names (e.g. `download`, `ingest`, `review`, `integrity`, `system-gb`) to Bootstrap Icons classes. An unmapped `system-<id>` falls back to `bi-controller`, so a new system needs no UI change. Font Awesome only where BI has no match (e.g. the GitHub brand on About). Decorative icons get `aria-hidden="true"`; icon-only buttons need an `aria-label`.

## 12. Integrity checker

`Integrity` job: daily quick check at `Integrity:QuickCheckTime`, plus "Run quick" / "Run deep" (Admin). Shares the job lock with ingestion (§3.1); skipped when busy (D20).

### 12.1 Rules
| Mode | Per ZIP under `roms/<system>/` |
|---|---|
| Quick | If a DB row exists with equal `zip_size_bytes` + `zip_mtime_utc` (file mtime truncated to whole seconds, UTC; survives FS/SMB precision differences) → only check location/name. Else open the ZIP, parse `info.json`, compare the entry list + sizes to `info.json`, check box art, repair the DB. |
| Deep | Open every ZIP; all quick checks + recompute the SHA1 of every ROM and image entry. |

Both modes also: list orphan files (anything that isn't a `.zip` in the library tree, stale `*.zip.tmp`), check every DB `games` row still has its ZIP, and detect duplicate `gameId`s and SHA1s.
- **Scope:** every `.zip` at any depth under a registered system folder (`roms/<Id>/`) is checked; one not at its §5 location (e.g. `roms/gb/Tetris.zip`, wrong letter or system folder) → `WrongLocation`, DB repaired as normal. Files directly in `roms/` and everything under an unregistered folder (e.g. `roms/snes/`) → `OrphanFile`, never opened.
- `InfoJsonInvalid` = any §6.2 validation failure; `details` names the first failing rule.

### 12.2 Issue types
| Type | Severity | Auto-repair |
|---|---|---|
| `ZipUnreadable` | Error | — |
| `InfoJsonMissing` / `InfoJsonInvalid` | Error | — |
| `InfoJsonNewerSchema` | Warn | — |
| `EntryMissing` (listed, not in ZIP) / `EntrySizeMismatch` | Error | — |
| `EntryUnlisted` (in ZIP, not listed) | Warn | — |
| `HashMismatch` (deep) | Error | — |
| `MissingBoxArt` (`info.json` lists no `BoxArt` image; a listed but absent entry is `EntryMissing`) | Error | — |
| `DefaultBoxArt` | Info | — |
| `WrongLocation` (system/bucket/name ≠ §6.1; a clash suffix ` (N)` on the expected name is accepted) | Warn | — |
| `OrphanFile` | Warn | — |
| `DuplicateGameId` | Error | DB repair skipped for those ZIPs. `details` lists each path with its `metadata.updatedAt`; the usual cause is a crash during a title rename (§6.1), and the fix is deleting the older ZIP by hand |
| `DuplicateSha1` (same ROM in two games) | Warn | DB repair skipped for the later ZIP (`zip_path` ordinal), since `roms.sha1` is unique |
| `DbRowInserted` / `DbRowUpdated` / `DbRowDeleted` | Info | yes → recorded as `AutoRepaired` |

- **Fields:** see `integrity_issues` (§14.1).
- **Dedup:** an open issue with the same (`type`, `zip_path`, `entry_name`) is updated (`last_seen_job_id`), not duplicated. An open issue not seen again in a `Completed` run of the same or deeper mode → `Resolved` (`Cancelled`/`Aborted` runs resolve nothing).
- **DB auto-repair** (D10): game rows are replaced from `info.json` in one transaction. Rows whose ZIP is gone are deleted, unless the same `gameId` was found at another path (then `zip_path` is updated). File-side problems are reported only; the checker never moves or deletes library files. ZIPs with `ZipUnreadable` / `InfoJsonMissing` / `InfoJsonInvalid` / `InfoJsonNewerSchema` keep their existing DB rows unchanged.
- **Self-repair:** `HashMismatch` / `EntryMissing` / `EntrySizeMismatch` can be fixed by ingesting a byte-identical copy (§7.2 step 3).
- **Retention:** at the end of each integrity run, delete `jobs` (and their `job_items`) and non-`Open` issues older than **180 days**.

## 13. Logging

- `nlog.config` in the API project; `Paths:Logs`, `Logging:MinLevel` and `Logging:RetentionDays` are injected as NLog variables at startup.
- `builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Trace)` + `UseNLog()`: Microsoft.Extensions.Logging would otherwise default to `Information` and drop `Debug` events before NLog sees them. NLog rules do all filtering.
- **Targets:** console (single line, for `docker logs`) and file `${logDir}/rnrommanager-${date:universalTime=true:format=yyyy-MM-dd}.log` (one file per UTC day, no NLog archiving).
- **Retention:** the app deletes `rnrommanager-*.log` files in `Paths:Logs` older than `RetentionDays` at startup and daily at 00:05 UTC (scheduler, §4.1). Doing it in code avoids relying on NLog archive cleanup of date-based file names.
- **Layout:** `${date:universalTime=true:format=o}|${level:uppercase=true}|${logger}|${threadid}|${scopeproperty:JobId}|${scopeproperty:FileId}|${aspnet-TraceIdentifier}|${message}${onexception:${newline}${exception:format=tostring}}` (`tostring` includes inner exceptions and stack traces).
- **Correlation:** `JobId` (every job), `FileId` (per ingestion item, a GUID also stored in `job_items`), HTTP `TraceIdentifier` (returned as `traceId` in ProblemDetails). Set with `ILogger.BeginScope`.
- Levels: `Logging:MinLevel` for `RnRomManager.*`; `Warn` for `Microsoft.*`/`System.*`, except `Microsoft.Hosting.Lifetime` at `Info`.

| Level | Events |
|---|---|
| Debug | each pipeline step + its structured properties (path, size, hashes, header fields, DAT result, chosen game, target ZIP); outbound HTTP (URL, status, duration); thumbnail 404s |
| Info | startup/version/redacted config; migrations applied; job start/end + counts; per-item outcome; system-folder mismatch; DB auto-repair changes; login success; user changes; library edits; DAT refresh + media retry results; ingest DB sync pre-pass changes |
| Warn | duplicates; extension rename; failed logins + lockouts; CLI password reset; HTTP retries; media fallback to default; thumbnail circuit breaker tripped; integrity file issues; `Aborted` jobs at startup |
| Error | item `Error`s; ZIP verify failure; DB write failure after a ZIP rename; DAT download failure; unhandled request exceptions |
| Fatal | config/migration/startup failures |

- File target uses `keepFileOpen="true"` with `concurrentWrites="false"` (one process). `LogManager.Shutdown()` runs before every exit (incl. `Fatal` exit 1 and the CLI modes) so buffered lines are flushed.
- **Redaction:** passwords, cookies, antiforgery tokens and the `Admin:InitialPassword` value are never logged. Connection strings are only logged via `MySqlConnectionStringBuilder` with `Password` removed. Request/response bodies aren't logged.
- **Download logs** (`GET /api/admin/logs`, Admin): a ZIP of every `*.log` in `Paths:Logs` plus `settings.json` (redacted effective config) and `version.txt`. Built with `SafePath` and streamed; log files are opened with `FileShare.ReadWrite` because NLog may hold today's file open.

## 14. Database

All tables InnoDB, `utf8mb4` / `utf8mb4_unicode_ci`. Times are `DATETIME(6)` UTC. GUIDs are `CHAR(36)`. JSON columns use the `JSON` type (stored as text; no JSON functions are queried). Column names avoid MySQL 8 reserved words (hence `system_id`, `trigger_source`).

### 14.1 Tables
**users** — `id` INT PK AI · `username` VARCHAR(64) UNIQUE · `password_hash` VARCHAR(255) · `role` VARCHAR(16) (`Admin`/`User`) · `is_disabled` BOOL · `must_change_password` BOOL · `failed_login_count` INT · `lockout_until` NULL · `security_stamp` CHAR(32) · `created_at`, `updated_at`, `last_login_at` NULL

**games** — `id` CHAR(36) PK · `system_id` VARCHAR(16) · `title` VARCHAR(255) · `display_title` VARCHAR(255) · `zip_path` VARCHAR(512) UNIQUE, collation `utf8mb4_uca1400_as_ci` (case-insensitive but accent-sensitive, D41; relative to `Paths:Roms`, `/` separators) · `bucket` CHAR(1) · `release_year` SMALLINT NULL · `developers`, `publishers`, `genres` JSON · `description` TEXT NULL · `has_default_boxart` BOOL · `zip_size_bytes` BIGINT · `zip_mtime_utc` DATETIME(0) (whole seconds, §12.1) · `info_schema_version` INT · `created_at`, `updated_at`
Indexes: (`system_id`, `bucket`, `title`), (`title`), (`created_at`)

**roms** — `id` BIGINT PK AI · `game_id` FK→games ON DELETE CASCADE · `system_id` VARCHAR(16) · `file_name` VARCHAR(255) · `original_file_name` VARCHAR(255) · `base_title` VARCHAR(255) · `regions`, `languages`, `flags` JSON · `region_key` VARCHAR(255) (`|USA|Europe|`, with leading and trailing `|`; filter `LIKE '%|USA|%'`) · `revision` VARCHAR(32) NULL · `is_primary` BOOL · `id_method` VARCHAR(16) · `dat_name`, `dat_version`, `dat_game_name` VARCHAR(255) NULL (D41) · `size_bytes` BIGINT · `crc32` CHAR(8) · `md5` CHAR(32) · `sha1` CHAR(40) UNIQUE · `header` JSON NULL · `date_added`, `date_modified`
Indexes: (`game_id`), (`system_id`, `base_title`), (`crc32`)

**images** — `id` BIGINT PK AI · `game_id` FK CASCADE · `file_name` VARCHAR(255) · `type` VARCHAR(16) · `source` VARCHAR(32) · `source_url` VARCHAR(1024) NULL · `is_default` BOOL · `size_bytes` BIGINT · `sha1` CHAR(40) · `width`, `height` INT · `date_added` · UNIQUE (`game_id`, `file_name`)

**jobs** — `id` CHAR(36) PK · `type` VARCHAR(16) (`Ingest`/`Integrity`/`DatRefresh`/`MediaRetry`) · `mode` VARCHAR(16) NULL (`Quick`/`Deep`) · `trigger_source` VARCHAR(16) (`Manual`/`Schedule`) · `status` VARCHAR(16) (`Running`/`Completed`/`Cancelled`/`Failed`/`Aborted`) · `requested_by` INT NULL FK→users · `started_at`, `finished_at` NULL · `summary` JSON NULL (counts) · `error` TEXT NULL · index (`type`, `started_at`)

**job_items** — `id` BIGINT PK AI · `job_id` FK CASCADE · `file_id` CHAR(36) · `system_id` VARCHAR(16) NULL · `source_path` VARCHAR(1024) · `archive_entry` VARCHAR(512) NULL · `outcome` VARCHAR(16) · `reason` VARCHAR(64) NULL (code) · `details` TEXT NULL · `game_id` CHAR(36) NULL · `sha1` CHAR(40) NULL · `created_at` · index (`job_id`, `outcome`)

**ingest_review_items** — `id` BIGINT PK AI · `system_id` VARCHAR(16) · `reason` VARCHAR(16) (`NoDatMatch`/`AmbiguousGroup`) · `source_path` VARCHAR(1024) · `archive_path` VARCHAR(1024) NULL · `archive_entry` VARCHAR(512) NULL · `original_file_name` VARCHAR(255) · `size_bytes` BIGINT · `crc32` CHAR(8) · `md5` CHAR(32) · `sha1` CHAR(40) UNIQUE · `header` JSON NULL · `suggestions` JSON (`[{datGameName,romName,score}]`; empty for `AmbiguousGroup`) · `dat_identification` JSON NULL (`{datName,datVersion,datGameName,romName}`, `AmbiguousGroup` only) · `candidate_game_ids` JSON NULL (`AmbiguousGroup` only) · `status` VARCHAR(16) (`Pending`/`Resolved`/`Dismissed`) · `game_id` CHAR(36) NULL · `created_at` · `last_seen_job_id` CHAR(36) · `resolved_at` NULL · `resolved_by` INT NULL FK→users · index (`status`, `created_at`)

**integrity_issues** — `id` BIGINT PK AI · `type` VARCHAR(32) · `severity` VARCHAR(8) (`Info`/`Warn`/`Error`) · `system_id` VARCHAR(16) NULL · `zip_path` VARCHAR(512) NULL · `game_id` CHAR(36) NULL · `entry_name` VARCHAR(512) NULL · `details` TEXT NULL · `status` VARCHAR(16) (`Open`/`AutoRepaired`/`Resolved`) · `first_seen_job_id`, `last_seen_job_id` CHAR(36) · `detected_at`, `resolved_at` NULL · index (`status`, `type`), (`zip_path`)

**data_protection_keys** — `id` INT PK AI · `friendly_name` VARCHAR(255) · `xml` TEXT · `created_at` (§9.2)

**schema_versions** — DbUp journal (configured with `JournalToMySqlTable(db, "schema_versions")`; DbUp's default name is `schemaversions`).

### 14.2 Search & sync
- **Search:** `display_title LIKE CONCAT('%', @q, '%') OR title LIKE …` (with `%`/`_` escaped). This replaces the earlier FULLTEXT plan (D26).
- **Sync rule:** every library write = ZIP first (§6.1, built from the ZIP's own `info.json`), then one DB transaction that deletes and re-inserts the game's `roms`/`images` and upserts `games`. The DB is never written without a ZIP that matches it. Gaps are healed by integrity (§12), the ingest DB sync pre-pass (§7.1 step 4) and crash recovery (§7.4).

### 14.3 Migrations
- Scripts `src/RnRomManager.Data/Migrations/NNNN_description.sql`, embedded, applied in name order at startup. Forward-only; applied scripts are never edited. One logical change per script (MariaDB DDL commits implicitly, so transactions can't roll DDL back).
- Failure → `Fatal` + exit 1; the container restarts and retries. Back up the DB before upgrading (docs/deployment.md). Downgrades aren't supported.

## 15. Testing

### 15.1 What must be tested
| Component | Tests |
|---|---|
| GB header extractor | every field, both checksums (valid/invalid), CGB/SGB variants, unknown table values, MBC2 RAM, MBC1 multicart detection, size mismatch, < 0x150 bytes, random garbage |
| DAT parser | header/version, games with/without region, quoted names containing `(`, unknown fields ignored, several metadat blocks per CRC merged distinct, `dats.json` SHA1 mismatch = corrupt, malformed input |
| Naming | base title incl. a non-region group before the region (`Yelu Wangzi (Prince Yeh Rude) (Taiwan)`) and names with no region group, displayTitle article rule, sanitize (every illegal char, reserved names, length), bucket, clash suffixes, §6.1.1 tag parsing (ROM name ≠ game name, `(SGB Enhanced, GB Compatible)`, `(Proto 2)`, `(v1.0.3)`), revision order, primary ranking incl. `Unverified` |
| Thumbnail key + lookup | escaping of every special char + URL encoding; circuit breaker (3 failures trip, 404 doesn't count, 200 resets); `MediaRetry` job (candidates, Manual-only skipped, rewrite only on change, breaker → `Failed`) |
| Suggestions | scoring, threshold, size boost, top-5, file-name stem vs header title (higher wins), empty inputs |
| Ingestion pipeline (temp dirs) | every outcome row of §7.2 incl. `Repaired`, archive fates + limits + traversal, min file age, global dedupe, review-row lifecycle on re-runs (incl. dismissed SHA1 re-dropped, archive entry dismissed/resolved → archive fate), `AmbiguousGroup` + attach as `None`, lowercase-first-letter bucket, case-insensitive ZIP clash, new release into an existing ZIP, unreadable/newer-schema target ZIP, cancellation mid-archive, crash-recovery cleanup, review resolution actions (suggestion → `Unverified`), DB sync pre-pass (ZIP without DB row → no second copy/`(2)` ZIP), `DbWriteFailed` stops the job, OS junk files ignored, `Resolved` review row re-queued, null header → folder's handler |
| ZIP writer | atomic write, verify failure leaves the original intact, `info.json` round-trip, schema upgrade chain, newer-schema refusal, every §6.2 validation rule, unknown properties ignored, writes start from the ZIP not the DB |
| Integrity | each issue type, quick-vs-deep, scope (ZIP outside its bucket, unregistered system folder, file in `roms/` root), DB repair (skips `DuplicateGameId`/`DuplicateSha1`), issue dedup/resolve, cancelled run resolves nothing, retention purge |
| Library edits | move, merge (field fill, box-art takeover, same-system rule, source ZIP deleted), metadata fill of empty fields only on a new release |
| Auth | lockout + reset, `--reset-password` CLI (unknown user, last-admin promotion, stamp rotated), login rate limit, last-admin rule, security stamp invalidation, must-change gate, antiforgery required, Data Protection keys survive a restart (Testcontainers) |
| CSP | inline-script hashes computed from a sample `index.html` |
| Repositories (Testcontainers) | migrations from scratch, CRUD, unique constraints, search escaping |
| API (`WebApplicationFactory`) | role matrix for every endpoint (401/403), validation errors, pagination, downloads (headers), 409 job lock |
| UI (Vitest) | library filters ↔ URL, theme toggle, fetch wrapper (XSRF header, 401 redirect), review form validation |
| E2E (Playwright + axe) | login + forced password change, ingestion of a seeded set, browse/search/filter, game detail + download, review resolve, Admin edit/move/delete |

- Rule: every non-trivial class has unit tests; behaviour changes need a test before merge. Coverage reported in CI without a gate (D21).

### 15.2 Synthetic ROMs & fixture DAT (no copyrighted ROMs)
- `RnRomManager.TestData` holds `SyntheticRomBuilder`: deterministic bytes from a seed; sets title, CGB/SGB flags, cart type, ROM/RAM size, licensee, destination and the Nintendo logo (48-byte bitmap from Pan Docs, needed for `logoValid`); computes both checksums; options to corrupt either checksum, the logo or the size, and to build a 1 MiB MBC1 multicart (second logo at `0x40104`).
- `FixtureDatWriter` writes clrmamepro DATs (no-intro + the 4 metadat) for a named ROM set, using the real hashes of the generated ROMs, with versions like `fixture-1`, plus a matching `dats.json` (§10.1), so the app loads them like a refresh.
- **Built-in sets:** `Basic` (3 GB + 2 GBC games, multiple regions/revs, one article title, one dual-mode cart in `ingest/gb/`, one duplicate, one unmatched → review, one invalid header, one zip + one 7z archive) and `Full` (`Basic` + 200 generated games for paging).
- CLI: `dotnet run --project tests/RnRomManager.TestData -- --ingest <dir> --cache <dir> --set Basic|Full`.
- **7z fixture:** SharpCompress can't write 7z, so `tests/RnRomManager.TestData/Fixtures/basic.7z` is committed (a few KiB). It holds one `SyntheticRomBuilder` ROM from a fixed seed; a unit test asserts the extracted bytes equal the builder output, so the fixture DAT hashes stay valid. `Fixtures/README.md` records how it was made (`7z a` with the seed). ZIP archives are generated at run time with `System.IO.Compression`.

### 15.3 CI integration
- Testcontainers runs on GitHub's `ubuntu-latest` (Docker preinstalled); image `mariadb:11.4`.
- E2E: `scripts/test-e2e.ps1` builds the image (local arch), seeds `Basic` into temp ingest/cache dirs, starts `tests/e2e/docker-compose.e2e.yml` (mariadb + app with those dirs mounted, `Admin__InitialPassword` set, `Sources__ThumbnailBaseUrl` empty), waits for `/health/ready`, runs Playwright, and always tears down. With media fetch disabled the default-art path is exercised and nothing hits the network. Thumbnail HTTP behaviour is covered by API tests with a stub `HttpMessageHandler`. Traces/screenshots are uploaded as artifacts on failure.

### 15.4 Manual test-ingest workflow
`seed-ingest.ps1` → files land in `./.dev/ingest` and fixture DATs in `./.dev/cache/dats` → `start-dev.ps1` (paths point at `./.dev/*`) → Ingestion page → Start.

## 16. Dev tooling (`/scripts`, PowerShell 7, cross-platform)

All scripts: `$ErrorActionPreference = 'Stop'`, non-zero exit on failure, run from any directory (resolve the repo root from `$PSScriptRoot`).

| Script | Parameters | Does |
|---|---|---|
| `setup-dev.ps1` | `-SkipDb` | Checks .NET 10 SDK, Node ≥ 24 (CI and the Dockerfile pin 24), Docker. `dotnet restore`, `npm ci` (Web), Playwright browsers. Starts MariaDB via `docker-compose.dev.yml` (port 3306, db/user `rnrommanager`). Creates `./.dev/{roms,ingest,logs,cache}` and `appsettings.Development.json` pointing there (git-ignored). |
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
- CLI modes handled in `Main` before the web host: `--healthcheck` (below) and `--reset-password <username>` (§9.1).
- `HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD ["/app/RnRomManager.Api", "--healthcheck"]`. That mode is handled first in `Main`, before the host is built (no config validation, NLog or DB): it GETs `http://127.0.0.1:<port>/health/live` (port = first entry of `ASPNETCORE_HTTP_PORTS`, default `8080`) with a 3 s timeout and exits 0/1 (no shell or curl needed).
- `/health/live` (process up, anonymous) and `/health/ready` (anonymous: DB `SELECT 1` within 2 s **and** the four paths exist; 503 otherwise). Neither returns details. The full write probe runs only at startup (§4.1).
- The healthcheck reads only `ASPNETCORE_HTTP_PORTS`; setting `ASPNETCORE_URLS` instead breaks it (docs/deployment.md says so).
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
    stop_grace_period: 35s   # job shutdown, §3.1
    depends_on:
      mariadb: { condition: service_healthy }
  mariadb:
    image: mariadb:11.4
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "healthcheck.sh", "--connect", "--innodb_initialized"]
      interval: 10s
      retries: 10
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

**`ci.yml`** — on `pull_request` and `workflow_call` (`main` and tags are tested through `release.yml`; branch pushes aren't a trigger, so a PR branch isn't tested twice):
| Job | Steps |
|---|---|
| backend | checkout → setup-dotnet 10 → `dotnet tool restore` → `dotnet restore` → `dotnet build -c Release --no-restore -warnaserror` → `dotnet test --no-build --collect "XPlat Code Coverage"` (incl. Testcontainers) → ReportGenerator summary to the job summary |
| web | setup-node 24 + npm cache → `npm ci` → `npm run lint` (eslint + prettier check) → `npm run check` (svelte-check) → `npm test` → `npm run build` |
| e2e | needs backend + web → `pwsh scripts/test-e2e.ps1` → upload Playwright report on failure |

**`release.yml`** — on `push` to `main` and tags `v*`:
1. `uses: ./.github/workflows/ci.yml` (all tests must pass).
2. checkout (`fetch-depth: 0`, so MinVer sees tags) → `dotnet tool restore` → `dotnet minver --tag-prefix v` → `VERSION`.
3. `setup-buildx-action` (no QEMU: the runtime stage only copies files) → `login-action` (Docker Hub).
4. `docker/metadata-action` tags: main → `edge`, `sha-<7>`; `vX.Y.Z` tag → `X.Y.Z`, `X.Y`, `latest` (D27).
5. `build-push-action`: `platforms: linux/amd64,linux/arm64`, `build-args: VERSION`, `cache-from/to: type=gha`.
6. Tag builds only: `peter-evans/dockerhub-description` pushes `README.md` as the Docker Hub description.

- Both workflows: `permissions: contents: read`. `release.yml` uses `concurrency: release-${{ github.ref }}` (no cancel-in-progress), so pushes to the same ref publish in order.
- E2E runs on the amd64 image only; the arm64 image is built but not smoke-tested (accepted, since it's a cross-compiled copy of the same code).
- **Secrets:** `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN` (Docker Hub PAT with Read/Write/Delete; the description update needs it).
- **Tool manifest** `.config/dotnet-tools.json`: `minver-cli` and `dotnet-reportgenerator-globaltool`.
- **Versioning:** MinVer as the **CLI tool only** (`minver-cli` in `.config/dotnet-tools.json`, `--tag-prefix v`); no MinVer package in the csproj, because `.git` isn't in the Docker build context. The version reaches the build as `-p:Version=$VERSION` (Dockerfile `ARG VERSION=0.0.0-dev`); plain local builds are `0.0.0-dev`. Untagged commits get `X.Y.Z-alpha.0.N` (logged at startup, shown on About). Releases are made by pushing an annotated `vX.Y.Z` tag on `main`.

## 19. Documentation

| Doc | Content | Updated when |
|---|---|---|
| `README.md` | what it is, screenshots, quick start (compose §17.3), legal disclaimer, attribution. Images and links use absolute GitHub URLs, because it's also the Docker Hub description (§18) | user-visible feature or deployment change |
| `docs/deployment.md` | compose, creating the database + grants (§2), volumes/permissions, reverse proxy + forwarded headers (and when not to enable them, §4), TZ, backups, upgrades, admin password recovery (§9.1) | §17 changes |
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
| D23 | 2026-11-04 | libretro DATs have no parent/clone data, so grouping = same system + base title (name before the first ` (`). Admin can move a release to another or a new game; the moved ROM keeps its base title so later releases follow it. A DAT match sets the system (refines D1; the header decides only for unmatched files; CGB rule = bit 7). *(Base-title rule refined by D36; limits accepted in D35.)* |
| D24 | 2026-11-04 | ZIP names keep the No-Intro article form (`Legend of Zelda, The - …` → `L/`); the UI shows `displayTitle` with the article moved to the front. |
| D25 | 2026-11-04 | `datVersion` = DAT header `version`, not a commit SHA; DATs come from raw.githubusercontent.com (no GitHub API). |
| D26 | 2026-11-04 | Search uses `LIKE` on titles instead of FULLTEXT (small table; avoids min-token/stopword settings we can't change on a shared server). Header stored as JSON in `roms.header` instead of flattened columns (keeps systems pluggable). `ingest_runs`/`integrity_runs` merged into `jobs` (one job lock and status endpoint). |
| D27 | 2026-11-04 | Docker tags: `main` → `edge` + `sha-<7>`; `vX.Y.Z` tags → `X.Y.Z`, `X.Y`, `latest`. |
| D28 | 2026-11-04 | MVP library edits (Admin): edit metadata, move release, delete release/game. Removed ROMs go to `ingest/<system>/_removed/`, never hard-deleted. |
| D29 | 2026-11-04 | Defaults applied in the gap review: global SHA1 dedupe; 10 s min file age; archive limits 100 entries / 256 MiB; ROM max 16 MiB; `Error` outcome leaves the source for retry; password 12–128 chars, lockout 5/15 min, 7-day sliding session, security stamp; SPA antiforgery via `XSRF-TOKEN` cookie + `X-XSRF-TOKEN` header; generated admin password goes to the console only; `--healthcheck` mode + `/health/live`/`/health/ready`; cross-compiled multi-arch build; lazy `info.json` schema upgrades. |
| D30 | 2026-11-04 | Picking a DAT suggestion for a file whose hashes don't match records the release as **unverified**: `method = Manual`, flag `Unverified`, `fileName` + ` (Unverified)`, never ranked above a verified dump. Ingesting a byte-identical copy of a library ROM with an open `HashMismatch`/`EntryMissing`/`EntrySizeMismatch` issue **repairs** the entry (outcome `Repaired`). |
| D31 | 2026-11-04 | Defaults applied in the second gap review: tags parsed from the DAT ROM name (§6.1.1) with defined revision order; `MinRomSizeBytes` on `ISystemHandler`; Data Protection keys in DB table `data_protection_keys`; CSP `script-src` hashes computed by the API from `index.html`, theme script moved to `/theme.js`; Docker `TARGETARCH` → .NET arch mapping; `--healthcheck` handled before host build; integrity DB repair skips `DuplicateSha1` ZIPs; review rows auto-resolve/delete on re-runs; `TargetZipUnreadable` / `InfoJsonNewerSchema` → `Error`; cancel waits for a whole archive; 180-day retention for jobs + closed issues; usernames `[A-Za-z0-9._-]{3,64}` case-insensitive; login rate limit 10/min/IP; admin reset clears lockout + forces change; `region_key` pipe-delimited both ends; `regions` on `/api/systems`; `Sources:DatBaseUrl` / `Sources:ThumbnailBaseUrl` config (empty thumbnails = offline e2e); `ci.yml` skips `main` pushes; single instance per DB; `info.json` field `region` → `regions`. |
| D32 | 2026-11-04 | When a new release's base title is held by more than one game (after a partial move), it goes to the review queue as `AmbiguousGroup`; the Admin attaches it to one of the candidate games as a normal release (`flag: None`, DAT identification kept). |
| D33 | 2026-11-04 | Defaults applied in the third gap review: `DatsMissing` checked before a job row exists (409); `IsPlausible` replaces GB-specific wording in Core; bucket upper-cases a lowercase first letter; file-name comparisons case-insensitive; NFC in Sanitize; displayTitle articles include `Die/Der/Das/Le/La/Les/El/Il`; stored bytes = `NormalizeForHashing` output; dismissed SHA1s re-queue (loose) or fail the archive (entry); resolved archive entries count as `Ingested` for archive fate; metadata for `Unverified` from the chosen entry's CRC, none for Manual; media skips releases without `datGameName`; Retry media covers `MissingBoxArt`; move-to-new-game runs metadata + media; change-password re-signs the current session; persistent 7-day cookie, no absolute limit; MEL min level `Trace` with NLog filtering; log retention done by the app, one file per UTC day; app forces `DateTimeKind=Utc`/`GuidFormat=Char36`; DB must pre-exist; CI tests MariaDB 11.4 only; MinVer CLI only (no package); healthcheck port from `ASPNETCORE_HTTP_PORTS`; committed `basic.7z` fixture; no schedule catch-up; consolidated code list (§23.1); `/jobs/{id}` page, navbar, 404 page, static cache headers. |
| D34 | 2026-11-04 | `developer`/`publisher` become `developers[]`/`publishers[]` (info.json v1, DB JSON) because metadat repeats a CRC per credited company. Defaults applied in the fourth gap review: metadat blocks per CRC merged distinct, unknown DAT fields ignored, values never split; `Image.sizeBytes` + `images.size_bytes` (entry size checks need it); `zip_mtime_utc` compared in whole seconds; package verify failure on a corrupt existing entry is `VerifyFailed` naming the entry; `dats.json` per-file SHA1 checked on load, written by `FixtureDatWriter`; `409 SourceMissing` for review actions whose source is gone; unmatched renames use `RomExtensions[0]`; log download opens files `FileShare.ReadWrite`; builder MBC1M option. |
| D35 | 2026-11-04 | D23 kept despite its limits: different games sharing a base title (e.g. Sachen `4 in 1` ×9) are split by the Admin with Move release; the same game under different regional names is combined with a new Admin **Merge into…** action (`POST /api/games/{id}/merge`), and the target keeps its title. |
| D36 | 2026-11-04 | Defaults applied in the fifth gap review: base title ends at the first all-known-regions group (fixes `(Prince Yeh Rude)`/`(Top Secret)` names); step 1 extensions limited to the folder's family; headers read from normalised bytes; metadata fills empty fields of existing games; suggestions also score the file-name stem; job `Failed` = job-level exception; cancelled integrity resolves nothing, cancelled DAT refresh keeps the old set; validation vs folder creation split (§4.1 step 4); `MissingBoxArt` vs `EntryMissing` defined; `DuplicateGameId` details give the fix; `/health/ready` = DB `SELECT 1` + paths exist; PATCH game field limits; 409/404 rules for all library mutations; stale rom/image IDs → UI reload; move/merge need the same system; `release.yml` permissions + concurrency; arm64 image not e2e-tested. |
| D37 | 2026-11-04 | Admin password recovery = CLI mode `--reset-password <username>` run via `docker exec` (no shell needed); random password to the console only, forces a change, re-enables the user and promotes them to Admin if no enabled Admin is left. |
| D38 | 2026-11-04 | Bulk `MediaRetry` background job ("Retry missing media" on the Integrity page) next to the per-game Retry media; same job lock, progress and cancel. |
| D39 | 2026-11-04 | Defaults applied in the sixth gap review: ingest starts with a DB sync pre-pass (fixes duplicate entries/`(2)` ZIPs after a crash or DB failure); `DbWriteFailed` stops the ingest job; every write builds from the ZIP's `info.json`, never from DB rows; thumbnail circuit breaker (3 consecutive network failures per job); `info.json` validation rules defined, unknown properties ignored; SIGTERM cancels the job with a 30 s shutdown timeout (compose `stop_grace_period: 35s`); a review upsert resets a row in any status to `Pending`; null `ReadHeader` → ingest folder's handler; integrity scope (ZIPs outside their bucket → `WrongLocation`, unregistered system folders → `OrphanFile`); `Thumbs.db`/`desktop.ini` ignored; game typeahead via `/api/games`; game detail response and sort directions defined; DbUp journal name set explicitly; compose DB healthcheck gate; NLog flushed before exit; Manual/Unverified → DAT upgrade is a documented known limit; README uses absolute URLs for Docker Hub. |
| D40 | 2026-11-04 | Seventh review (consistency): review Attach continues at §7.2 step 7 (step 6 skipped); `/api/integrity/status` null rules; `/api/about` licences from an embedded `licenses.json`; local Node ≥ 24 accepted (CI/Docker pin 24); ReportGenerator added to the tool manifest; `ci.yml` triggers on `pull_request` + `workflow_call` only (no branch-push runs, so PRs aren't tested twice). Implementation plan: one branch + PR per step, MVP = Docker Hub image deployed with the §17.3 compose file on the owner's host. |
| D41 | 2026-11-04 | Found in S08: `games.zip_path` uses column collation `utf8mb4_uca1400_as_ci` (case-insensitive, **accent-sensitive**, MariaDB 10.10+), so the DB unique key agrees with the app's case-insensitive ordinal clash check (`Café.zip` ≠ `Cafe.zip`); the table default `utf8mb4_unicode_ci` would have made accent-only differences fail as `DbWriteFailed`. MySQL 8 doesn't have this collation, so MySQL support (best effort) needs `utf8mb4_0900_as_ci` there. `roms.dat_name`, `dat_version`, `dat_game_name` are `VARCHAR(255) NULL`. |
| D42 | 2026-11-04 | Found in S10: the must-change-password gate also lets `GET /api/auth/antiforgery` and `POST /api/auth/login` through (otherwise a must-change user whose session-scoped antiforgery cookie expired couldn't get a token for change-password or logout). A missing/invalid session is `401 Unauthorized` and a wrong role `403 Forbidden`, both ProblemDetails like every other error. |

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
    RomHeader? ReadHeader(ReadOnlySpan<byte> rom);   // null = unreadable / no header (unmatched → ingest folder's handler, §7.2 step 4)
    ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => rom; // e.g. SNES copier-header strip
}

public sealed record RomHeader(
    string? ClassifiedSystemId,              // which handler Id the header says (gb/gbc)
    string? Title,                           // for suggestions
    bool IsPlausible,                        // false → InvalidHeader when unmatched
    IReadOnlyDictionary<string, object?> Fields); // serialised to info.json `header` / roms.header
```

- Registered with `services.AddSystemHandler<T>()`. Core enumerates handlers for folders, DATs, extensions and limits; nothing in Core/Data/UI is GB-specific. DB `system_id` is a `VARCHAR` (no enum migrations), and `/api/systems` drives the UI filters.
- Adding SNES/GBA = a new `RnRomManager.Systems.X` project + registration + docs/tests; no core changes. Every ROM (loose or extracted) is hashed and stored after `NormalizeForHashing` (§7.2 step 2), so stored bytes, `info.json` hashes and deep-check hashes always agree.
- **Contract limits:** one file per release (cartridge systems; multi-file/CD systems would need a contract change); the whole ROM is read into memory for `ReadHeader` (bounded by `MaxRomSizeBytes`); `RomHeader.Fields` values must be JSON-serialisable primitives (string, number, bool, null), since the UI renders them as a generic table.
- Ingest folders are per handler `Id` (`ingest/gb/`, `ingest/gbc/`); `Family` only decides which handlers' headers and DATs are tried for a file (§7.2 step 4).

## 23. API

JSON, camelCase. Errors are RFC 7807 ProblemDetails with an extra `code` (§23.1) and `traceId`; validation errors include `errors{field:[…]}`. Lists use `?page=1&pageSize=48` (max 200) → `{ items, page, pageSize, total }`. Every `/api` route except login/antiforgery needs auth; mutations need the XSRF header (§9.2).

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
| GET | `/api/games/{id}` | auth | → the §6.2 game fields (minus `schemaVersion`/`metadata.appVersion`) plus `id`, `hasDefaultBoxArt`, `roms[]` (§6.2 Rom fields flattened: `id`, `fileName`, …, `crc32`, `md5`, `sha1`, `sizeBytes`, `header`, `downloadUrl`) and `images[]` (`id`, `type`, `isDefault`, `width`, `height`, `url`) |
| GET | `/api/games/{id}/images/{imageId}` | auth | → `image/png`, `ETag` = sha1, `Cache-Control: private, max-age=86400` |
| GET | `/api/games/{id}/download` | auth | → ZIP file (range support, `Content-Disposition` with RFC 5987 `filename*`) |
| GET | `/api/games/{id}/roms/{romId}/download` | auth | → ROM bytes streamed from the ZIP |
| PATCH | `/api/games/{id}` | Admin | `{title?,releaseYear?,developers?,publishers?,genres?,description?}` → game. Validation: `title` per §6.1; `releaseYear` null or 1970–2100; each list ≤ 20 items of 1–255 chars (trimmed, distinct); `description` ≤ 4000 chars |
| DELETE | `/api/games/{id}` | Admin | → 204 (ROMs → `_removed/`) |
| DELETE | `/api/games/{id}/roms/{romId}` | Admin | → 204 (last ROM → game deleted) |
| POST | `/api/games/{id}/roms/{romId}/move` | Admin | `{targetGameId}` or `{newGameTitle}` → `{targetGameId}` |
| POST | `/api/games/{id}/merge` | Admin | `{targetGameId}` → `{targetGameId}` (§11; target ≠ source, same system) |
| POST | `/api/games/{id}/media/retry` | Admin | → game |
| GET | `/api/ingest/pending` | Admin | → `[{system,files:[{name,sizeBytes,modifiedAt}]}]` |
| POST | `/api/jobs/ingest` | Admin | → 202 `{jobId}` · 409 `JobRunning` / `DatsMissing` |
| POST | `/api/jobs/integrity` | Admin | `{mode:"Quick"\|"Deep"}` → 202 `{jobId}` · 409 |
| POST | `/api/jobs/dat-refresh` | Admin | → 202 `{jobId}` · 409 |
| POST | `/api/jobs/media-retry` | Admin | → 202 `{jobId}` · 409 `JobRunning` (§10.2) |
| GET | `/api/jobs/current` | Admin | → `{id,type,mode,status,current,total,currentItem,startedAt}` · 204 when idle |
| GET | `/api/jobs` | Admin | `?type&page&pageSize` → page of jobs |
| GET | `/api/jobs/{id}` | Admin | → job incl. `summary` |
| GET | `/api/jobs/{id}/items` | Admin | `?outcome&page&pageSize` → page of `job_items` |
| POST | `/api/jobs/{id}/cancel` | Admin | → 202 · 404 if not running |
| GET | `/api/review` | Admin | `?status=Pending&page&pageSize` → page of `{id,system,reason,fileName,sizeBytes,headerTitle,createdAt}` (`fileName` = `originalFileName`) |
| GET | `/api/review/{id}` | Admin | → item incl. header, hashes, suggestions, `datIdentification`, `candidateGames[{id,displayTitle}]` |
| POST | `/api/review/{id}/resolve` | Admin | `{action:"Dat",datSystem,datGameName}` (→ `Unverified`, §7.5) · `{action:"Attach",targetGameId,flag:"Hack"\|"Translated"\|"None"}` (`None` only for `AmbiguousGroup`) · `{action:"Manual",title,regions[],languages[],revision?,flags[]}` → `{gameId}` · 409 `JobRunning` / `AmbiguousGroup` / `SourceMissing` · 400 `ValidationFailed` |
| POST | `/api/review/{id}/dismiss` | Admin | → 204 · 409 `JobRunning` / `SourceMissing` |
| GET | `/api/dat/search` | Admin | `?system&q&limit=20` → `[{gameName,romName,sizeBytes,crc32}]` |
| GET | `/api/integrity/status` | Admin | → `{lastQuick,lastDeep,nextScheduledAt}` (`lastQuick`/`lastDeep` = latest job of that mode or null; `nextScheduledAt` UTC, null when the schedule is disabled) |
| GET | `/api/integrity/issues` | Admin | `?status&type&page&pageSize` → page of issues |
| GET | `/api/admin/dats` | Admin | → `[{datName,version,downloadedAt}]` |
| GET | `/api/admin/settings` | Admin | → effective config, secrets masked |
| GET | `/api/admin/logs` | Admin | → ZIP (§13) |
| GET | `/api/about` | auth | → `{version,licenses[]}` (`licenses` = a hand-maintained embedded `licenses.json`: `[{name,license,url}]` for the app, every direct NuGet/npm dependency and libretro-database) |
| GET | `/health/live`, `/health/ready` | anon | → 200 / 503 |

- Every Admin library mutation (PATCH/DELETE game, delete/move rom, merge, media retry) takes the job lock and can return `409 JobRunning`, `InfoJsonNewerSchema` or `TargetZipUnreadable`, plus `404 NotFound` for a stale id (§11).
- `/api/games` sort: `dir` defaults to `asc` for `title`/`year` and `desc` for `added`; `release_year` nulls sort last in both directions; tie-break `id`.
- `letter=#` must be sent URL-encoded (`%23`). The Review badge count is `GET /api/review?status=Pending&pageSize=1` → `total`, refreshed on navigation and when a job ends.
- Unknown `/api/*` → 404 ProblemDetails; any other unknown path → `index.html` (SPA fallback).
- No CORS (the SPA is same-origin; the Vite dev server proxies `/api`).

### 23.1 Codes
| Code | Where | Meaning |
|---|---|---|
| `ValidationFailed` (400), `AntiforgeryInvalid` (400) | API | bad input / missing XSRF header |
| `Unauthorized` (401), `Forbidden` (403) | API | no valid session / role not allowed (D42) |
| `InvalidCredentials` (401), `PasswordChangeRequired` (403), `TooManyRequests` (429) | API | §9 |
| `NotFound` (404) | API | unknown id, or `cancel` with no running job |
| `SourceMissing` (409) | API | review resolve/dismiss whose source file (or archive) is gone; the row is deleted |
| `JobRunning`, `DatsMissing`, `LastAdmin`, `UsernameTaken`, `AmbiguousGroup`, `InfoJsonNewerSchema`, `TargetZipUnreadable` (409) | API | conflicts; the last two when a library edit hits such a ZIP |
| `UnsupportedType`, `TooSmall`, `TooLarge`, `ArchiveUnsafe`, `ArchiveCorrupt`, `ArchiveEncrypted`, `InvalidHeader`, `Dismissed` | `job_items.reason` (`Failed`) | file problems (§7.2, §7.3, §7.5) |
| `IoError`, `VerifyFailed`, `DbWriteFailed`, `TargetZipUnreadable`, `InfoJsonNewerSchema`, `Unexpected` | `job_items.reason` (`Error`) | environment problems; source left in place |
| `NoDatMatch`, `AmbiguousGroup` | `job_items.reason` (`Review`) + `ingest_review_items.reason` | §7.2 steps 5–6 |
| `TooRecent` | `job_items.reason` (`Skipped`) | modified < 10 s ago (§7.1) |
