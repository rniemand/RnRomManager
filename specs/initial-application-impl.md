# RnRomManager — MVP Implementation Plan

> Breaks [initial-application.md](initial-application.md) (the **spec**, v0.11) into small steps that end with the MVP running on the owner's host. The spec is the source of truth for *what* to build; this file says *in what order* and *when a step is done*. `§n` always refers to a section of the spec.

## 1. How to use this plan

### 1.1 The per-step loop
1. `git switch main && git pull`.
2. Pick the **first unticked step** in the tracker (§3). Its dependencies are always earlier steps.
3. `git switch -c step/NN-slug` (e.g. `step/07-db-plumbing`).
4. Read the step's **Spec** sections in full, plus anything they link to that the step touches.
5. Build exactly the step's **Deliverables**. Nothing from later steps, no "for later" code.
6. Write the step's **Tests**. Every non-trivial class gets unit tests (§15.1 rule).
7. Check every item in the step's **Done when** list, plus the global Definition of Done (§1.2).
8. Tick the step in §3 (`[x]`, PR number) **in the same branch**.
9. Commit, push, open a PR titled `SNN: <step title>`. Wait for CI to pass (from S06 on), then squash-merge.

One step = one branch = one PR. If a step turns out bigger than expected, split it into `NNa`/`NNb` rows in the tracker rather than leaving it half done.

### 1.2 Definition of Done (every step)
- `dotnet build -c Release -warnaserror` passes, and `dotnet test` is green (once S07 lands, that includes Testcontainers; Docker must be running).
- From S02: `npm run lint`, `npm run check`, `npm test` and `npm run build` pass in `src/RnRomManager.Web`.
- From S06: `pwsh scripts/test-all.ps1` passes locally, and the PR's CI is green.
- Docs that the step's behaviour belongs to are updated (§19 table; S50 does the final pass, but nothing waits for it).
- No secrets, `.dev/` content, or real ROMs committed.
- New spec gaps are never guessed at: see §1.3.

### 1.3 When the spec is unclear or wrong
1. Stop that part of the work.
2. Add the question to spec §20 "Open questions".
3. Ask the owner. Once it's answered, write the decision into the spec section itself **and** spec §21 as the next `D<n>`, in the same PR as the code.
4. Never change the spec silently to match the code.

### 1.4 Code conventions (apply from S01)
- **Solution:** `RnRomManager.slnx` at the repo root. `global.json` pins SDK `10.0.x` (`rollForward: latestFeature`).
- **`Directory.Build.props`:** `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`, `InvariantGlobalization=false` (NFC normalisation, §6.1, needs ICU; the `-extra` runtime image has it).
- **`Directory.Packages.props`:** central package management. Every package version lives there.
- **Namespaces** follow the project name (`RnRomManager.Core.Ingestion`, …). One public type per file.
- **Time:** inject `TimeProvider` everywhere (lockout, schedule, file age, retention), so tests use `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`).
- **API:** minimal APIs grouped per area in `RnRomManager.Api/Endpoints/<Area>Endpoints.cs` (`MapAuthEndpoints`, …). One `Problems` helper builds ProblemDetails with `code` + `traceId` (§23). Request DTOs validated in the endpoint and returned as `400 ValidationFailed` with `errors{}`.
- **Data:** one repository per table in `RnRomManager.Data/Repositories`, interfaces in Core, Dapper SQL inline. Connections come from one `IDbConnectionFactory`.
- **Tests:** NUnit `Assert.That`, NSubstitute, real temp dirs (`TempDir` helper in `RnRomManager.TestData`, deleted in `TearDown`). Testcontainers fixtures are `[SetUpFixture]`-scoped (one MariaDB per test assembly).
- **Frontend:** `src/lib/api/` (fetch wrapper + typed calls), `src/lib/components/`, `src/routes/` per page. Svelte 5 runes only.

## 2. Before you start (owner, one-off)
These are manual and can't be done by an agent. Each step that needs one says so.

| # | Task | Needed by |
|---|---|---|
| P1 | Install .NET 10 SDK, Node ≥ 24, Docker Desktop, PowerShell 7 (all present on the dev machine as of 2026-11-04) | S01 |
| P2 | Create the Docker Hub repo `niemandr/rnrommanager` (public) and a PAT with Read/Write/Delete | S06 |
| P3 | GitHub repo `rniemand/RnRomManager` → Settings → Secrets → Actions: `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN` | S06 |
| P4 | Branch protection on `main`: require PRs + the `backend` and `web` checks (add `e2e` after S49) | after S06 |
| P5 | Deployment host with Docker + Compose; decide the data folder and the host UID/GID that owns it (§17.2) | S52 |
| P6 | On the shared MariaDB: `CREATE DATABASE rnrommanager CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;` + a user with `ALL` on it only (§2) — or use the compose MariaDB from §17.3 | S52 |

## 3. Tracker

