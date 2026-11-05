---
title: Game Boy Advance support
status: ready # draft | ready | in-progress | completed
created: 2026-11-05
branch: dev/gba-support
---

# Game Boy Advance support

## Summary
Add Game Boy Advance (`gba`) as a third system. Admins drop `.gba` files (or `.zip`/`.7z` archives of them) into `ingest/gba/`. Ingestion identifies them against the No-Intro GBA DAT and stores them under `roms/gba/`. Everyone can browse and filter them in the library like GB/GBC games. This uses the existing `ISystemHandler` plug-in, so Core, the database, the API and the SPA need no code changes.

## Out of scope
- An auto DAT refresh after upgrading (decided: document the manual **Refresh DATs** step instead).
- Extensions other than `.gba` (`.agb`, `.bin`).
- A dedicated GBA icon. `icons.ts` maps no system today, and `system-gba` gets the controller fallback like `gb`/`gbc`.
- Save files, BIOS files, e-Reader cards, multiboot (`.mb`) images and GBA Video carts treated specially. Video carts are ordinary DAT entries.
- Any change to `ISystemHandler`, `RomHeader`, the DB schema or the API contract.

## Behaviour
- **B1 Registration.** A new handler `GameBoyAdvanceHandler` is registered after the GB and GBC handlers. `/api/systems`, the library's System filter and startup folder creation then list three systems in this order: `gb`, `gbc`, `gba`.
- **B2 Contract values.** `Id` = `gba`, `DisplayName` = `Game Boy Advance`, `Family` = `gba` (its own family: GB/GBC files are never checked against the GBA DAT and vice versa), `RomExtensions` = `[".gba"]`, `MinRomSizeBytes` = `0xC0` (192, the header size), `MaxRomSizeBytes` = 32 MiB (`32L * 1024 * 1024`, the largest GBA cart), `DatName` = `Nintendo - Game Boy Advance` (the five upstream DAT files and the libretro-thumbnails folder all exist; checked 2026-11-05), `NormalizeForHashing` = identity (the interface default).
- **B3 Folders.** At startup, `roms/gba/` and `ingest/gba/` are created with their letter folders and `_*` folders by the existing `StorageFolders` code. Nothing new is needed.
- **B4 Header reader** (`GameBoyAdvanceHeaderReader.Read`, GBATEK "GBA Cartridge Header"). It never throws. It returns `null` when the input is shorter than `0xC0`. Otherwise it returns a `RomHeader` with:
  - `ClassifiedSystemId` = `gba` always.
  - `Title` = the title at `0xA0–0xAB` (12 bytes), read with the same rules as GB `ReadTitle`: ASCII up to the first NUL, non-printable bytes → `?`, trimmed, and null when empty.
  - `IsPlausible` = `logoValid || headerChecksumValid` (the GB rule).
  - `Fields`, all JSON primitives:
    | Field | Source |
    |---|---|
    | `title` | as `Title` |
    | `gameCode` | `0xAC–0xAF` as ASCII when all four bytes are `A–Z`/`0–9`, else null |
    | `destination` | 4th game-code char: `J` Japan, `E` USA, `P` Europe, `D` Germany, `F` France, `I` Italy, `S` Spain, `H` Netherlands, `K` Korea, `X`/`Y` Europe; another char → `Unknown (X)`; null when `gameCode` is null |
    | `makerCode` | `0xB0–0xB1` as ASCII when both bytes are `A–Z`/`0–9`, else null |
    | `maker` | `GameBoyHeaderTables.NewLicensees[makerCode]` (GBA maker codes are the same two-character list as GB new-licensee codes); `Unknown (0xHHHH)` when the code is not in the table; null when `makerCode` is null |
    | `unitCode` | `0xB3` as a hex string (`0x00`) |
    | `deviceType` | `0xB4` as a hex string |
    | `version` | `0xBC` as an int |
    | `fixedValueValid` | `0xB2 == 0x96` |
    | `logoValid` | `0x04–0x9F` equals the 156-byte Nintendo logo |
    | `headerChecksumValid` | `0xBD` equals the complement check: `chk = 0; for i in 0xA0..0xBC: chk -= rom[i]; chk -= 0x19` (byte arithmetic) |
    | `romSizeBytes` | the file length (GBA headers don't store a size) |
- **B5 Logo constant.** The 156-byte logo is copied from GBATEK into `GameBoyAdvanceHeaderReader.NintendoLogo`. It starts `24 FF AE 51 69 9A A2 21`. A unit test pins its length (156) and its CRC32 (computed with `Core.Storage.Crc32` from the GBATEK bytes when the constant is added). Both the builder and the reader copy the constant, so the test guards against an accidental edit, not against a wrong copy. The implementer cross-checks it against GBATEK once.
- **B6 Ingestion.** It uses the existing pipeline unchanged. A matched `.gba` → `roms/gba/<Letter>/…zip`. An unmatched file with a plausible header → Review, with the header title as a suggestion. An unmatched file with an implausible header → `InvalidHeader`. A file under 192 bytes → `TooSmall`, over 32 MiB → `TooLarge`. A `.gb`/`.gbc` file in `ingest/gba/`, or a `.gba` file in `ingest/gb/`, fails with the existing `UnsupportedType` reason ("isn't a ROM extension of the … family") and moves to `_failed/`.
- **B7 Default box art.** `gba.svg` (drawn for this project, MIT) and its 512×512 `gba.png` render are added to `Resources/boxart/` and embedded. It is stored when no thumbnail is found, as for GB/GBC.
- **B8 Upgrade.** After upgrading, ingestion refuses to start with `DatsMissing` until an Admin runs **Admin → DATs → Refresh DATs**. This is existing behaviour, now documented in the upgrade notes.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core (system) | `src/RnRomManager.Systems.GameBoy/GameBoyAdvanceHeaderReader.cs` (new) | B4/B5 reader |
| Core (system) | `src/RnRomManager.Systems.GameBoy/GameBoyAdvanceHandler.cs` (new) | B2. Implements `ISystemHandler` directly, because `GameBoyHandlerBase`'s limits and reader are GB-only |
| Core (system) | `src/RnRomManager.Systems.GameBoy/GameBoyHandlerBase.cs`, `GameBoyHeaderTables.cs`, new `EmbeddedResources.cs` | move `LoadResource` into an `internal static EmbeddedResources.Load(name)` helper (throws `InvalidOperationException` when missing, as today). `GameBoyHandlerBase`, `GameBoyAdvanceHandler` and `GameBoyHeaderTables.Load` all use it, so there is one resource code path |
| Core (system) | `src/RnRomManager.Systems.GameBoy/Resources/boxart/gba.svg`, `gba.png`, `README.md` | B7. The csproj already embeds `boxart/*.png` |
| Api | `src/RnRomManager.Api/AppHost.cs` | `builder.Services.AddSystemHandler<GameBoyAdvanceHandler>();` after GBC |
| Test data | `tests/RnRomManager.TestData/SyntheticGbaRomBuilder.cs` (new), `IngestSeeder.cs` | a deterministic GBA ROM builder (step 1). `IngestSeeder.GbaDat = "Nintendo - Game Boy Advance"`, and the seeder always writes that fixture DAT (empty in step 2). Step 3: the Basic set gains one matched GBA game, `Star Courier (USA)`, as `ingest/gba/01-star-courier.gba`. Full adds nothing more |
| Tests (existing, broken by a third system) | the Api integration fixtures that call `FixtureDatWriter.Write` with only GB/GBC: `IngestEndpointsTests`, `IngestPipelineTests`, `IngestArchiveTests`, `IngestReviewTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `ReviewEndpointsTests`, `MediaRetryEndpointsTests` | add `[IngestSeeder.GbaDat] = []` to each dictionary. Without it, `IngestJob.Preflight` refuses with `DatsMissing` |
| Tests (existing) | `DatEndpointsTests` | the stub remote also serves the five GBA DATs; `_server.Requests` count 10 → 15 |
| Tests (existing) | `LibraryEndpointsTests.cs` (~114), `IngestEndpointsTests.cs` (~122), `SystemStartupTests.cs` | `/api/systems` count 2 → 3; pending systems `{gb, gbc}` → `{gb, gbc, gba}`; startup ids `gb, gbc, gba` |
| Tests (existing) | `tests/RnRomManager.Core.Tests/Ingest/IngestSeederTests.cs` | DAT files 10 → 15 (step 2); seeded files Basic 10 → 11 and Full 210 → 211 (step 3) |
| Tests (existing) | `SeedIngestTests.cs`, `tests/e2e/specs/mvp.spec.ts` | step 3: Basic 7→8 releases / 5→6 games, Full 207→208 / 205→206; e2e job summary `Ingested 7` → `Ingested 8` (~line 73), both `5 games` checks → `6 games` (~lines 85, 128) |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md` | supported systems, `ingest/gba/`, GBA `header` fields, upgrade note (B8), seed table, project table. `adding-a-system.md` notes that a system may share a project when that's simpler (GBA lives in the GameBoy project) |

No migrations, config keys or API contract changes. `system_id` is a `VARCHAR`. Existing data is untouched.

## Risks
- **Ingestion blocked after upgrade** (B8) until DATs are refreshed. Mitigated by the upgrade note in `docs/deployment.md` and `README.md`.
- **Memory.** A 32 MiB ROM is read fully into memory for `ReadHeader` (the existing design, bounded by `MaxRomSizeBytes`). Within the 256 MiB archive total. Acceptable.
- **Wrong logo bytes** would make every real cart depend on the checksum for plausibility, and matched files are unaffected either way. B5's pinned CRC32 and the one-time GBATEK check cover this.
- **A third system breaks existing tests**: every fixture that writes only GB/GBC DATs gets `DatsMissing`, and system counts change. Step 2 fixes all of them together with the registration. Seed count changes land in step 3.
- No real ROMs are committed. Tests use synthetic ROMs only.

## Steps
- [ ] **1. GBA header reader**: `GameBoyAdvanceHeaderReader` (B4, B5) and `SyntheticGbaRomBuilder`, a `sealed record` like `SyntheticRomBuilder` with these properties and defaults: `Seed = 0`, `SizeBytes = 256 * 1024`, `Title = null` (zero bytes), `GameCode = null` (zero bytes), `MakerCode = "01"`, `UnitCode = 0`, `DeviceType = 0`, `Version = 0`, `CorruptLogo = false`, `CorruptFixedValue = false`, `CorruptHeaderChecksum = false`. The body is `Random(Seed)` bytes. The logo, the `0x96` fixed value and the header fields are written over the body, and the complement check is computed last (corrupted by `+1` when asked). Files: `src/RnRomManager.Systems.GameBoy/GameBoyAdvanceHeaderReader.cs`, `tests/RnRomManager.TestData/SyntheticGbaRomBuilder.cs`, `tests/RnRomManager.Systems.GameBoy.Tests/GameBoyAdvanceHeaderReaderTests.cs`, `SyntheticGbaRomBuilderTests.cs`. Tests: null under 0xC0 and not null at exactly 0xC0; title rules (NUL-terminated, padded, non-printable, empty → null); gameCode/makerCode valid and invalid → null; destination for `J`, `E`, `P`, unknown char, null gameCode; maker known, unknown and null; logo valid/corrupt; checksum valid/corrupt; `IsPlausible` truth table; `fixedValueValid`; `romSizeBytes` = length; random garbage never throws; logo constant length 156 and pinned CRC32; the builder is deterministic (same properties → same bytes) and its output reads back with every field as set. Done when: the reader tests and the builder tests pass, and `dotnet build` passes with warnings as errors.
- [ ] **2. GBA handler and registration**: `GameBoyAdvanceHandler` (B2), the `EmbeddedResources` helper refactor (including `GameBoyHeaderTables`), `gba.svg` + `gba.png` (B7) and the README note, and registration in `AppHost` (B1). Every existing fixture also needs a GBA DAT now, so ingestion isn't refused with `DatsMissing`: `IngestSeeder.GbaDat` plus an empty GBA DAT in the seeder, `[IngestSeeder.GbaDat] = []` in every Api fixture listed in § Changes, and the GBA files in the `DatEndpointsTests` stub. Files: `GameBoyAdvanceHandler.cs`, `EmbeddedResources.cs`, `GameBoyHandlerBase.cs`, `GameBoyHeaderTables.cs`, `Resources/boxart/*`, `src/RnRomManager.Api/AppHost.cs`, `tests/RnRomManager.TestData/IngestSeeder.cs`, `tests/RnRomManager.Systems.GameBoy.Tests/GameBoyHandlerTests.cs`, `tests/RnRomManager.Api.Tests/SystemStartupTests.cs`, the nine Api integration fixtures, `DatEndpointsTests.cs`, `tests/RnRomManager.Core.Tests/Ingest/IngestSeederTests.cs`. Tests: a `GameBoyAdvance_Contract` test (every B2 value); box art is a 512×512 PNG and differs from GB/GBC; `ReadHeader` classifies a synthetic ROM as `gba`; `NormalizeForHashing` is the identity; `SystemStartupTests` expects `gb, gbc, gba` and the `gba` folders; `/api/systems` returns 3; pending systems are `{gb, gbc, gba}`; the DAT refresh makes 15 requests; the seeder writes 15 DAT files. The existing GB/GBC handler and header-table tests still pass after the refactor. Done when: `dotnet test` passes for `tests/RnRomManager.Systems.GameBoy.Tests`, `tests/RnRomManager.Core.Tests` and `tests/RnRomManager.Api.Tests` (Docker running), and `pwsh scripts/test-e2e.ps1` still passes with the unchanged counts.
- [ ] **3. Seed data, ingest integration and e2e**: add `Star Courier (USA)` to the Basic seed and the GBA fixture DAT (B6): `new FixtureGame("Star Courier (USA)", "Star Courier (USA).gba", new SyntheticGbaRomBuilder { Seed = 3301, Title = "STARCOURIER", GameCode = "ASCE", MakerCode = "01" }.Build()) { ReleaseYear = 2002, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Action"] }`, written loose as `ingest/gba/01-star-courier.gba` (the seeder also creates `ingest/gba/`). Update the counts listed in § Changes. Add one B6 test: a `.gba` file in `ingest/gb/` fails with `UnsupportedType`. Files: `tests/RnRomManager.TestData/IngestSeeder.cs`, `tests/RnRomManager.Core.Tests/Ingest/IngestSeederTests.cs`, `tests/RnRomManager.Api.Tests/Integration/SeedIngestTests.cs`, `tests/RnRomManager.Api.Tests/Integration/IngestPipelineTests.cs`, `tests/e2e/specs/mvp.spec.ts`. Tests: the seeder writes `ingest/gba/01-star-courier.gba` (files Basic 11, Full 211); `SeedIngestTests` expect Basic 8/6 and Full 208/206, and assert that Star Courier is stored under `roms/gba/S/` with `system: gba` and its `header.gameCode` = `ASCE`; the `UnsupportedType` test; the e2e expects `Ingested 8 · Duplicate 1 · Review 1 · Failed 1`, `6 games` in both places, and that System → `Game Boy Advance` puts `system=gba` in the URL and shows `1 game`. Done when: those test projects pass and `pwsh scripts/test-e2e.ps1` passes.
- [ ] **4. Docs**: README (supported systems, upgrade note), `docs/features.md` (GBA, `ingest/gba/`), `docs/zip-format.md` (GBA `header` fields from B4), `docs/deployment.md` (`ingest/gba/` and the B8 upgrade step), `docs/development.md` (project table, seed table and text), `docs/adding-a-system.md` (the shared-project note, and drop "or GBA" from the example). Tests: none (docs only). Done when: every behaviour in B1–B8 appears in its doc, and `pwsh scripts/test-all.ps1` passes.

## Decisions
- 2026-11-05 Where does the GBA handler live? → In the existing `RnRomManager.Systems.GameBoy` project, not a new project (user)
- 2026-11-05 Which extensions? → `.gba` only (user)
- 2026-11-05 Ingestion blocked by `DatsMissing` after upgrade? → Document the manual Refresh DATs step; no auto-refresh (user)
- 2026-11-05 Test coverage beyond unit tests? → Seed data + integration + one e2e check (user)
- 2026-11-05 GBA family → its own `gba` family, because no GB/GBC header can classify a GBA file (code)
- 2026-11-05 Maker names → reuse `GameBoyHeaderTables.NewLicensees`, the same Nintendo maker-code list (code)
- 2026-11-05 Existing fixtures that write only GB/GBC DATs → add an empty GBA DAT to each in step 2, together with the registration (reviewer)
- 2026-11-05 One embedded-resource helper for box art and header tables (reviewer)
- 2026-11-05 A `.gba` file in another family's folder → the existing `UnsupportedType`, covered by a test (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-05-gba-support.md
```
Starts the spec, or resumes it at the first unticked step.
