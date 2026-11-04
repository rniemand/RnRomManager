# RnRomManager — Initial Application Spec

> Status: **DRAFT v0.3** — being refined over several sessions. Items marked **[OPEN]** need a decision; items marked **[PROPOSED]** are my recommendation and need confirmation. Confirmed decisions are listed in §21.

## 1. Purpose

A self-hosted web app for managing a personal backup collection of game ROM files. Every ROM enters through an **ingestion** pipeline that identifies it, normalises its name, groups it with its sibling releases, fetches media, and stores it as one ZIP per game in a well-known folder layout. A MySQL/MariaDB database mirrors the ZIP contents for fast browsing/searching, and a scheduled integrity checker keeps the two in sync.

**MVP scope:** Game Boy (`.gb`) and Game Boy Color (`.gbc`) only.

### Non-goals (MVP)
- Emulation / playing games in the browser.
- Downloading or distributing ROMs. The app only manages files the user already owns.
- Systems other than GB/GBC (the design must make adding them easy, though).
- Multi-tenant / per-user libraries (all users see the same library).

## 2. Tech stack

| Concern | Choice |
|---|---|
| Backend | .NET 10 ASP.NET Core Web API |
| Data access | Dapper + MySqlConnector |
| DB | MySQL / MariaDB (external, shared instance; app uses its own database, e.g. `rnrommanager`, no table prefix) |
| Migrations | DbUp (`dbup-mysql`) — plain embedded `.sql` scripts, applied on startup, journal table `schema_versions` |
| Logging | NLog — console + rolling text file |
| Backend tests | NUnit + NSubstitute (+ Shouldly or NUnit constraints — **[OPEN]**) |
| Integration tests | Testcontainers (MariaDB) for repository/migration tests; temp-dir file system for ingestion tests |
| Frontend | Svelte 5 — SvelteKit with `adapter-static` (SPA mode), TypeScript |
| CSS | Bootstrap 5.3 (native `data-bs-theme` dark/light) |
| Icons | Bootstrap Icons (primary) + Font Awesome Free (for gaps, e.g. brand logos) |
| Archive input | `System.IO.Compression` (ZIP) + SharpCompress (7z) |
| Frontend tests | Vitest + @testing-library/svelte; Playwright smoke e2e |
| Container | Single image; API serves the built UI from `wwwroot` |
| CI/CD | GitHub Actions → Docker Hub `niemandr/rnrommanager` |

## 3. Architecture

```
Browser (Svelte SPA) ──HTTP/JSON──> ASP.NET Core API ──Dapper──> MariaDB
                                          │
                                          ├── /app/ingest  (input)
                                          ├── /app/roms    (library, ZIPs)
                                          ├── /app/logs
                                          └── outbound HTTPS: libretro DATs + thumbnails
```

### Solution layout **[PROPOSED]**
```
/src
  RnRomManager.Api/            # endpoints, auth, hosting, static UI
  RnRomManager.Core/           # domain, services, ingestion pipeline, interfaces
  RnRomManager.Data/           # Dapper repositories, DbUp migrations (embedded .sql)
  RnRomManager.Systems.GameBoy/# GB/GBC header extractor + naming rules
  RnRomManager.Web/            # Svelte app
/tests
  RnRomManager.Core.Tests/
  RnRomManager.Data.Tests/
  RnRomManager.Systems.GameBoy.Tests/
  RnRomManager.Api.Tests/      # WebApplicationFactory
/scripts                       # ps1 dev tooling
/docs                          # generated/maintained docs
/specs                         # this file
```
Each supported system is a plugin-style implementation of `ISystemHandler` (extensions, folder name, header extractor, DAT/thumbnail source names) so adding SNES etc. = new project + registration.

## 4. Configuration

All settings via `appsettings.json`, overridable by environment variables (Docker-friendly).