| ✓ | Step | Title | PR |
|---|---|---|---|
| [x] | S01 | Repo + solution scaffold | #1 |
| [x] | S02 | Web app scaffold | #2 |
| [x] | S03 | API host, health/live, static UI, `--healthcheck` | #3 |
| [x] | S04 | Configuration, NLog, startup steps 1–4 | #4 |
| [x] | S05 | Dockerfile + `build-docker.ps1` | #5 |
| [x] | S06 | CI + release workflows, `test-all.ps1` | #6 |
| [x] | S07 | DB plumbing, DbUp, dev DB, `/health/ready` | #7 |
| [x] | S08 | Schema migrations (all tables) | #8 |
| [x] | S09 | Users, password hashing, admin seed, Data Protection keys | #9 |
| [x] | S10 | Login, session, antiforgery, lockout, rate limit | #10 |
| [x] | S11 | User admin endpoints + `--reset-password` CLI | #11 |
| [x] | S12 | Security headers + CSP hashes | #12 |
| [x] | S13 | `SafePath` + limit stream | #13 |
| [x] | S14 | `SyntheticRomBuilder` | #14 |
| [x] | S15 | GB/GBC header extractor | #15 |
| [x] | S16 | `ISystemHandler` + GB/GBC handlers | #16 |
| [x] | S17 | Naming + tag parsing + primary ranking | #17 |
| [x] | S18 | DAT parser, DAT store, `FixtureDatWriter` | #18 |
| [x] | S19 | `info.json` model, validation, upgraders | #19 |
| [x] | S20 | Library ZIP reader/writer + PNG IHDR | |
| [x] | S21 | Job runner + jobs API + crash recovery | |
| [x] | S22 | Game repositories + mirror write + DB auto-repair | |
| [ ] | S23 | HTTP client + `DatRefresh` job + DAT loading at startup | |
| [ ] | S24 | Thumbnails + media service | |
| [ ] | S25 | Ingest job start: pre-pass, snapshot, pending API | |
| [ ] | S26 | Ingest pipeline: loose-file happy path | |
| [ ] | S27 | Ingest: dedupe, self-repair, failures | |
| [ ] | S28 | Ingest: archives (zip + 7z) | |
| [ ] | S29 | Ingest: review queue + suggestions | |
| [ ] | S30 | Review resolution + DAT search API | |
| [ ] | S31 | TestData CLI, seed sets, `seed-ingest.ps1` | |
| [ ] | S32 | Library read API (systems, list, detail, images, downloads) | |
| [ ] | S33 | Library edits: PATCH, delete release/game | |
| [ ] | S34 | Library edits: move + merge | |
| [ ] | S35 | Media retry (per game + `MediaRetry` job) | |
| [ ] | S36 | Integrity checker | |
| [ ] | S37 | Scheduler + integrity API | |
| [ ] | S38 | Admin settings, log download, about | |
| [ ] | S39 | UI foundation: fetch wrapper, auth, layout, theme | |
| [ ] | S40 | UI: Login + Change password | |
| [ ] | S41 | UI: Library page | |
| [ ] | S42 | UI: Game detail (read + download) | |
| [ ] | S43 | UI: Game detail admin actions | |
| [ ] | S44 | UI: Ingestion + Job detail | |
| [ ] | S45 | UI: Review queue | |
| [ ] | S46 | UI: Integrity page | |
| [ ] | S47 | UI: Admin pages | |
| [ ] | S48 | Dev scripts complete (`setup-dev`, `start-dev`) | |
| [ ] | S49 | E2E: Playwright + axe, compose, CI job | |
| [ ] | S50 | Documentation pass | |
| [ ] | S51 | Release `v0.1.0` | |
| [ ] | S52 | Deploy to the owner's host + smoke test | |

**Milestones:** M1 walking skeleton ships an `edge` image (S06) · M2 DB + auth (S12) · M3 domain core (S20) · M4 ingestion works end to end via API (S31) · M5 full API (S38) · M6 full UI (S47) · M7 MVP live (S52).

## 4. Steps

Format: **Spec** = sections to read · **Deliverables** = what exists after the step · **Tests** · **Done when** = checks on top of §1.2.

---

### M1 — Walking skeleton

#### S01 — Repo + solution scaffold
- **Spec:** §2, §3 (solution layout), §18 (tool manifest, versioning), §19 (`LICENSE`, `CONTRIBUTING.md`).
- **Deliverables:**
  - `.gitignore` (dotnet + node + `.dev/`, `appsettings.Development.json`, `coverage`, Playwright output), `.gitattributes` (`* text=auto`, `*.7z binary`, `*.png binary`), `.editorconfig`.
  - `global.json`, `Directory.Build.props`, `Directory.Packages.props` (§1.4), `RnRomManager.slnx`.
  - Empty class-library projects: `Core`, `Data`, `Systems.GameBoy`; `Api` (web, empty `Program.cs` that returns 0); test projects `Core.Tests`, `Data.Tests`, `Systems.GameBoy.Tests`, `Api.Tests` (NUnit + NSubstitute + `Microsoft.NET.Test.Sdk` + `coverlet.collector`); `TestData` (console app, also referenced as a library by the test projects). Project references follow §3 (Api → Core, Data, Systems.GameBoy; Data → Core; Systems.GameBoy → Core).
  - `.config/dotnet-tools.json` with `minver-cli` and `dotnet-reportgenerator-globaltool`.
  - `LICENSE` (MIT, Richard Niemand), `CONTRIBUTING.md` (the §1.1 loop, test rule, doc rule), README stub (title + one-line description).
- **Tests:** none. Empty test projects are fine; don't add placeholder tests.
- **Done when:** `dotnet tool restore`, `dotnet build -c Release -warnaserror` and `dotnet test` succeed; `dotnet minver --tag-prefix v` prints `0.0.0-alpha.0.N`.

#### S02 — Web app scaffold
- **Spec:** §2 (frontend rows), §11 (first bullet: `theme.js`, `app.html`).
- **Deliverables:** `src/RnRomManager.Web` — SvelteKit + TypeScript, `@sveltejs/adapter-static` with `fallback: 'index.html'`, `ssr = false` + `prerender = false` in the root `+layout.ts`; Bootstrap 5.3, `bootstrap-icons`, `@fortawesome/fontawesome-free`; ESLint (flat config) + Prettier + `prettier-plugin-svelte`; Vitest + `@testing-library/svelte` + jsdom; `svelte-check`. npm scripts: `dev`, `build`, `lint` (eslint + `prettier --check`), `format`, `check`, `test`. `static/theme.js` (sets `data-bs-theme` from `localStorage` or `prefers-color-scheme`) loaded by a blocking `<script src="/theme.js">` in `app.html`. Placeholder home page. `vite.config.ts` dev proxy `/api` and `/health` → `http://localhost:5080`. `.nvmrc` = `24`. `package.json` `engines.node` = `>=24`.
- **Tests:** a Vitest test for the theme-resolution logic (export it from a small `theme.ts` that `theme.js` mirrors, or test `theme.js` directly).
- **Done when:** `npm ci && npm run lint && npm run check && npm test && npm run build` pass; `build/index.html` exists.

#### S03 — API host, health/live, static UI, `--healthcheck`
- **Spec:** §3.1 (none yet), §11 (static cache headers), §17.1 (`--healthcheck`, `/health/live`), §23 (unknown `/api/*` → 404 ProblemDetails, SPA fallback, no CORS).
- **Deliverables:** `Program.cs` with a `CliDispatcher`: `--healthcheck` handled first (GET `http://127.0.0.1:<first ASPNETCORE_HTTP_PORTS port or 8080>/health/live`, 3 s timeout, exit 0/1), else build the web host. `/health/live` (anonymous, 200, no body details). Static files from `wwwroot` with the §11 cache headers; `MapFallbackToFile("index.html")` excluding `/api` and `/health`; `/api/{**rest}` → 404 ProblemDetails (`code: NotFound`). The `Problems` helper (§1.4). MSBuild target: in `Release`, copy `../RnRomManager.Web/build/**` into `wwwroot` only if present (the Dockerfile does the real copy).
- **Tests (Api.Tests, `WebApplicationFactory`):** `/health/live` 200; `/api/nope` 404 with `code`+`traceId`; `/some/route` returns `index.html` (test `wwwroot` fixture); `/_app/immutable/x.js` gets the immutable cache header; `--healthcheck` port parsing unit test.
- **Done when:** `dotnet run --project src/RnRomManager.Api` serves `/health/live`.

