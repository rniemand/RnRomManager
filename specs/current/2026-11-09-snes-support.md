---
title: Super Nintendo (SNES) support
status: in-progress
created: 2026-11-09
---

# Super Nintendo (SNES) support

## Summary
Super Nintendo Entertainment System (`snes`) joins as the sixth system through the `ISystemHandler` plug-in, in a new `RnRomManager.Systems.Snes` project. Ingestion accepts `.sfc` and `.smc` dumps; a 512-byte copier header is stripped before hashing and storing (as N64 fixes byte order), so headered dumps match the No-Intro DAT and are stored as `.sfc`. The internal cartridge header (LoROM / HiROM / ExHiROM) is read for classification, review suggestions and the stored `header` fields.

## Out of scope
- Satellaview (BS-X), Sufami Turbo and Nintendo Power: separate libretro DATs and families.
- Old copier formats `.swc` / `.fig` and interleaved (`.mgd`-style) dumps; split multi-file dumps.
- The DAT's 12 aftermarket `.bin` flash variants (dropped by the existing extension filter, N3 of the NES spec).
- A dedicated SNES icon (the controller fallback is used) and an automatic DAT refresh after upgrading (documented manual step, as for NES).

## Behaviour
- **S1 Registration.** `SnesHandler`, in a new `src/RnRomManager.Systems.Snes` project, is registered after NES. Systems list order: `gb`, `gbc`, `gba`, `n64`, `nes`, `snes`.
- **S2 Contract values.** `Id` = `snes`, `DisplayName` = `Super Nintendo Entertainment System`, `Family` = `snes`, `RomExtensions` = `[".sfc", ".smc"]` (`.sfc` first: renamed and resolved files are stored with it), `MinRomSizeBytes` = `8192` (the smallest `.sfc` in the DAT), `MaxRomSizeBytes` = `8 MiB + 512` (8388608, the largest `.sfc`, plus a copier header), `DatName` = `Nintendo - Super Nintendo Entertainment System` (the five libretro files and the libretro-thumbnails repository exist; DAT version 2026.10.07 has 3718 `.sfc` and 12 `.bin` entries, no `.sfc` entry has size `% 1024 == 512`; checked 2026-11-09). The size limits apply to the file as dropped (before the strip), as for every system.
- **S3 Copier-header strip** (`NormalizeForHashing`). When `length % 1024 == 512`, the first 512 bytes are removed; otherwise the bytes are returned unchanged. The rule depends on size only, not extension, so a headered file named `.sfc` is also fixed. The stripped bytes are what is hashed, stored, header-read and integrity-checked. The strip is not recorded in `header` (`ReadHeader` only sees normalized bytes); `originalFileName` keeps the `.smc` name.
- **S4 Header reader** (`SnesHeaderReader.Read`). Never throws. Candidates, in order: LoROM at `0x7FC0`, HiROM at `0xFFC0`, ExHiROM at `0x40FFC0`; for ROMs over 4 MiB the ExHiROM candidate is tried first (ExHiROM dumps can hold bank data that looks like a header at `0xFFC0`). A candidate is considered only if the ROM is at least `offset + 0x40` bytes long. Returns `null` when no candidate fits (ROMs under 32 KiB: then the file is treated as headerless and plausible, as today). Candidate `c` at `o`:
  - `checksumValid` = `u16le(o+0x1E) + u16le(o+0x1C) == 0xFFFF` (checksum + complement).
  - `mapModeFits` = `rom[o+0x15]` has `0x20` set and its low nibble fits the location: LoROM `0x0` or `0x2` or `0x3`, HiROM `0x1` or `0xA` (SA-1 / SPC7110 variants included as LoROM `0x3`, HiROM `0xA`), ExHiROM `0x5`.
  - `titleOk` = all 21 title bytes (`o..o+0x14`) are printable ASCII `0x20–0x7E`, and at least one isn't a space.
  - Chosen candidate: the first with `checksumValid`; else the first with `titleOk && mapModeFits`; else none. **`IsPlausible`** = a candidate was chosen (lenient rule). `ClassifiedSystemId` = `snes` always. `Title` = the chosen candidate's 21 title bytes decoded with the existing GB/N64 rule (ASCII up to the first NUL, other bytes outside `0x20–0x7E` become `?`, trimmed, null when empty; as `Nintendo64HeaderReader.ReadTitle`), or null when no candidate was chosen. Half-width katakana (`0xA1–0xDF`) therefore become `?`.
  - `Fields` (all present; null when no candidate was chosen, except `headerValid` and `romSizeBytes`):

    | Field | Value |
    |---|---|
    | `headerValid` | a candidate was chosen |
    | `title` | as `Title` |
    | `mapping` | `LoROM`, `HiROM` or `ExHiROM` (the chosen location) |
    | `fastRom` | `rom[o+0x15] & 0x10 != 0` |
    | `romType` | `rom[o+0x16]` as a number (chipset byte; not decoded) |
    | `romSizeKiB` | `1 << rom[o+0x17]`, null when the byte is 0 or > 13 |
    | `sramSizeKiB` | 0 when `rom[o+0x18]` is 0, `1 << byte` when 1–10, else null |
    | `region` | `rom[o+0x19]`: 0 `Japan`, 1 `USA`, 2 `Europe`, 3 `Sweden`, 4 `Finland`, 5 `Denmark`, 6 `France`, 7 `Netherlands`, 8 `Spain`, 9 `Germany`, 10 `Italy`, 11 `China`, 12 `Indonesia`, 13 `Korea`, 15 `Canada`, 16 `Brazil`, 17 `Australia`; other values → null |
    | `version` | `rom[o+0x1B]` as a number |
    | `checksum` | `u16le(o+0x1E)` as 4 upper-case hex digits |
    | `checksumValid` | as above, for the chosen candidate |
    | `romSizeBytes` | the normalized length |