| Key | Env var | Default |
|---|---|---|
| ConnectionStrings:Default | `ConnectionStrings__Default` | *(required)* |
| Paths:Roms | `Paths__Roms` | `/app/roms` |
| Paths:Ingest | `Paths__Ingest` | `/app/ingest` |
| Paths:Logs | `Paths__Logs` | `/app/logs` |
| Paths:Cache | `Paths__Cache` | `/app/cache` (downloaded DATs, image cache) |
| Admin:InitialPassword | `Admin__InitialPassword` | *(see §9)* |
| Integrity:Schedule | `Integrity__Interval` | `24:00:00` **[OPEN]** |
| Logging level | `Logging__MinLevel` | `Debug` **[OPEN]** |

Paths being configurable is what allows tests and manual testing to point ingestion at a **temporary directory** seeded with test files.

## 5. File system layout

```
/app
  roms/
    gb/
      #/          # titles starting with a digit (or any non-letter)
      A/ … Z/
        Tetris.zip
    gbc/          # GBC-only (CGB 0xC0) and dual-mode (CGB 0x80) carts
  ingest/
    gb/           # user drops files here
      _duplicates/   # byte-identical dupes moved here (logged)
      _failed/       # unidentifiable / corrupt / errors (logged, with .error.txt sidecar?) [OPEN]
    gbc/
  logs/
  cache/
```

- Letter folders are **uppercase** `A`–`Z`; `#` holds everything whose first character isn't A–Z (digits, symbols, non-ASCII).
- Bucket = first character of the ZIP file name, **including** a leading "The" (`The Legend of Zelda….zip` → `T/`).
- Users drop files into the per-system ingest folder (`ingest/gb/`, `ingest/gbc/`). The ingest folder is a hint: the header decides the final system, so a GBC ROM dropped into `ingest/gb/` is still accepted and stored under `roms/gbc/` (logged at `Info`).
- All system folders, letter folders and `_duplicates/`/`_failed/` are created on startup if missing.

## 6. ZIP format (one ZIP per game)

```
Tetris.zip
  info.json
  roms/
    Tetris (World) (Rev 1).gb
    Tetris (Japan).gb
  images/
    boxart-01.png
    snap-01.png
```

- ZIP name = English (or World/USA/Europe) release title with region/revision tags stripped, filesystem-safe characters only.
- All releases (regions, revisions) **and** hacks/translations of the same game live in the same ZIP. Hacks/translations are flagged (`"Hack"`, `"Translated"`) in `info.json` and are never `isPrimary`.
- Writes are atomic: build to `*.zip.tmp` in the same folder, then rename over the original. Never edit a ZIP in place.
- Compression: Deflate, optimal.

### 6.1 `info.json` schema (v1) **[PROPOSED]**

```jsonc
{
  "schemaVersion": 1,
  "gameId": "8b1c…",                 // stable GUID, also the DB key
  "system": "gb",
  "title": "Tetris",                  // display title (English)
  "sortTitle": "Tetris",
  "releaseYear": 1989,
  "developer": "Bullet-Proof Software",
  "publisher": "Nintendo",
  "genres": ["Puzzle"],
  "description": null,
  "roms": [
    {
      "fileName": "Tetris (World) (Rev 1).gb",   // normalised No-Intro name
      "originalFileName": "tetris_v1.1.gb",
      "region": ["World"],
      "languages": ["En"],
      "revision": "Rev 1",
      "flags": [],                                // e.g. "Beta", "Hack", "Translated"
      "isPrimary": true,                          // the release the ZIP is named after
      "identification": {
        "method": "Dat",                          // Dat | Header | Manual
        "datName": "Nintendo - Game Boy (libretro no-intro)",
        "datVersion": "2026-10-01"
      },
      "header": {                                  // system-specific extractor output
        "title": "TETRIS",
        "cgbFlag": "0x00",
        "sgbFlag": "0x00",
        "cartridgeType": "ROM ONLY",
        "romSizeBytes": 32768,
        "ramSizeBytes": 0,
        "destination": "Japanese",
        "licensee": "Nintendo",
        "version": 1,
        "headerChecksumValid": true,
        "globalChecksumValid": true
      },
      "metadata": {
        "sizeBytes": 32768,
        "crc32": "46df91ad",
        "md5": "…",
        "sha1": "…",
        "dateAdded": "2026-11-04T08:00:00Z",
        "dateModified": "2026-11-04T08:00:00Z",
        "sourcePath": "/app/ingest/gb/tetris_v1.1.gb"
      }
    }
  ],
  "images": [
    {
      "fileName": "boxart-01.png",
      "type": "BoxArt",          // BoxArt | Snap | TitleScreen | Other
      "source": "libretro-thumbnails",
      "sourceUrl": "https://thumbnails.libretro.com/…",
      "isDefault": false,        // true = embedded placeholder
      "sha1": "…",
      "width": 512, "height": 512,
      "dateAdded": "…"
    }
  ],
  "metadata": {
    "createdAt": "…",
    "updatedAt": "…",
    "appVersion": "0.1.0"
  }
}
```