#### S04 — Configuration, NLog, startup steps 1–4
- **Spec:** §4 (whole table + bullets), §4.1 steps 1–4, §13 (all except retention purge and log download), §5 (folder creation of `roms/`, `ingest/`, `logs/`, `cache/` roots only; system folders come in S16).
- **Deliverables:** options classes (`PathsOptions`, `AdminOptions`, `IntegrityOptions`, `LoggingOptions`, `SourcesOptions`) + validators (`IValidateOptions<T>`, `ValidateOnStart`), covering: required, absolute paths, the four paths distinct and not nested, `HH:mm` or empty, `RetentionDays` 1–365, URLs, `InitialPassword` empty or ≥12/≤128 and ≠ `admin`. `nlog.config` with the §13 layout/targets/rules; `Paths:Logs`, `MinLevel` injected as NLog variables; `ClearProviders().SetMinimumLevel(Trace)` + `UseNLog()`. Startup: log version + redacted effective config (`Info`), create folders, write probe (`.rnrm-probe`, message names path + UID/GID via `Environment` / `getuid` P/Invoke on Linux). Any failure → `Fatal`, `LogManager.Shutdown()`, exit 1. A `StartupRunner` class owns the §4.1 sequence; later steps add their steps to it.
- **Tests:** every validation rule (valid + invalid); nested/duplicate paths; write probe failure on a read-only temp dir (skip on Windows if needed, with `Assume.That`); redaction of `Password` in the logged connection string.
- **Done when:** running with a bad `Paths__Roms=relative` exits 1 with a `Fatal` line.

#### S05 — Dockerfile + `build-docker.ps1`
- **Spec:** §17.1, §17.2, §16 (`build-docker.ps1`).
- **Deliverables:** multi-stage `Dockerfile` (`node:24` UI stage and `sdk:10.0` publish stage on `$BUILDPLATFORM`, `TARGETARCH` → `x64`/`arm64` mapping, `ARG VERSION=0.0.0-dev`, `-p:Version=$VERSION`), runtime `aspnet:10.0-noble-chiseled-extra`, `/app/{roms,ingest,logs,cache}` owned by `1654:1654` via `COPY --chown`, `USER 1654`, `EXPOSE 8080`, `VOLUME`s, `HEALTHCHECK` using `--healthcheck`, `ENTRYPOINT`. `.dockerignore` (§17.1). `scripts/build-docker.ps1` (`-Tag`, `-Platform`, `-Version` default from `dotnet minver`).
- **Tests:** none automated (covered by S49 e2e).
- **Done when:** `pwsh scripts/build-docker.ps1` builds; `docker run -e ConnectionStrings__Default=x niemandr/rnrommanager:dev` reaches `healthy` and serves the placeholder UI on `:8080`.

#### S06 — CI + release workflows, `test-all.ps1`
- **Spec:** §18 (whole), §16 (`test-all.ps1`). Owner tasks **P2, P3** must be done before merging.
- **Deliverables:** `.github/workflows/ci.yml` (jobs `backend` + `web`; `e2e` comes in S49), `.github/workflows/release.yml` (exactly §18 steps 1–6), `scripts/test-all.ps1` (`-SkipIntegration` → `--filter "Category!=Integration"`, `-SkipWeb`, `-Coverage` → ReportGenerator HTML into `./.dev/coverage`). Mark Testcontainers tests `[Category("Integration")]` from S07 on.
- **Done when:** the PR's CI is green; after merge, `release.yml` on `main` pushes `niemandr/rnrommanager:edge` + `sha-<7>` for amd64 + arm64 (check on Docker Hub). Then do owner task **P4**.

---

### M2 — Database + auth

#### S07 — DB plumbing, DbUp, dev DB, `/health/ready`
- **Spec:** §2 (data access, DB rows), §4.1 steps 5–6, §14 intro, §14.3, §17.1 (`/health/ready`), §16 (`docker-compose.dev.yml` part of `setup-dev.ps1`).
- **Deliverables:** `IDbConnectionFactory` that forces `DateTimeKind=Utc`, `GuidFormat=Char36` via `MySqlConnectionStringBuilder`. DB wait (60 s, every 3 s; unknown database / access denied → `Fatal` naming the database). DbUp runner (`dbup-mysql`, embedded scripts, `JournalToMySqlTable(db, "schema_versions")`, log through NLog). The runner ships with zero scripts (tables come in S08). `/health/ready` (DB `SELECT 1` within 2 s + the four paths exist → 200 else 503). `docker-compose.dev.yml` (mariadb:11.4 on 3306, db/user `rnrommanager`). Data.Tests `MariaDbFixture` (Testcontainers `mariadb:11.4`).
- **Tests:** connection-string forcing; DbUp on an empty DB succeeds and creates `schema_versions`; DB wait gives up after the timeout (use a short override in the test); `/health/ready` 200 / 503 (stub the DB check).
- **Done when:** the app starts against the dev DB and `/health/ready` is 200.

#### S08 — Schema migrations (all tables)
- **Spec:** §14.1 (every table), §14 intro (charset, types), §14.3.
- **Deliverables:** one script per table in dependency order: `0001_users.sql`, `0002_games.sql`, `0003_roms.sql`, `0004_images.sql`, `0005_jobs.sql`, `0006_job_items.sql`, `0007_ingest_review_items.sql`, `0008_integrity_issues.sql`, `0009_data_protection_keys.sql`. Every column, nullability, FK, `ON DELETE CASCADE`, unique key and index exactly as §14.1. Booleans `TINYINT(1) NOT NULL DEFAULT 0`.
- **Tests (Testcontainers):** migrate from scratch; re-running is a no-op; `information_schema` check that every §14.1 unique/index exists; a case-insensitive unique test on `users.username` and `games.zip_path`.
- **Done when:** the app starts on a fresh dev DB and all 9 tables exist.