- **S5 Ingestion.** The existing pipeline. A matched dump is stored like any other system: a game ZIP `roms/snes/<bucket>/<sanitized title>.zip` whose ROM entry is the DAT ROM name (`….sfc`), with the stripped bytes. Unmatched + plausible (or no candidate) → Review (suggestions from `Title`, else the file name); resolving it stores the normalized bytes as `….sfc` (existing `RomExtensions[0]` rule). Unmatched + candidates present but none chosen → `InvalidHeader`. Under 8192 bytes → `TooSmall`; over 8388608 + 512 → `TooLarge`. A `.sfc`/`.smc` file in another family's folder, or another family's file in `ingest/snes/` → `UnsupportedType`.
- **S6 Badge colour.** A fixed SNES purple, the same in both themes (Bootstrap `dark` would match the dark-theme page background). `app.css` defines `--bs-snes: #5b4b9a` on `:root` (outside any theme selector) and `.text-bg-snes { color: #fff !important; background-color: var(--bs-snes) !important; }`, mirroring Bootstrap's `text-bg-*`. `systemColours` gets `snes: { name: 'snes', foreground: '#fff' }` (the `name` union gains `'snes'`), so `SystemBadge` (`text-bg-{name}`) and the Library Size treemap (`var(--bs-{name})`) work unchanged. White on `#5b4b9a` is ≥ 4.5:1 (AA), and the colour differs from both theme backgrounds.
- **S7 Default box art.** `snes.svg` (drawn for this project, MIT) and its 512×512 `snes.png` render, embedded and loaded like NES's.
- **S8 Upgrade.** Ingestion refuses with `DatsMissing` until an Admin runs **Admin → DATs → Refresh DATs** (documented).
- **S9 Unknown-system test id.** Tests that use `snes` as an unregistered system switch to `md` (Mega Drive, not planned), so they keep testing "unknown".

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | new `src/RnRomManager.Systems.Snes/` (`SnesHandler.cs`, `SnesHeaderReader.cs`, `SnesCopierHeader.cs`, `Resources/boxart/{snes.svg,snes.png,README.md}`, csproj) | S2–S4, S7 |
| Build | `RnRomManager.slnx`, `Dockerfile` (restore layer) | new src + test projects |
| Api | `AppHost.cs`, `RnRomManager.Api.csproj` | S1 registration + project reference |
| Web | `src/app.css`, `src/lib/systemColours.ts`, `systemColours.test.ts`, `SystemBadge` test (in `Pagination.test.ts`) | S6 |
| Test data | new `tests/RnRomManager.TestData/SyntheticSnesRomBuilder.cs`; `IngestSeeder.cs` (`SnesDat`, `ingest/snes/`, seed game) | S4 builder, seed |
| Tests (new) | `tests/RnRomManager.Systems.Snes.Tests/` (`SnesHeaderReaderTests`, `SnesCopierHeaderTests`, `SnesHandlerTests`, `SyntheticSnesRomBuilderTests`) | S2–S4, S7 |
| Tests (existing, broken by a 6th system) | the fifteen files that reference `IngestSeeder.NesDat`: `AuditLibraryEditTests`, `DatEndpointsTests`, `ImageEndpointsTests`, `ImageUrlEndpointsTests`, `IngestArchiveTests`, `IngestEndpointsTests`, `IngestPipelineTests`, `IngestReviewTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `NoteEndpointsTests`, `ReportEndpointsTests`, `ReviewEndpointsTests` | add `[IngestSeeder.SnesDat] = []`, else `DatsMissing` |
| Tests (existing, registration counts; step 3) | `DatEndpointsTests` (DAT names + `snes`, requests 25 → 30), systems 5 → 6 (`LibraryEndpointsTests`, `IngestEndpointsTests`), `SystemStartupTests` | counts, ids |
| Tests (existing, seed counts; step 4) | `IngestSeederTests`, `SeedIngestTests`, `tests/e2e/specs/mvp.spec.ts` | Basic-set counts, flows |
| Tests (existing, S9) | `GameQueryParsingTests`, `IngestEndpointsTests`, `LibraryEndpointsTests`, `ReportEndpointsTests`, `ReviewEndpointsTests`, `Core.Tests` `IngestJobTests` and `SystemRegistryTests`, `Data.Tests` `IntegrityJobTests` and `LibraryMirrorTests`, Web `Pagination.test.ts`, `systemColours.test.ts` | `snes` → `md` as the unknown id (`stores.svelte.test.ts`'s `system-snes` icon fallback stays true, unchanged) |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md` | SNES (systems, folders, `.sfc`/`.smc` + strip, header fields, upgrade), seed table, project table; `adding-a-system.md` replaces "a SNES handler would strip…" with the real example, and its other hypothetical-SNES lines (intro "Adding SNES means…", the `'system-snes'` icon example, the `SuperNintendoHandler` registration snippet) switch to `md` / `MegaDriveHandler` |