- `info.json` is the **source of truth**; the DB is a rebuildable mirror (content only, no image bytes).
- Every ZIP **must** contain one `BoxArt` image; if none is found online, the embedded default placeholder is added with `isDefault: true` so it can be replaced later.

## 7. Ingestion pipeline

Triggered manually from the UI (MVP). Runs as a single background job (one at a time); UI shows progress.

For each file in `ingest/<system>/` (non-recursive, ignoring `_*` folders):

1. **Validate / unpack** — extension supported, size sane, readable. `.zip` and `.7z` inputs are extracted to a temp working folder (under `Paths:Cache/work/<runId>/`); every supported ROM inside is ingested as its own item (non-ROM entries ignored and logged). Guards: max entry count, max uncompressed size, reject path-traversal entry names. The archive is deleted only when **all** its ROMs were ingested or deduped; otherwise it stays and the outcome per entry is logged.
2. **Hash** — CRC32, MD5, SHA1 of the raw ROM bytes.
3. **Extract header** — system extractor (see §8). Header failure is logged but not fatal if DAT match succeeds.
4. **Identify** — look up SHA1 (fallback CRC32+size) in the No-Intro DAT → canonical name, region, revision. If no DAT match: the file **stays in the ingest folder** and is added to the **Review queue** (DB table `ingest_review_items`) with its hashes, header info and up to 5 fuzzy DAT-name suggestions based on the header title. Processing of that file stops here until the user resolves it (§11). Resolved items are re-run through steps 5–10 with `identification.method = "Manual"`.
5. **Group** — derive the parent game: strip region/rev/flags from the canonical name; use DAT `cloneof` parent where available. Find existing game in DB/library.
6. **Dedupe** — if a byte-identical ROM (same SHA1) already exists in the target ZIP → move source to `_duplicates/`, log at `Warn` with both paths and hashes. Skip.
7. **Media** — if the game has no media yet: fetch 1 box art + 1 gameplay snap (§10). On failure → default box art, no snap; log.
8. **Package** — create or rebuild ZIP atomically, write `info.json`.
9. **Mirror** — upsert DB rows inside a transaction.
10. **Clean up** — **delete** the source file, only after the ZIP has been written, re-opened and the ROM entry's SHA1 verified. If verification fails, the source is kept, the temp ZIP discarded and the item logged as failed.

Every step logs at `Debug`/`Info` with a correlation ID per ingestion run and per file (see §13).

## 8. Game Boy / Game Boy Color header extractor

Cartridge header lives at `0x0100–0x014F`. Files smaller than `0x150` bytes are rejected.

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0x0104 | 48 | Nintendo logo | Compare to known bitmap → `logoValid` |
| 0x0134 | 16 | Title | ASCII, NUL-padded. Newer carts: 15 chars (0x143 = CGB flag) or 11 chars + 4-char manufacturer code (0x13F–0x142) |
| 0x0143 | 1 | CGB flag | `0x80` = GBC-enhanced (dual), `0xC0` = GBC only |
| 0x0144 | 2 | New licensee code | ASCII, used when 0x014B = `0x33` |
| 0x0146 | 1 | SGB flag | `0x03` = Super Game Boy support |
| 0x0147 | 1 | Cartridge type | MBC lookup table (ROM ONLY, MBC1, MBC3+RTC, MBC5…) |
| 0x0148 | 1 | ROM size | `32 KiB << value` |
| 0x0149 | 1 | RAM size | lookup table |
| 0x014A | 1 | Destination | `0x00` Japan, `0x01` overseas |
| 0x014B | 1 | Old licensee code | lookup table |
| 0x014C | 1 | Mask ROM version | |
| 0x014D | 1 | Header checksum | `x = 0; for i in 0x134..0x14C: x = x - rom[i] - 1` |
| 0x014E | 2 | Global checksum | Big-endian sum of all bytes except these two |