#### S09 — Users, password hashing, admin seed, Data Protection keys
- **Spec:** §9.1 (accounts, admin seed, usernames, password policy), §9.2 (Data Protection keys), §4.1 step 8.
- **Deliverables:** `UserRepository`; `PasswordPolicy` + `UsernamePolicy` validators; `PasswordHasher<User>`; admin seed when `users` is empty (`InitialPassword` or 20 random URL-safe chars written to `Console.Out` only, `must_change_password = true`, random `security_stamp`). `DbXmlRepository : IXmlRepository` over `data_protection_keys`, `SetApplicationName("RnRomManager")`.
- **Tests:** policy rules; seed runs once (second start doesn't re-seed); generated password never reaches the NLog file (capture the file target); keys persist across two `WebApplicationFactory` instances on the same DB (Testcontainers).
- **Done when:** first start against an empty DB prints the admin password to the console.

#### S10 — Login, session, antiforgery, lockout, rate limit
- **Spec:** §9.1 (lockout, rate limit, must-change gate), §9.2 (cookie, security stamp, antiforgery), §23 `/api/auth/*` rows, §23.1 auth codes.
- **Deliverables:** cookie auth (`rnrm.auth`, HttpOnly, `SameSite=Strict`, `Secure=SameAsRequest`, persistent, sliding 7 days, 401/403 instead of redirects); `OnValidatePrincipal` re-checks `security_stamp` + `is_disabled`; policies `Authenticated` / `AdminOnly` applied to every `/api` group (deny by default; login + antiforgery anonymous); must-change-password middleware/filter (`403 PasswordChangeRequired` except `me`, `change-password`, `logout`); `IAntiforgery` with `X-XSRF-TOKEN`, `GET /api/auth/antiforgery` sets `XSRF-TOKEN`, validation filter on every mutating `/api` call → `400 AntiforgeryInvalid`; login/logout/me/change-password endpoints (re-sign current session with the new stamp; re-issue the token); lockout 5 → 15 min (via `TimeProvider`); `RateLimiter` fixed window 10/min/IP on login → `429`; forwarded headers honoured only via `ASPNETCORE_FORWARDEDHEADERS_ENABLED`; `last_login_at`.
- **Tests (Api.Tests + Testcontainers):** happy login; wrong password → generic 401; lockout after 5 and reset after success / after 15 min; 11th login in a minute → 429; missing XSRF header → 400; must-change gate; change-password kills other sessions but keeps the current one; disabled user's session dies.
- **Done when:** all §15.1 "Auth" rows except the CLI and last-admin are covered.

#### S11 — User admin endpoints + `--reset-password` CLI
- **Spec:** §9.1 (last admin, users disabled not deleted, CLI recovery D37), §23 `/api/users*` rows.
- **Deliverables:** `GET/POST /api/users`, `PATCH /api/users/{id}`, `POST /api/users/{id}/reset-password` (clears lockout, sets must-change, rotates stamp); `409 UsernameTaken`, `409 LastAdmin`. CLI `--reset-password <username>` in `CliDispatcher` (§4.1 steps 1, 2, 5 only; promote to Admin if no enabled Admin would remain; stderr + exit 1 on failure; `Warn` log with username only; `LogManager.Shutdown()`).
- **Tests:** role matrix (User → 403, anon → 401) for the four endpoints; last-admin disable/demote → 409; CLI: unknown user → exit 1, success rotates the stamp and re-enables, last-admin promotion.
- **Done when:** §15.1 "Auth" row fully covered.

#### S12 — Security headers + CSP hashes
- **Spec:** §9.2 (response headers bullet).
- **Deliverables:** middleware that adds CSP / `nosniff` / `no-referrer`; at startup read `wwwroot/index.html`, SHA-256 every inline `<script>` body, build `script-src 'self' 'sha256-…'`. Missing `index.html` (API-only dev) → `script-src 'self'`.
- **Tests:** hashes from a sample `index.html` with two inline scripts; headers present on API and static responses.
- **Done when:** the built SPA in the Docker image loads with no CSP errors in the browser console.

---

### M3 — Domain core (pure code, unit-tested)

#### S13 — `SafePath` + limit stream
- **Spec:** §9.3, §7.3 (limit stream on actual bytes).
- **Deliverables:** `SafePath.Resolve(root, relative)` + `UnsafePathException`; `LimitStream` (throws a typed `LimitExceededException` after N bytes read); `FileNameRules` helper for the `(2)`, `(3)` suffix used by §5 moves and §6.1 clashes.
- **Tests:** traversal (`..`, absolute, rooted, mixed separators), sibling-prefix trick (`/app/roms2`), limit exact/over, suffix generation.

#### S14 — `SyntheticRomBuilder`
- **Spec:** §15.2 (builder bullet), §8.1 (field offsets), Pan Docs logo bitmap.
- **Deliverables:** `RnRomManager.TestData.SyntheticRomBuilder` (seeded deterministic bytes; title, CGB/SGB flags, cart type, ROM/RAM size codes, licensee old/new, destination, version, logo; computes header + global checksum; options to corrupt either checksum, the logo or the size; `BuildMbc1Multicart()`), and `TempDir`.
- **Tests:** same seed → same bytes; checksums valid by the §8.1 formulas; corruption options flip only what they say.

#### S15 — GB/GBC header extractor
- **Spec:** §8 (all), §22 (`RomHeader`).
- **Deliverables:** `GameBoyHeaderReader` in `Systems.GameBoy` returning `RomHeader` with the §8.2 `Fields`; embedded resources for the old/new licensee tables and cart types (§8.3); classification rule (bit 7); `IsPlausible = logoValid || headerChecksumValid`; MBC1M detection; `< 0x150` bytes → null.
- **Tests:** every §15.1 "GB header extractor" row, using S14.

#### S16 — `ISystemHandler` + GB/GBC handlers
- **Spec:** §22 (whole), §5 (system/letter/`_*` folder creation), §6.1 (default box art), §8.4 (`MaxRomSizeBytes`).
- **Deliverables:** `ISystemHandler`, `RomHeader` in Core; `services.AddSystemHandler<T>()` + `ISystemRegistry` (by `Id`, by `Family`, by extension); `GameBoyHandler` (`gb`) and `GameBoyColorHandler` (`gbc`), family `gameboy`, `MinRomSizeBytes = 0x150`, `MaxRomSizeBytes = 16 MiB`, DAT names; two embedded 512×512 default box-art PNGs (simple cartridge silhouette + system name, made for this project — draw as SVG, export PNG, commit the SVG source too). Startup creates every `roms/<id>/{#,A..Z}` and `ingest/<id>/{_duplicates,_failed,_removed}`.
- **Tests:** registry lookups; folder creation idempotent; default PNGs are valid PNG 512×512.

#### S17 — Naming + tag parsing + primary ranking
- **Spec:** §6.1 (table + bullets), §6.1.1, §5 (bucket rule).
- **Deliverables:** `NoIntroName.Parse(romName)` → base title, regions, languages, revision, flags; embedded region list (complete No-Intro country list); `DisplayTitle.From(title)`; `FileNameSanitizer.Sanitize`; `Bucket.For(zipName)`; `RevisionComparer`; `PrimaryReleaseRanker`.
- **Tests:** every §15.1 "Naming" row. Include real DAT-style names (`Yelu Wangzi (Prince Yeh Rude) (Taiwan)`, `seaQuest DSV`, `Maus, Die`, `Legend of Zelda, The - Link's Awakening (USA, Europe) (Rev 2)`).

#### S18 — DAT parser, DAT store, `FixtureDatWriter`
- **Spec:** §10 intro, §10.1 (format, merge rules, `dats.json`, load-time SHA1 check), §15.2 (`FixtureDatWriter`).
- **Deliverables:** `ClrMameProParser` (tokenizer for quoted strings + nested parens); no-intro and metadat models; `DatStore` per system: SHA1 → entry, CRC → merged metadata, base-title name list; `DatCache` that loads `cache/dats/` + `dats.json` (missing/corrupt → `Error`, system has no DATs); `TestData.FixtureDatWriter` (writes 5 files per system + `dats.json`).
- **Tests:** every §15.1 "DAT parser" row; writer → loader round trip.

#### S19 — `info.json` model, validation, upgraders
- **Spec:** §6.2 (all), §9.3 (1 MiB limit).
- **Deliverables:** `InfoJson` records (game, rom, image) with `System.Text.Json` options (camelCase, indented, nulls written, no BOM); `InfoJsonValidator` (every validation bullet, returns the first failing rule); `IInfoJsonUpgrader` chain (empty for v1, but the chain runs); `InfoJsonReader` result: `Ok` / `Invalid(rule)` / `NewerSchema`.
- **Tests:** round trip; each validation rule; unknown properties ignored; newer schema detected; upgrader chain invoked with a fake v0 upgrader in a test.

#### S20 — Library ZIP reader/writer + PNG IHDR
- **Spec:** §6 (layout), §6.1 (writes start from the ZIP, atomic write, compression, path change), §3.1 (FileShare + move retry), §9.3 (library ZIP read rules), §10.2 (PNG validation).
- **Deliverables:** `LibraryZip.Open(path)` (reads `info.json` + entry list via limit streams; `FileShare.Read | FileShare.Delete`); `LibraryZipWriter.Write(target, existingZipOrNull, changes)` → streams to `<name>.zip.tmp`, re-opens and verifies every ROM SHA1, `File.Move(overwrite)` with 3× 500 ms retry, deletes tmp on failure; `PngInfo.Read(bytes)` (signature + IHDR width/height, ≤ 5 MiB).
- **Tests:** every §15.1 "ZIP writer" row that doesn't need the DB; verify failure leaves the original intact; corrupt existing entry → `VerifyFailed` naming the entry.

---

### M4 — Jobs, persistence, external data

#### S21 — Job runner + jobs API + crash recovery
- **Spec:** §3.1 (whole), §7.4, §14.1 (`jobs`, `job_items`), §23 `/api/jobs/current`, `/api/jobs`, `/api/jobs/{id}`, `/api/jobs/{id}/items`, `/api/jobs/{id}/cancel`.
- **Deliverables:** `JobLock` (`SemaphoreSlim(1)` + `TryAcquire`), `JobRunner` (create row, run in background, progress in memory, cancel flag, status transitions, `Failed` with `error`, final-update-failure handling, `BeginScope(JobId)`), `IJob` interface, `HostOptions.ShutdownTimeout = 30 s` + cancel on stop; `JobRepository`, `JobItemRepository`; crash recovery at startup (§4.1 step 7: `Running` → `Aborted`, delete `cache/work/*` and every `*.zip.tmp`). A `NoopTestJob` exists in tests only.
- **Tests:** lock busy → `JobRunning`; cancel → `Cancelled`; exception → `Failed`; shutdown cancels; crash recovery; API role matrix + `204` when idle + `404` cancel when not running.

#### S22 — Game repositories + mirror write + DB auto-repair
- **Spec:** §14.1 (`games`, `roms`, `images`), §14.2 (sync rule), §12.2 (DB auto-repair bullet), §7.1 step 4.
- **Deliverables:** `GameRepository` / `RomRepository` / `ImageRepository`; `LibraryMirror.Replace(InfoJson, zipPath, size, mtime)` = one transaction (upsert `games`, delete + insert `roms`/`images`, `region_key`, `has_default_boxart`, `bucket`); `LibraryMirror.Delete(gameId)`; `DbSync.SyncAll(roots)` = the shared auto-repair used by the ingest pre-pass and integrity (new ZIP → insert, changed size/mtime → update, missing ZIP → delete unless the `gameId` is at another path; skip rules for `DuplicateGameId` / `DuplicateSha1` / unreadable ZIPs; returns a change list).
- **Tests (Testcontainers + temp dirs):** replace is idempotent; `roms.sha1` unique enforced; sync inserts/updates/deletes/moves; skip rules.

#### S23 — HTTP client + `DatRefresh` job + DAT loading at startup
- **Spec:** §10.1 (refresh), §10.4, §4.1 step 9, §23 `POST /api/jobs/dat-refresh`, `GET /api/admin/dats`.
- **Deliverables:** named `HttpClient`s with `Microsoft.Extensions.Http.Resilience` (20 s, 3 retries exp. from 1 s on 5xx/408/timeout, no 404 retry), `User-Agent`, `Debug` request logging; `DatRefreshJob` (download 10 files to a temp dir → parse → swap into `cache/dats/` → `dats.json` → reload `DatStore`; any failure keeps the old set; cancel between downloads); startup DAT load; the two endpoints.
- **Tests:** stub `HttpMessageHandler`: success swaps all; one 404/parse error keeps the old set and the job is `Failed`; cancel keeps the old set; `GET /api/admin/dats` lists versions.
- **Done when:** on the dev stack, `POST /api/jobs/dat-refresh` downloads the real DATs (manual check).

#### S24 — Thumbnails + media service
- **Spec:** §10.2 (key, lookup order, validation, empty base URL, circuit breaker), §6.2 Image fields, §6.1 (default box art).
- **Deliverables:** `ThumbnailKey.For(datGameName)`; `MediaService.Find(releases in primary order, breaker)` → box art + snap results (or default); `CircuitBreaker` (per job instance).
- **Tests:** every §15.1 "Thumbnail key + lookup" row except `MediaRetry` (S35), using a stub handler.

---

### M5 — Ingestion (API only)

#### S25 — Ingest job start: pre-pass, snapshot, pending API
- **Spec:** §7.1 (all), §5 (ignored files, case-insensitive extensions), §23 `GET /api/ingest/pending`, `POST /api/jobs/ingest`.
- **Deliverables:** `IngestJob` skeleton: `DatsMissing` check before the row, pre-pass via `DbSync`, delete stale `Pending` review rows, snapshot with 10 s age rule (`Skipped TooRecent` items), progress; `IngestFileScanner` (non-recursive, junk/dot-file filter) shared with the pending endpoint.
- **Tests:** `409 DatsMissing` creates no row; pre-pass fixes a ZIP without a DB row; too-recent files skipped; junk ignored.

#### S26 — Ingest pipeline: loose-file happy path
- **Spec:** §7.2 steps 1, 2, 4, 6–11 for DAT-matched loose files; §6.1 naming/clash; §5 bucket + system override; §13 per-item scope/logging.
- **Deliverables:** `IngestPipeline.Process(item)` running the steps in order with one `job_items` row per item (`FileId` scope); new game + new release into an existing game; metadata fill rules (step 7); media (step 8, via S24); package (S20); mirror (S22); delete source. `DbWriteFailed` stops the job as `Failed`.
- **Tests:** ingest a DAT-matched GB ROM → ZIP at the right bucket with correct `info.json` + DB rows; second region of the same game joins the ZIP and primary is recomputed; GBC ROM dropped in `ingest/gb/` lands in `roms/gbc/`; lowercase-first-letter bucket; case-insensitive ZIP clash → ` (2)`; existing game metadata only fills empty fields; `DbWriteFailed` stops the job (substitute repository); target ZIP unreadable / newer schema → `Error`.

#### S27 — Ingest: dedupe, self-repair, failures
- **Spec:** §7.2 steps 1, 3, 5 (`InvalidHeader`), the failure column, outcomes list; §5 (`_failed/`, `_duplicates/`, `.error.txt`, ` (N)` names); §8.4 (extension rename).
- **Deliverables:** validation failures (`UnsupportedType`/`TooSmall`/`TooLarge`) → `_failed/` + `.error.txt`; global dedupe → `_duplicates/`; self-repair of an entry with an open `HashMismatch`/`EntryMissing`/`EntrySizeMismatch` issue → `Repaired`; `InvalidHeader`; IO error → `Error` with source untouched; unexpected exception → `Error Unexpected`, job continues; delete failure → `Warn`.
- **Tests:** each outcome above; name clash in `_failed/` gets ` (2)`; self-repair rewrites the entry and resolves the issue row.

#### S28 — Ingest: archives (zip + 7z)
- **Spec:** §7.2 step 1a, §7.3 (all), §3.1 (cancel waits for the whole archive), §15.2 (7z fixture).
- **Deliverables:** `ArchiveExtractor` (ZIP via `System.IO.Compression`, 7z via SharpCompress) into `cache/work/<jobId>/<fileId>/` with every limit + traversal + symlink check; entries processed as items (`archive.zip › entry.gb`); archive fate rules; work dir cleanup at job end. Commit `tests/RnRomManager.TestData/Fixtures/basic.7z` + `Fixtures/README.md` (how it was built from a fixed builder seed).
- **Tests:** every archive row of §15.1 "Ingestion pipeline": fates, limits (entries, total, per-entry), traversal, symlink, encrypted, corrupt, nested archive skipped, cancel mid-archive finishes the archive; `basic.7z` bytes == builder output.

#### S29 — Ingest: review queue + suggestions
- **Spec:** §7.2 steps 3 (review-item rules), 5, 6 (`AmbiguousGroup`); review rows on re-runs (§7.2 bullets); §10.3 suggestions; §14.1 `ingest_review_items`; §8.4 (null header → folder's handler).
- **Deliverables:** `ReviewItemRepository` (upsert on SHA1, reset to `Pending`); `SuggestionEngine` (normalise, Jaccard + Levenshtein, size boost, ≥ 0.4, top 5); `NoDatMatch` and `AmbiguousGroup` paths; re-run lifecycle (`Pending` → `Resolved`/deleted); `Dismissed` SHA1 re-drop rules.
- **Tests:** every §15.1 "Suggestions" row; review lifecycle rows of §15.1 "Ingestion pipeline".

#### S30 — Review resolution + DAT search API
- **Spec:** §7.5 (all), §7.3 (archive item resolution), §23 `/api/review*`, `/api/dat/search`, §23.1.
- **Deliverables:** `ReviewResolver` (actions `Dat` → `Unverified`, `Attach` Hack/Translated/None, `Manual`, `Dismiss`) taking the job lock and reusing the pipeline steps 6–11; `409 SourceMissing` deletes the row; endpoints `GET /api/review`, `GET /api/review/{id}`, `POST /resolve`, `POST /dismiss`, `GET /api/dat/search`.
- **Tests:** each action incl. archive entries; validation errors; `409 AmbiguousGroup` for Manual with several candidates; role matrix.

#### S31 — TestData CLI, seed sets, `seed-ingest.ps1`
- **Spec:** §15.2 (built-in sets, CLI), §15.4, §16 (`seed-ingest.ps1`).
- **Deliverables:** `RnRomManager.TestData` `Main` with `--ingest`, `--cache`, `--set Basic|Full`; the `Basic` set exactly as §15.2 (3 GB + 2 GBC games, regions/revs, an article title, dual-mode cart in `ingest/gb/`, a duplicate, an unmatched file, an invalid header, one zip + one 7z archive) and `Full` (+200 games); `scripts/seed-ingest.ps1` (`-Path`, `-CachePath`, `-Set`, `-Clean`).
- **Tests:** an Api-level integration test that seeds `Basic`, runs an ingest job and asserts the expected outcome counts (this becomes the regression test for M5).
- **Done when:** `seed-ingest.ps1` → start the API → `POST /api/jobs/ingest` (via `.http` file or curl) produces the expected library.

---

### M6 — Library, integrity, admin API

#### S32 — Library read API
- **Spec:** §23 `/api/systems`, `/api/games` (+ sort bullets, `letter=#`), `/api/games/{id}`, images, downloads; §14.2 search; §11 (48 per page, `boxArtUrl`).
- **Deliverables:** query repository for list/filter/sort/search (escaped `LIKE`, `region_key LIKE`), detail from DB rows, image bytes + ROM bytes streamed from the ZIP (`FileShare.Read | Delete`), ZIP download with range support + RFC 5987 `filename*`, `ETag`/`Cache-Control` on images.
- **Tests:** filters, sort directions + nulls last + tie-break, pagination bounds (`pageSize` max 200), search escaping (`%`, `_`), download headers, role matrix.

#### S33 — Library edits: PATCH, delete release/game
- **Spec:** §11 (library edits bullets), §6.1 (title rename → path change), §23 PATCH/DELETE rows + the mutation bullet, D28.
- **Deliverables:** `LibraryEditor` (takes the job lock): PATCH with validation (rename writes the new ZIP, commits, deletes the old one), delete release (ROM → `ingest/<system>/_removed/`, ZIP rebuilt or deleted with the last ROM), delete game.
- **Tests:** §15.1 "Library edits" rows for these actions; `409 JobRunning`, `InfoJsonNewerSchema`, `TargetZipUnreadable`, `404` stale id.

#### S34 — Library edits: move + merge
- **Spec:** §11 (move + merge bullets, D23, D32, D35), §23 move/merge rows.
- **Deliverables:** move to existing game / to a new game (runs §7.2 steps 7–8 for the new game); merge (field fill, box-art takeover, snap only if the target has none, source ZIP deleted after commit); same-system rule → `400 ValidationFailed`.
- **Tests:** §15.1 "Library edits" move/merge rows; a later ingest of the same base title follows the moved ROM; both games keeping the base title → `AmbiguousGroup` on the next ingest.

#### S35 — Media retry (per game + `MediaRetry` job)
- **Spec:** §10.2 ("Retry media", `MediaRetry` job, D38), §23 `media/retry` and `jobs/media-retry`.
- **Deliverables:** `POST /api/games/{id}/media/retry`; `MediaRetryJob` (candidates, ordered by title, Manual-only skipped, rewrite only on change, summary counts, breaker → `Failed`).
- **Tests:** §15.1 "`MediaRetry` job" items.

#### S36 — Integrity checker
- **Spec:** §12 (all), §14.1 `integrity_issues`.
- **Deliverables:** `IntegrityJob` (Quick/Deep), scope rules, every issue type, dedup/resolve rules, DB repair through `DbSync`, retention purge (180 days) at the end; `IntegrityIssueRepository`.
- **Tests:** every §15.1 "Integrity" row.

#### S37 — Scheduler + integrity API
- **Spec:** §4 (`QuickCheckTime`, DST rules, no catch-up), §4.1 step 10, §13 (log retention at startup + 00:05 UTC), §12 intro (skipped when busy), §23 `/api/jobs/integrity`, `/api/integrity/status`, `/api/integrity/issues`.
- **Deliverables:** `Scheduler : BackgroundService` driven by `TimeProvider` (local-time daily quick check, 00:05 UTC log purge); `LogRetention` purge; the three endpoints.
- **Tests:** next-run calculation incl. DST skip/repeat (use a fixed `TimeZoneInfo`), disabled schedule → `nextScheduledAt` null, busy lock → skipped + `Info`, log purge by age.

#### S38 — Admin settings, log download, about
- **Spec:** §13 (download logs), §23 `/api/admin/settings`, `/api/admin/logs`, `/api/about`, §10.4 (attribution).
- **Deliverables:** redacted effective-settings model (shared with the startup log line from S04), log ZIP (`*.log` with `FileShare.ReadWrite` + `settings.json` + `version.txt`), embedded `licenses.json` + `/api/about`.
- **Tests:** secrets masked; ZIP contents while NLog holds today's file open; role matrix.
- **Done when:** the §15.1 "API" row (role matrix for **every** endpoint) is complete — add a single table-driven test that enumerates all mapped endpoints and fails if one has no expected-role entry.

---

### M7 — UI

All UI steps: use Bootstrap classes only (no custom component library), `Icon.svelte` for every icon, the shared loading/empty/error states, keyboard-reachable controls with labels (§11 accessibility). Check each page at 360 px wide and in both themes.

#### S39 — UI foundation
- **Spec:** §11 (theme, routing/guards, shared states, navbar, icons, 404, times/sizes), §9.2 (SPA antiforgery), §23 (ProblemDetails).
- **Deliverables:** `api/client.ts` (fetch wrapper: calls `/api/auth/antiforgery` once, adds `X-XSRF-TOKEN` to mutations, 401 → `/login?returnUrl=…`, 403 `PasswordChangeRequired` → `/change-password`, ProblemDetails → typed error); auth store (`me`); root layout with navbar (role-aware, Review badge), user menu, theme toggle (Light/Dark/Auto, live `prefers-color-scheme`); `Icon.svelte`; `Loading`, `EmptyState`, `ErrorAlert`, `Toast` (for `409 JobRunning`), `ConfirmModal`; "Not allowed" + "Page not found" pages; format helpers (local time, KiB/MiB); `jobs` polling store (1.5 s while running).
- **Tests (Vitest):** fetch wrapper (XSRF header, 401 redirect), theme toggle, format helpers.

#### S40 — UI: Login + Change password
- **Spec:** §11 page table (Login, Change password), §9.1 password policy.
- **Tests:** form validation (policy hint, confirm mismatch).

#### S41 — UI: Library page
- **Spec:** §11 page table (Library), §23 `/api/games`, `/api/systems`.
- **Deliverables:** grid/list toggle (`localStorage`), filters in the URL query, debounced search, sort, paging, box-art `alt`.
- **Tests:** filters ↔ URL round trip.

#### S42 — UI: Game detail (read + download)
- **Spec:** §11 page table (Game detail, non-Admin part), §23 `/api/games/{id}`.
- **Deliverables:** box art + snap, metadata, releases table with copy buttons and expandable generic header table, download ZIP / ROM.

#### S43 — UI: Game detail admin actions
- **Spec:** §11 page table (Admin part), game typeahead bullet, library-edit bullets, stale-id `404` rule.
- **Deliverables:** Edit metadata modal, Retry media, Move release (typeahead / new title), Merge into…, Delete release, Delete game — each with a confirm modal; `GameTypeahead.svelte` (300 ms, ≥ 2 chars, same system, source excluded).
- **Tests:** typeahead debounce + exclusion; edit form validation.

#### S44 — UI: Ingestion + Job detail
- **Spec:** §11 page table (Ingestion, Job detail), polling bullet.
- **Deliverables:** pending files per system, Start/Cancel, progress bar (`aria-live`), last 10 runs, `DatsMissing` empty state; `/jobs/{id}` with summary and items table filtered by outcome.

#### S45 — UI: Review queue
- **Spec:** §11 page table (Review queue), §23 `/api/review*`, `/api/dat/search`.
- **Deliverables:** pending list; detail with header + hashes; suggestions, DAT search, attach (Hack/Translated), manual form, AmbiguousGroup attach (`None`), Dismiss.
- **Tests:** manual form validation.

#### S46 — UI: Integrity page
- **Spec:** §11 page table (Integrity), §23 integrity endpoints.
- **Deliverables:** status, Run quick / Run deep / Retry missing media, issues table with filters, history (jobs of type Integrity/MediaRetry).

#### S47 — UI: Admin pages
- **Spec:** §11 page table (Admin), §23 users/dats/settings/logs/about.
- **Deliverables:** Users (create, enable/disable, role, reset password), DATs (versions + Refresh with progress), Settings (read-only + Download logs), About (version, licences, attribution, GitHub link with the FA brand icon).

---

### M8 — Tooling, e2e, docs, release, deploy

#### S48 — Dev scripts complete
- **Spec:** §16 (`setup-dev.ps1`, `start-dev.ps1`), §15.4.
- **Deliverables:** `setup-dev.ps1` (checks, restore, `npm ci`, Playwright browsers, dev DB, `./.dev/*`, `appsettings.Development.json` with **absolute** paths and the dev connection string), `start-dev.ps1` (`dotnet watch` on :5080 + Vite; `-ApiOnly`/`-WebOnly`; Ctrl+C stops both).
- **Done when:** on a fresh clone: `setup-dev.ps1` → `seed-ingest.ps1` → `start-dev.ps1` → log in → ingest → browse works (the §15.4 workflow).

#### S49 — E2E: Playwright + axe, compose, CI job
- **Spec:** §15.1 "E2E" row, §15.3, §16 (`test-e2e.ps1`), §18 (`e2e` job).
- **Deliverables:** `tests/e2e` Playwright project (TypeScript) + `@axe-core/playwright`; `docker-compose.e2e.yml`; `scripts/test-e2e.ps1` (`-SkipBuild`, `-Headed`, always tears down); specs: login + forced password change, ingest `Basic`, browse/search/filter, game detail + download, review resolve, Admin edit/move/delete; axe on every page visited (fail on serious/critical); CI `e2e` job uploading the report on failure. Then add `e2e` to branch protection (P4).
- **Done when:** `pwsh scripts/test-e2e.ps1` passes locally and in CI.

#### S50 — Documentation pass
- **Spec:** §19 (whole), §17.3, §10.4 (attribution + disclaimer).
- **Deliverables:** `README.md` (absolute GitHub URLs, quick start with the §17.3 compose, disclaimer, CC BY-SA attribution, screenshots taken from the e2e/dev stack), `docs/deployment.md`, `docs/configuration.md`, `docs/features.md`, `docs/zip-format.md`, `docs/adding-a-system.md`, `docs/development.md`; check `CONTRIBUTING.md`.
- **Done when:** every row of §19 exists and matches the code (spot-check each config key and endpoint against the source).

#### S51 — Release `v0.1.0`
- **Spec:** §18 (versioning, tags, Docker Hub description).
- **Steps:** on an up-to-date `main` with green CI: `git tag -a v0.1.0 -m "v0.1.0 — MVP"` → `git push origin v0.1.0`.
- **Done when:** Docker Hub has `0.1.0`, `0.1`, `latest` for amd64 + arm64; the Docker Hub description is the README; About in the image shows `0.1.0`.

#### S52 — Deploy to the owner's host + smoke test
- **Spec:** §17.2–§17.4, §4 (forwarded headers), docs/deployment.md. Owner tasks **P5, P6**.
- **Steps:**
  1. On the host: create `./data/{roms,ingest,logs,cache}` (+ `db` if using the compose MariaDB), owned by the chosen UID/GID.
  2. Write `.env` with `DB_PASSWORD`, `ADMIN_PASSWORD` (≥ 12 chars) and the §17.3 `docker-compose.yml` (pinned to `niemandr/rnrommanager:0.1.0`; drop the `mariadb` service and point the connection string at the shared server if using P6). Set `TZ`.
  3. `docker compose up -d`; wait for `healthy`; check `docker logs` for the startup lines and no `Error`.
  4. Smoke checklist: log in as `admin` → forced password change → Admin → DATs → Refresh (real DATs download) → copy a few of **your own** `.gb`/`.gbc` files into `data/ingest/gb/` → Ingestion → Start → games appear with box art → open a game, download the ZIP and a ROM → Review queue shows any unmatched file → Integrity → Run quick (no `Error` issues) → create a `User` account and confirm it can browse but not see Admin pages → `docker compose restart` and confirm you're still logged in (Data Protection keys in DB).
  5. Optional: put it behind your reverse proxy with `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (only if the port isn't reachable otherwise, §4).
- **Done when:** the whole checklist passes. Tick S52 in a final PR that also bumps the spec status line to "v1.0 — MVP live".

## 5. Kickoff / resume prompt

Paste this into a new Claude Code session at the repo root to start or continue the work:

```text
We are implementing RnRomManager step by step.

1. Read specs/initial-application-impl.md in full (the plan), then read the
   tracker in its §3 and find the first unticked step. If a step is ticked but
   its PR isn't merged, or the working tree has uncommitted work on a step/*
   branch, tell me what you found and ask before doing anything.
2. Read every section of specs/initial-application.md (the spec) that the
   step's "Spec" line lists, plus the plan's §1 (loop, Definition of Done,
   unclear-spec rule, conventions).
3. Check the step's owner prerequisites (plan §2). If one isn't done, stop and
   tell me which.
4. Follow the per-step loop in plan §1.1 for that ONE step only: switch to
   main and pull, create branch step/NN-slug, implement the deliverables,
   write the tests, meet every "Done when" item and the Definition of Done,
   tick the step in the tracker, commit, push and open a PR titled
   "SNN: <title>".
5. If anything in the spec is unclear, missing or contradicts the code, don't
   guess: follow plan §1.3 and ask me.
6. When the PR is open and CI is green, stop and give me a short report: what
   was built, test results, anything deferred or risky, and the next step's
   ID and title. Don't start the next step until I say so.
```