No migrations or config keys. `system_id` is a `VARCHAR`; existing data is untouched. API: `GET /api/systems` and `GET /api/dats` gain `snes`; no new routes.

## Risks
- **Ingestion blocked after upgrade** (S8) until DATs are refreshed. Documented in README and `deployment.md`.
- **A 6th system breaks existing tests** (`DatsMissing`, counts, `snes` as the unknown id). Fixed in the same step as the registration (step 3).
- **Strip false positive:** a real headerless ROM whose size is `≡ 512 (mod 1024)` would lose 512 bytes. None exists in the DAT (S2), so a matched dump can't be harmed; an unmatched one would still go to Review with its original file kept in `ingest/` until resolved.
- **Lenient header rule** lets some junk reach Review instead of `InvalidHeader`. Accepted: Review is recoverable, a wrongly rejected hack or prototype isn't visible.
- **Memory:** an 8 MiB ROM is read whole, as N64's 64 MiB already is.

## Steps
- [x] **1. SNES header reader and builder**: S3, S4 and `SyntheticSnesRomBuilder` (`sealed record`: `Seed = 0`, `SizeKiB = 512`, `Mapping` (LoROM/HiROM/ExHiROM enum in TestData), `Title = "SYNTHETIC SNES"` (padded to 21 with spaces; written as Latin-1 so a test can set any byte `0x00–0xFF`, e.g. `"\u0001"` or `"±"`; over 21 chars throws `ArgumentException`), `FastRom = false`, `RomType = 0`, `SramSizeByte = 0`, `Region = 1`, `Version = 0`, `ValidChecksum = true`, `CopierHeader = false` (prepends 512 zero bytes); body is `Random(Seed)` bytes, then the header written at the mapping's offset, map-mode byte `0x20 | (FastRom ? 0x10 : 0) | mode` with mode LoROM `0x0`, HiROM `0x1`, ExHiROM `0x5`; byte `0x17` = `log2(SizeKiB)` (`SizeKiB` must be a power of two); `SizeKiB * 1024` smaller than `offset + 0x40` throws `ArgumentException` (ExHiROM needs `SizeKiB` ≥ 8192 here; tests set it); checksum: write complement `FFFF` and checksum `0000`, sum every byte of the (headerless) image mod 0x10000, then write that as the checksum and its bitwise NOT as the complement; `ValidChecksum = false` writes `0000`/`0000` instead). New projects `src/RnRomManager.Systems.Snes` and `tests/RnRomManager.Systems.Snes.Tests` in `RnRomManager.slnx`, and the Snes csproj in the `Dockerfile` restore layer, all in this step. Files: `SnesHeaderReader.cs`, `SnesCopierHeader.cs` (`Strip(ReadOnlyMemory<byte>)`), both csproj files, `RnRomManager.slnx`, `Dockerfile`, `SyntheticSnesRomBuilder.cs`, `SnesHeaderReaderTests.cs`, `SnesCopierHeaderTests.cs`, `SyntheticSnesRomBuilderTests.cs`. Tests: null under 32 KiB (and for a 32 KiB+ ROM no candidate past its length is read); each mapping found at its offset; every S4 field (incl. `romSizeKiB`/`sramSizeKiB` edge bytes, each listed region and an unlisted one → null, `fastRom`); candidate priority (bad checksum at LoROM + valid at HiROM → HiROM; no checksum anywhere + printable title and fitting map mode → plausible; neither → `headerValid: false`, `IsPlausible` false, other fields null); title with a non-printable byte or all spaces → not `titleOk`; a checksum-chosen candidate whose title has `0xA1–0xDF` bytes and NUL padding → `Title` per the GB rule; an ExHiROM builder ROM with a fake valid LoROM/HiROM-looking area reads as `ExHiROM`; random garbage never throws; strip: `n*1024+512` loses exactly the first 512 bytes, `n*1024` and `n*1024+256` unchanged; builder deterministic, reads back, checksum matches an independent sum. Done when: `dotnet test tests/RnRomManager.Systems.Snes.Tests` passes and `dotnet build` passes with warnings as errors.
- [x] **2. SNES handler and box art**: S2, S7. `SnesHandler` (`ReadHeader` → reader, `NormalizeForHashing` → `SnesCopierHeader.Strip`), box art SVG + 512×512 PNG + README as embedded resources. Files: `SnesHandler.cs`, `Resources/boxart/*`, `src/RnRomManager.Systems.Snes/RnRomManager.Systems.Snes.csproj` (embedded resources), `tests/RnRomManager.Systems.Snes.Tests/RnRomManager.Systems.Snes.Tests.csproj` (adds a reference to `RnRomManager.Systems.Nes` for the box-art comparison), `SnesHandlerTests.cs`. Tests: `Snes_Contract` (every S2 value); box art is a 512×512 PNG differing from the NES one; `ReadHeader` classifies a builder ROM as `snes`; `NormalizeForHashing` strips a builder ROM with `CopierHeader = true` back to the `false` bytes. Done when: `dotnet test tests/RnRomManager.Systems.Snes.Tests` passes and `dotnet build` passes with warnings as errors.
- [x] **3. Registration and existing tests**: S1, S6, S9. Api project reference and `AppHost` line; `IngestSeeder.SnesDat` const, an empty SNES fixture DAT in the seeder's `FixtureDatWriter.Write` call, `ingest/snes/` created; `[IngestSeeder.SnesDat] = []` in the fifteen files of § Changes; registration count updates (DATs 25 → 30, systems 5 → 6, startup ids/folders; `IngestSeederTests`' seeded DAT file count 25 → 30, since the empty SNES fixture DAT lands here); S9 renames; S6 (`app.css`, `systemColours.ts` + tests). Files: `AppHost.cs`, `RnRomManager.Api.csproj`, `IngestSeeder.cs`, the test files in § Changes' "broken by a 6th system", "registration counts" and "S9" rows (not `mvp.spec.ts` or the seed-count files, which change in step 4), `app.css`, `systemColours.ts`, `systemColours.test.ts`, `Pagination.test.ts`. Tests: `systemColour('snes')` is `{ name: 'snes', foreground: '#fff' }` and `systemColour('md')` is the fallback; `SystemBadge` for `snes` has class `text-bg-snes`; `SystemStartupTests` sees `snes` and its folders; DAT refresh fetches 30 files. Done when: `dotnet test` passes for the Snes, Core, Data and Api test projects (Docker running), SPA lint/check/tests pass, and `docker build .` succeeds.
- [x] **4. SNES seed, integration and e2e**: S3, S5. Add `new FixtureGame("Mode Seven Rally (USA)", "Mode Seven Rally (USA).sfc", new SyntheticSnesRomBuilder { Seed = 1101, Mapping = LoROM, Title = "MODE SEVEN RALLY", SramSizeByte = 3 }.Build()) { ReleaseYear = 1992, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Racing"] }` to the Basic set and the SNES fixture DAT, written loose as `ingest/snes/01-mode-seven-rally.smc` from the same builder `with { CopierHeader = true }`. Update every Basic/Full count it changes (`IngestSeederTests`, `SeedIngestTests`, `mvp.spec.ts`: `Ingested 10` → `Ingested 11`, the audit `toHaveCount(10)` → `11`, and both `8 games` → `9 games`; any other count the seed changes). `IngestPipelineTests` (own DAT where needed): a `.sfc` in `ingest/nes/` → `UnsupportedType`; a `.nes` in `ingest/snes/` → `UnsupportedType`; a headerless `.sfc` then the same game as a headered `.smc` → one game, the second `Duplicate` (same SHA1); an 8191-byte `.sfc` → `TooSmall`; an 8 MiB + 513-byte `.smc` → `TooLarge`; an unmatched 64 KiB builder ROM with `ValidChecksum = false` and a non-printable title → `InvalidHeader`; the same with a printable title → Review, `NoDatMatch`, suggestion text from the title; an unmatched 16 KiB ROM (no candidate) → Review; an unmatched headered `.smc` resolved via review is stored as `….sfc` with the stripped bytes' SHA1; a `.zip` in `ingest/snes/` holding a headered `.smc` of a DAT game → ingested, ROM entry `….sfc` with the stripped bytes (archive path uses the per-extension size limit). Files: `IngestSeeder.cs`, `IngestSeederTests.cs`, `SeedIngestTests.cs`, `IngestPipelineTests.cs`, `mvp.spec.ts`. Tests: `SeedIngestTests` asserts the game ZIP under `roms/snes/M/` holding the ROM entry `Mode Seven Rally (USA).sfc` whose bytes equal the headerless builder output, `originalFileName` `01-mode-seven-rally.smc`, `system: snes`, `header.mapping` `LoROM`, `header.title` `MODE SEVEN RALLY`, `header.sramSizeKiB` 8 and its year/genre metadata; e2e selects System → `Super Nintendo Entertainment System`, expects `system=snes` in the URL and `1 game`. Done when: those projects pass and `pwsh scripts/test-e2e.ps1` passes.
- [x] **5. Docs**: every doc in § Changes' Docs row. Tests: none. Done when: README lists SNES with the Refresh DATs upgrade note; `features.md` covers `ingest/snes/` (`.sfc`/`.smc`, copier header stripped, stored as `.sfc`); `zip-format.md` lists the SNES header fields; `deployment.md` has `ingest/snes/` and the upgrade step; `development.md` has the project table and seed table (Mode Seven Rally); `adding-a-system.md` cites SNES as the copier-strip example, lists the Snes project, and no longer uses SNES as a hypothetical (intro, registration snippet, icon example now `md`); and `pwsh scripts/test-all.ps1` passes.

