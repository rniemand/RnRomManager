---
title: TurboGrafx-16
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-10
---

# TurboGrafx-16

## Summary
TurboGrafx-16 / PC Engine HuCards (`tg16`) join as the 13th system, in a new `RnRomManager.Systems.Nec` project, for the Admin who ingests ROMs and everyone who browses the library. HuCards have no header, so the handler follows Atari 2600 (no header, unmatched files go to Review), plus a SNES-style 512-byte copier-header strip so headered `.pce` dumps match the No-Intro DAT.

## Out of scope
- SuperGrafx (its own libretro DAT, 5 games) and TurboGrafx-CD / PC Engine CD (multi-file, the contract holds one file per release): later specs.
- Bit-reversed (undecrypted) US HuCard dumps: no detection or conversion; such a file goes to Review like any unmatched dump.
- A dedicated `tg16` icon (the controller fallback is used) and an automatic DAT refresh after upgrading (documented manual step).

## Behaviour
- **T1 Contract values.** `Id` = `tg16`, `DisplayName` = `TurboGrafx-16`, `ShortName` = `TG16`, `Family` = `tg16`, `RomExtensions` = `[".pce"]`, `MinRomSizeBytes` = `73728` (smallest DAT entry), `MaxRomSizeBytes` = `2621440 + 512` (largest DAT entry plus a copier header; checked before the strip, as SNES does), `DatName` = `NEC - PC Engine - TurboGrafx 16` (no hyphen in "TurboGrafx 16": libretro's spelling). `ReadHeader` returns null always (HuCards have no header). Facts (libretro DAT 2026.10.07, checked 2026-11-10): all five DAT files (`no-intro`, `releaseyear`, `developer`, `publisher`, `genre`) and the `NEC - PC Engine - TurboGrafx 16` thumbnails folder exist; 491 entries, all `.pce`, 73,728–2,621,440 bytes; the DAT mixes Japanese PC Engine and US TurboGrafx-16 releases.
- **T2 Copier header.** `NormalizeForHashing` drops the first 512 bytes when `length % 8192 == 512` (by size only), otherwise returns the input unchanged. The stripped bytes are hashed and stored (existing `NormalizeForHashing` rule), so a headered dump matches the DAT and is stored without the header. Known cost: three aftermarket protos in the DAT (`Gnop`, `Masteroids` at 115,200 bytes, `Light Cycles` at 164,352) have that size unheadered; they get stripped wrongly, don't match, and go to Review (accepted, user decision).
- **T3 Registration.** `Nec/TurboGrafx16Handler` registered after `Sega32xHandler`. Registry order: `gb, gbc, gba, n64, nes, snes, a2600, coleco, gg, sms, md, s32x, tg16`. `/api/systems` (display-name order, `OrdinalIgnoreCase`): `tg16` (`TurboGrafx-16`) last. `/api/admin/dats` lists `NEC - PC Engine - TurboGrafx 16` in the endpoint's existing sort (after `Coleco - ColecoVision`, before `Nintendo - Game Boy`). A DAT refresh fetches 65 files (13 × 5). Ingestion refuses with `DatsMissing` until **Admin → DATs → Refresh DATs** (documented).
- **T4 Ingestion.** The existing pipeline. A DAT-matched dump (headered or not) is stored as `roms/tg16/<bucket>/<title>.zip` with the DAT's ROM name. An unmatched file always goes to Review (`NoDatMatch`, file-name suggestions, never `InvalidHeader`); resolving it stores `….pce`. Under 73,728 bytes → `TooSmall`, over 2,621,952 → `TooLarge`. A `.pce` in another system's folder, or another system's file in `ingest/tg16/` → `UnsupportedType`. Releases have no Header button; `info.json` `header` is null.
- **T5 Colour and box art.** Teal, same in both themes: `app.css` `--bs-tg16: #0b7a75` on `:root` and `.text-bg-tg16 { color: #fff !important; background-color: var(--bs-tg16) !important; }` next to the `s32x` rules (white on it ≈ 5.2:1, AA); `systemColours.tg16 = { name: 'tg16', foreground: '#fff' }` (union gains `'tg16'`). `tg16.svg` (drawn for this project, MIT) and its 512×512 `tg16.png` render, embedded and loaded like Atari's; a boxart `README.md` lists it.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | new `src/RnRomManager.Systems.Nec/` (`RnRomManager.Systems.Nec.csproj`, `TurboGrafx16Handler.cs`, `Resources/boxart/{tg16.svg,tg16.png,README.md}`) | T1, T2, T5 box art |
| Build | `RnRomManager.slnx`, `Dockerfile` (restore layer) | new src + test projects |
| Api | `src/RnRomManager.Api/AppHost.cs`, `RnRomManager.Api.csproj` | T3 |
| Web | `src/RnRomManager.Web/src/app.css`, `src/lib/systemColours.ts` (+ test), `src/lib/components/Pagination.test.ts` (holds the `SystemBadge` tests) | T5 colour |
| Test data | `tests/RnRomManager.TestData/IngestSeeder.cs` | `TurboGrafx16Dat` const, fixture DAT entry, `ingest/tg16/`, one seed game |
| Tests (new) | `tests/RnRomManager.Systems.Nec.Tests/` (csproj, `TurboGrafx16HandlerTests.cs`) | T1, T2, T5 |
| Tests (existing, broken by system 13) | every test file that lists `[IngestSeeder.Sega32xDat] = []` | add `[IngestSeeder.TurboGrafx16Dat] = []` |
| Tests (existing, counts/order) | `DatEndpointsTests` (60 → 65, DAT name order), `LibraryEndpointsTests` `/api/systems` list, `IngestEndpointsTests` system list, `SystemStartupTests`, `IngestSeederTests`, `SeedIngestTests`, `IngestPipelineTests`, `tests/e2e/specs/mvp.spec.ts` | |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md` | last step |

No migrations or config keys (`system_id` is a `VARCHAR`). API: `GET /api/systems` and `GET /api/admin/dats` gain `tg16` (additive). Existing data is untouched.

## Risks
- **A 13th system breaks existing tests** (`DatsMissing`, counts, orders): fixed in the registration step (2).
- **Size-only strip** misfires on three aftermarket protos (T2): they reach Review instead of matching. Review is recoverable.
- **No header check:** any 72 KiB–2.5 MiB `.pce` reaches Review rather than `InvalidHeader`, as with Atari 2600.

## Steps
- [x] **1. TurboGrafx-16 handler and box art**: T1, T2, T5 (box art). New projects `src/RnRomManager.Systems.Nec` and `tests/RnRomManager.Systems.Nec.Tests` in `RnRomManager.slnx`; the Nec csproj in the `Dockerfile` restore layer. Files: `TurboGrafx16Handler.cs`, `Resources/boxart/*`, both csproj files, `RnRomManager.slnx`, `Dockerfile`, `TurboGrafx16HandlerTests.cs` (test project references `RnRomManager.Systems.Atari` for the box-art comparison). Tests: contract (every T1 value); `ReadHeader` null for random bytes of several sizes; `NormalizeForHashing`: 262,144 + 512 bytes → the last 262,144; 262,144 → unchanged; 115,200 (`% 8192 == 512`) → stripped (documents T2's cost); box art valid 512×512 PNG differing from Atari's. Done when: `dotnet test tests/RnRomManager.Systems.Nec.Tests` passes and `dotnet build` passes with warnings as errors.
- [x] **2. Registration and existing tests**: T3, T5 (colour). Api project reference and `AppHost` line; `IngestSeeder.TurboGrafx16Dat` const, `[TurboGrafx16Dat] = []` in the seeder's `FixtureDatWriter.Write` call, `ingest/tg16/` created; the empty entry in every test file in § Changes "broken by system 13"; counts and orders (DATs 60 → 65, systems 12 → 13, `/api/systems` list per T3, startup ids/folders, `IngestSeederTests` DAT file count, `/api/admin/dats` order); `app.css`, `systemColours.ts` + test, `Pagination.test.ts`. Tests: `systemColour('tg16')` is `{ name: 'tg16', foreground: '#fff' }`; `SystemBadge` for `tg16` has `text-bg-tg16`; `SystemStartupTests` sees `tg16` and its folders; DAT refresh fetches 65 files. Done when: `dotnet test` passes for the Nec, Core, Data and Api test projects (Docker running); SPA `npm run lint`, `npm run check`, `npm test` pass; `docker build .` succeeds.
- [ ] **3. Seed, integration and e2e**: T2, T4. Basic set and fixture DAT: `new FixtureGame("Neon Circuit (USA)", "Neon Circuit (USA).pce", rom) { ReleaseYear = 1990, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Racing"] }`, `rom` = 262,144 bytes from `new Random(1801).NextBytes`, written loose as `ingest/tg16/01-neon-circuit.pce` **with 512 zero bytes prepended** (proves T2 end to end). Update every count it changes (`IngestSeederTests`, `SeedIngestTests`, `SeedIngestTests` `[TestCase(SeedSet.Basic, 17, 15)]` → `18, 16` and Full `[TestCase(SeedSet.Full, 217, 215)]` → `218, 216`; `mvp.spec.ts`: `Ingested 17` → `18`, ingested audit `toHaveCount(17)` → `18`, every `15 games` → `16 games`, the System option `toHaveText` array gains `'TurboGrafx-16 (1)'` as its last entry, any other). `IngestPipelineTests` (SNES's copier tests are the pattern): `.pce` in `ingest/md/` → `UnsupportedType`; `.md` in `ingest/tg16/` → `UnsupportedType`; 73,727 bytes → `TooSmall`; 2,621,953 → `TooLarge`; an unmatched 256 KiB `.pce` → Review `NoDatMatch`, null header, Manual resolve stored as `….pce`; with a fixture tg16 DAT entry, a headerless `.pce` then the same bytes with 512 bytes prepended → one game, then `Duplicate`; an unmatched 262,656-byte (headered) `.pce` → Review `NoDatMatch` with size 262,144, Manual resolve stores `….pce` with the 262,144 stripped bytes. e2e: System filter → `TurboGrafx-16 (1)`, URL `system=tg16`, `1 game`, `Neon Circuit` visible. Files: `IngestSeeder.cs`, `IngestSeederTests.cs`, `SeedIngestTests.cs`, `IngestPipelineTests.cs`, `mvp.spec.ts`. Tests: `IngestSeederTests` asserts the loose file is 262,656 bytes; `SeedIngestTests` asserts the ZIP under `roms/tg16/N/` with entry `Neon Circuit (USA).pce` equal to the 262,144 unheadered bytes, `originalFileName` `01-neon-circuit.pce`, `system: tg16`, null header, year/genre metadata. Done when: Core and Api test projects pass (Docker running) and `pwsh scripts/test-e2e.ps1` passes.
- [ ] **4. Docs**: README (TurboGrafx-16 in supported systems, Refresh DATs note, `data/ingest/tg16/`); `features.md` (systems list; `ingest/tg16/` `.pce`, 72 KiB–2.5 MiB, copier header stripped by size, always Review when unmatched; families list; size limits; Review first-extension list adds `.pce`); `zip-format.md` (`tg16/` folder, id, `header` null); `deployment.md` (`ingest/tg16/`, upgrade step); `development.md` (Nec project row, seed table with Neon Circuit, Basic counts); `adding-a-system.md` (box-art list; project list; TurboGrafx-16 as headerless + size-based copier strip example). Tests: none. Done when: every item above is in its doc and `pwsh scripts/test-all.ps1` passes.

## Decisions
- 2026-11-10 Naming → id `tg16`, `TurboGrafx-16`, short `TG16`, new project `RnRomManager.Systems.Nec` (user)
- 2026-11-10 512-byte copier header → strip when `length % 8192 == 512`, accepting three aftermarket protos go to Review (user)
- 2026-11-10 SuperGrafx and TurboGrafx-CD → out of scope, later specs (user)
- 2026-11-10 `ReadHeader` → always null (code: HuCards have no header; Atari 2600 pattern)
- 2026-11-10 Size limits → DAT min, DAT max + 512 (code: SNES/Atari pattern of DAT bounds)
- 2026-11-10 Colour teal `#0b7a75`, white text (default: distinct from the 12 in use, AA)
- 2026-11-10 Copier strip tested end to end: headered/unheadered duplicate and stripped Review resolve, as SNES (reviewer R1)
- 2026-11-10 SystemBadge test lives in `Pagination.test.ts`; Full seed counts and e2e option list named (reviewer R2, R3)

## Kickoff prompt
```text
/spec implement 2026-11-10-turbografx-16-support.md
```
Starts the spec, or resumes it at the first unticked step.