- `.gb` vs `.gbc` classification uses the **CGB flag**, not the file extension: `0xC0` or `0x80` → `gbc`; anything else → `gb`. This matches No-Intro (dual-mode carts are in the GBC DAT) and libretro-thumbnails. If the extension disagrees with the header, the header wins, the file is renamed to the correct extension, and a `Warn` is logged.
- The header title is **not** trusted for naming; it's a hint and a fallback identifier.
- Unit tests use small synthetic ROM byte arrays (generated in tests — no copyrighted ROMs in the repo) covering every field, both checksums, short files and garbage data.

## 9. Accounts & auth

- Local accounts only; stored in `users` table with ASP.NET Core `PasswordHasher<T>` (PBKDF2).
- Cookie authentication (HttpOnly, SameSite=Strict, Secure when behind HTTPS), anti-forgery for state-changing calls.
- Roles: `Admin` (manage users, run ingestion, resolve review queue, integrity, settings) and `User` (browse/search/download). Enforced server-side via authorization policies; the UI just hides what the role can't use.
- Admin seed on first start when no users exist: username `admin`, password from `Admin__InitialPassword`; if not set, generate a random one and print it **once** to the console log. Force password change on first login.
- Login rate limiting / lockout after N failures.

## 10. External data sources (free, no API key)

**[PROPOSED]** primary sources — both free, no account required:

| Need | Source |
|---|---|
| Canonical name, region, revision, parent/clone | No-Intro DATs via [libretro-database](https://github.com/libretro/libretro-database) `metadat/no-intro/Nintendo - Game Boy.dat` and `… Game Boy Color.dat` |
| Year, developer, publisher, genre | libretro-database `metadat/{releaseyear,developer,publisher,genre}/…dat` (matched by CRC) |
| Box art, gameplay snap, title screen | [libretro-thumbnails](https://thumbnails.libretro.com/) — `Named_Boxarts`, `Named_Snaps`, `Named_Titles`, keyed by No-Intro name (`&*/:\`<>?\|"` → `_`) |

- DATs are downloaded on first use into `Paths:Cache`, refreshed on demand from the UI / on a schedule. Pinned to a commit SHA? **[OPEN]**
- Image lookup tries the exact release name, then the parent/World/USA/Europe release name.
- HTTP client: `IHttpClientFactory` + resilience (retry w/ backoff, timeout), descriptive `User-Agent`.
- Optional later (needs a free user-supplied key): ScreenScraper, TheGamesDB or IGDB for descriptions and better art coverage.

## 11. UI

- Svelte 5 SPA, Bootstrap 5.3, dark/light/auto toggle (auto = OS setting) persisted per browser in `localStorage`.
- Pages (MVP):
  - **Login** / change password
  - **Library** — grid (box art) + list view, filter by system/letter/region, search by title, pagination
  - **Game detail** — media, all ROM releases with header + hash info, download ZIP or single ROM
  - **Ingestion** — files waiting per system, "Start ingestion" button, live progress, last run results (ingested / duplicate / failed with reasons)
  - **Review queue** (Admin) — unmatched ROMs with header info + hashes; actions: pick a suggested DAT entry, search the DAT, attach to an existing game as a hack/translation, or enter a name manually; or dismiss (moves file to `_failed/`)
  - **Integrity** — last run, schedule, "Run now", discrepancies list
  - **Admin** — users (create/disable/reset password), settings view, DAT refresh
- Progress updates: **[PROPOSED]** polling a job-status endpoint every 1–2 s (no SignalR in MVP).

## 12. Integrity checker

Background hosted service on a configurable interval, plus "Run now" in the UI.

Checks:
- Every ZIP in `roms/` has a valid `info.json` and a matching DB row (and vice-versa).
- `info.json` ROM/image lists match ZIP entries; ROM SHA1s match (two modes: **quick** (scheduled; ZIP size/timestamp vs. last check, `info.json` vs. entry list) and **deep** (on demand from the UI; full SHA1 re-hash of every ROM).
- Each game has a BoxArt.
- ZIP is in the correct system/letter folder.

Action: the **DB is auto-repaired** from `info.json` (insert/update/delete rows; every change logged at `Info` and recorded as a resolved issue). **File-side problems** (corrupt ZIP, hash mismatch, missing box art, wrong folder, orphan files) are **reported only** in `integrity_issues`; the app never moves or deletes library files automatically.

## 13. Logging

- NLog targets: console + rolling file `logs/rnrommanager-${shortdate}.log`, archive after N days **[OPEN: retention]**.
- Layout includes: timestamp (UTC, ISO-8601), level, logger, thread, correlation ID (`IngestRunId`, `FileId`, HTTP `TraceIdentifier`), message, full exception with stack trace and inner exceptions.
- Structured properties captured for every ingestion step: source path, file size, hashes, header fields, DAT match result, chosen game, target ZIP, HTTP URLs + status codes + durations for outbound calls.
- Default min level `Debug` for app namespaces, `Warn` for Microsoft/System.
- Never log passwords, cookies or connection-string passwords.
- **[OPEN]** Should the UI have a "Download logs" button for admins?

## 14. Database (initial tables) **[PROPOSED]**

`users`, `games`, `roms`, `images`, `ingest_runs`, `ingest_run_items`, `ingest_review_items`, `integrity_runs`, `integrity_issues`, `schema_versions` (DbUp).
`games` holds title/sortTitle/system/year/publisher/developer/zip path + JSON of genres; `roms` holds one row per release with hashes and flattened header fields; `images` holds metadata only. Full-text index on `games.title` for search. No table prefix — the app gets its own database on the shared instance.

## 15. Testing

- Rule: every non-trivial class has unit tests; behaviour changes need a test before merge.
- Core/system logic: NUnit + NSubstitute, file system abstracted via `System.IO.Abstractions` **[OPEN]** or real temp dirs.
- Ingestion integration tests: a temp dir per test, seeded with **synthetic** ROMs (valid headers, generated bytes) + a tiny fixture DAT; HTTP sources stubbed. No real ROMs committed.
- Data tests: Testcontainers MariaDB, migrations applied from scratch.
- API tests: `WebApplicationFactory`.
- UI: Vitest component tests; Playwright smoke tests (login, start ingestion on seeded synthetic ROMs, browse/search, game detail) against the built container + MariaDB in CI.
- Coverage reported in CI (no hard gate initially **[OPEN]**).

## 16. Dev tooling (`/scripts`, PowerShell 7)

| Script | Does |
|---|---|
| `setup-dev.ps1` | Checks prerequisites (.NET 10 SDK, Node LTS, Docker), `npm ci`, `dotnet restore`, starts a local MariaDB via `docker compose -f docker-compose.dev.yml`, creates `./.dev/{roms,ingest,logs,cache}` |
| `start-dev.ps1` | Starts API (`dotnet watch`) and Vite dev server (proxy `/api` → API) |
| `test-all.ps1` | Runs `dotnet test` (incl. Testcontainers) + `npm test`; non-zero exit on failure |
| `build-docker.ps1` | Builds image locally, tag `niemandr/rnrommanager:dev` |
| `seed-ingest.ps1` | Generates synthetic test ROMs into a given (temp) ingest folder |

## 17. Container

- Multi-stage Dockerfile: `node` stage builds UI → `sdk:10.0` stage publishes API (with UI in `wwwroot`) → runtime.
- Runtime base: **[OPEN]** `aspnet:10.0-noble-chiseled-extra` (no shell, smallest attack surface) vs. `aspnet:10.0-alpine` (has a shell for debugging). PUID/PGID is **not** needed, so either works.
- Runs as non-root (UID 1654 `app`); volumes: `/app/roms`, `/app/ingest`, `/app/logs`, `/app/cache`. No PUID/PGID mapping: users set `user:` in compose and own the volumes accordingly (documented in `docs/deployment.md`).
- Port `8080`. Health endpoint `/health` (DB reachable, paths writable).
- Example `docker-compose.yml` in docs.

## 18. CI/CD (GitHub Actions)

- `ci.yml` on PR + push: restore, build, `dotnet test`, UI lint/test/build.
- `release.yml` on push to `main` and tags `v*`: run all tests → build multi-arch image (`linux/amd64,linux/arm64` via buildx + QEMU) → push `niemandr/rnrommanager:{latest, x.y.z, sha}`.
- Secrets: `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN`.
- Versioning: **MinVer** — version comes from git tags `vX.Y.Z`; pushes to `main` get a pre-release version and tag `latest`/`sha`; pushing a `v*` tag publishes `X.Y.Z`, `X.Y`, `latest`.

## 19. Documentation (maintained alongside code)

- `README.md` — what it is, screenshots, quick start (docker compose), legal disclaimer.
- `docs/deployment.md`, `docs/configuration.md`, `docs/features.md`, `docs/zip-format.md` (info.json schema), `docs/adding-a-system.md`, `docs/development.md`.
- `CONTRIBUTING.md`, `LICENSE` (MIT), Docker Hub description synced from README.
- Rule: each implementation step updates the relevant doc in the same PR.

## 20. Open questions (summary)

1. Container runtime base: chiseled vs. Alpine (§17).
2. DAT source: always latest libretro-database `master`, or pinned to a commit SHA updated by the app/maintainer (§10)?
3. Logging: file retention (days), and an Admin "Download logs" button (§13)?
4. Test assertions: Shouldly vs. plain NUnit constraints; file system: real temp dirs vs. `System.IO.Abstractions`; coverage gate (§15)?
5. Integrity quick-check interval (default 24h?) (§4, §12).
6. Confirm `info.json` schema v1 (§6.1), solution layout (§3), and polling for job progress (§11).

## 21. Decisions log

| # | Date | Decision |
|---|---|---|
| D1 | 2026-11-04 | GBC-only (`0xC0`) and dual-mode (`0x80`) carts are stored under `roms/gbc/`; original GB under `roms/gb/`. Header CGB flag decides, not the extension. |
| D2 | 2026-11-04 | ROMs with no DAT match go to an Admin **Review queue** in the UI (file stays in ingest until resolved). |
| D3 | 2026-11-04 | Source files are **deleted** after the ZIP is written and hash-verified. |
| D4 | 2026-11-04 | Roles: `Admin` + `User`. |
| D5 | 2026-11-04 | Per-system ingest folders; header overrides the folder if they disagree. |
| D6 | 2026-11-04 | `.zip` and `.7z` archives in ingest are unpacked and each ROM inside ingested (SharpCompress for 7z). |
| D7 | 2026-11-04 | Uppercase `A`–`Z` + `#` buckets; leading "The" is kept (bucket = first char of ZIP name). |
| D8 | 2026-11-04 | Bootstrap Icons primary, Font Awesome Free for gaps. |
| D9 | 2026-11-04 | Hacks/translations go into the parent game ZIP, flagged in `info.json`. |
| D10 | 2026-11-04 | Integrity: DB auto-repaired from `info.json`; file problems reported only. Quick scheduled check + deep re-hash on demand. |
| D11 | 2026-11-04 | Docker image `linux/amd64` + `linux/arm64`, non-root, no PUID/PGID. |
| D12 | 2026-11-04 | MinVer (git-tag versions) + MIT license. |
| D13 | 2026-11-04 | Theme preference per browser (`localStorage`), default auto. |
| D14 | 2026-11-04 | App uses its own database on the shared MariaDB, no table prefix. |
| D15 | 2026-11-04 | DbUp, SvelteKit static SPA, Testcontainers MariaDB accepted. |
| D16 | 2026-11-04 | UI tests: Vitest + Playwright smoke tests in CI. |