## Decisions
- 2026-11-09 SNES file types → `.sfc` + `.smc`, copier header stripped by size (`% 1024 == 512`), stored as `.sfc` (user)
- 2026-11-09 Unmatched header rule → lenient: valid checksum pair, or printable title with a fitting map mode (user)
- 2026-11-09 Badge colour → custom SNES purple `#5b4b9a`, white text, via `--bs-snes` / `.text-bg-snes`; `dark` rejected because it matches the dark-theme background (user, after review)
- 2026-11-09 Title decoding → the GB/N64 rule (reviewer)
- 2026-11-09 ROMs over 4 MiB try ExHiROM first (reviewer)
- 2026-11-09 Seed → one synthetic SNES game dropped as a headered `.smc` (user)
- 2026-11-09 Project `RnRomManager.Systems.Snes`, short name like NES (code: NES precedent)
- 2026-11-09 Display name `Super Nintendo Entertainment System`, the DAT's name (code)
- 2026-11-09 Size limits from the libretro DAT 2026.10.07: smallest `.sfc` 8192, largest 8388608 (code)
- 2026-11-09 Unknown-system test id `snes` → `md` (code)
- 2026-11-09 Step 4 also tests a headered `.smc` inside a `.zip` (archive size-limit path) (user, after review)
- 2026-11-09 `IngestSeederTests`' DAT file count (25 → 30) moves to step 3, where the empty SNES fixture DAT is added; its Basic/Full file counts stay in step 4 (code, implementation)
- 2026-11-09 Builder `Title` is written as Latin-1, so step 1's non-printable and `0xA1–0xDF` title tests can set raw bytes (code, spec review)

## Kickoff prompt
```text
/spec implement 2026-11-09-snes-support.md
```
Starts the spec, or resumes it at the first unticked step.
