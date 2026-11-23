---
title: Satellaview and Sufami Turbo support
status: ready # draft | ready | in-progress | completed
created: 2026-11-23
---

# Satellaview and Sufami Turbo support

## Summary
Adds two SNES add-on systems, **Satellaview** (`.bs` BS-X memory packs) and **Sufami Turbo** (`.st` mini-carts), each with its own ingest folder and libretro No-Intro DAT (as Famicom Disk System sits beside NES). Both play in the browser with the operator-supplied Snes9x core, which needs a BIOS from `Paths:Bios`: `BS-X.bin` for Satellaview, `STBIOS.bin` for Sufami Turbo. The SNES DAT lists neither (3,734 `.sfc`, 12 `.bin`), so today these files can't be stored at all.

## Out of scope
- Copier-headered `.bs`/`.st` dumps (no 512-byte strip; they fail the size check or go to Review).
- Sufami Turbo link play (two carts in the adapter, Snes9x's `retro_load_game_special` subsystem): one cart per launch only.
- BS-X broadcast data (`BSX%04X-%d.bin`), the BS-X BIOS as a playable game, and `.bs` files in the SNES folder (the folder decides the system, as for `.fds`).
- An app-side size check of `BS-X.bin`/`STBIOS.bin` (ColecoVision and FDS have none either).

## Behaviour

### Systems
- **B1** Two handlers in `RnRomManager.Systems.Snes` (as `FdsHandler` lives in `RnRomManager.Systems.Nes`), each its own family, embedded in the ZIP, all four metadat kinds (default `MetadatKinds`; libretro has `releaseyear`, `developer`, `publisher` and `genre` files for both):

  | | `SatellaviewHandler` (new) | `SufamiTurboHandler` (new) |
  |---|---|---|
  | `Id` / `Family` | `satellaview` | `sufami` |
  | `DisplayName` | `Satellaview` | `Sufami Turbo` |
  | `ShortName` | `BS-X` | `ST` |
  | `RomExtensions` | `[".bs"]` | `[".st"]` |
  | `MinRomSizeBytes` | 131,072 (128 KiB, the DAT's smallest) | 524,288 (512 KiB) |
  | `MaxRomSizeBytes` | 1,048,576 (1 MiB, the DAT's largest) | 1,048,576 (1 MiB) |
  | `DatName` | `Nintendo - Satellaview` | `Nintendo - Sufami Turbo` |
  | `EmbedRomsInZip` | `true` | `true` |
  | `BrowserCore` | `snes9x` | `snes9x` |

  Sufami Turbo's limits are exactly the range Snes9x 1.63 (`6ca2343`, the pinned build) treats as a Sufami Turbo cart (`is_SufamiTurbo_Cart`: 0x80000–0x100000 bytes); every entry in the DAT (2026.10.07) is 512 KiB or 1 MiB. The 256 KiB ST BIOS image therefore fails `TooSmall`.
- **B2** Satellaview `ReadHeader` always returns null (no header check, like `Atari2600Handler`): an unmatched `.bs` always goes to Review (`NoDatMatch`, suggestions from the file name), and its releases have no Header button.
- **B3** Sufami Turbo `ReadHeader` applies Snes9x's own content test (`is_SufamiTurbo_Cart`): ASCII `BANDAI SFC-ADX` at offset 0 (14 bytes) and **not** `SFC-ADX BACKUP` at 0x10 (14 bytes; that marks the BIOS). With both: `new RomHeader("sufami", null, true, { ["headerValid"] = true })`. Otherwise: `new RomHeader("sufami", null, false, { ["headerValid"] = false })`, so an unmatched file fails `InvalidHeader` (a matched one is ingested whatever its header, as for FDS). A span shorter than 0x1E bytes can't reach it (the minimum size is 512 KiB), but the reader returns the implausible header rather than throwing.
- **B4** Neither handler overrides `NormalizeForHashing` or `KeepsSourceExtension` (one extension each). Matched files are stored under the DAT's ROM name; resolved without a DAT entry they're stored as `.bs` / `.st`.
- **B5** Each handler embeds its own 512×512 default box art (`boxart.satellaview.png`, `boxart.sufami.png`), drawn for this project, with the SVG source next to it in `src/RnRomManager.Systems.Snes/Resources/boxart/` and the README there listing them (same wording and export command as the existing `snes.svg`).
- **B6** Registered in `AppHost.AddSystemHandlers` directly after `SnesHandler`, so every registration-order list reads `…, "snes", "satellaview", "sufami", …`. `ingest/satellaview/` and `ingest/sufami/` are created at startup like every system folder. Until **Admin → DATs → Refresh DATs** fetches the two DATs and their metadat, ingestion is refused with `DatsMissing` (existing behaviour for any new system).

### Catalogues and UI
- **B7** IGDB (`IgdbPlatforms.ById`): Step 2 looks up the platforms through the IGDB API with the app's configured credentials (`POST /v4/platforms`, `fields id,name; search "Satellaview";` then `"Sufami Turbo"`) and maps each system to the id IGDB returns for that exact platform name. A system IGDB has no platform for maps to `19` (SNES), and `IgdbPlatformsTests.PlatformIds_AreDistinctAndPositive` then leaves each fallback system out by id (one or both, each named with the reason) and asserts the remaining values are unique. The ids found go into § Decisions.
- **B8** System colours, same in both themes, white text (`foreground: '#fff'`), added to `app.css` after the Famicom Disk System block as one block each, in the same comment style: `:root { --bs-<id>: … }` plus `.text-bg-<id> { color: #fff !important; background-color: var(--bs-<id>) !important; }`; and to `systemColours.ts` (the `SystemColour.name` union and the `systemColours` map):
  - `satellaview`: `--bs-satellaview: #1f5f8b` (a satellite-dish blue; white on it ≈ 6.9:1, AA).
  - `sufami`: `--bs-sufami: #7a2e5a` (a cartridge plum; white on it ≈ 8.6:1, AAA).
  Step 2 checks both hex values aren't used by another `--bs-*` system colour; if one is, it picks the nearest unused shade with the same contrast class and records it in § Decisions.
- **B9** No icon mapping: both use the controller fallback, as `snes` does.
- **B10** `systemEmulators.json` (Systems tab), keys `satellaview` and `sufami`, each with the `snes` entries' `os` values: Snes9x (`https://www.snes9x.com/`), bsnes (`https://github.com/bsnes-emu/bsnes`) and RetroArch (`https://www.retroarch.com/?page=platforms`) with `note` `RetroArch core: Snes9x or bsnes (needs BS-X.bin)` for Satellaview and `RetroArch core: Snes9x or bsnes (needs STBIOS.bin)` for Sufami Turbo.

### Browser play
- **B11** `biosFor(core, name)` in `static/emulator/params.js` returns `STBIOS.bin` for `snes9x` with a release name ending `.st` and `BS-X.bin` for `snes9x` with one ending `.bs` (both case-insensitive), and still null for any other `snes9x` release (`.sfc`, `.smc`). The play page's existing flow then applies unchanged: the operator-core HEAD check first (so a missing Snes9x shows the existing *SNES games need the Snes9x core files…* message), then `GET /api/emulator/bios/<name>` with `biosError`'s messages (404: *This system needs its BIOS: ask an Admin to put STBIOS.bin in the BIOS folder (Paths:Bios).*), then `EJS_biosUrl` set to that URL. EmulatorJS writes the BIOS next to the ROM under the URL's last segment, and Snes9x's `LoadBIOS` looks in the ROM's folder first (`ROMFILENAME_DIR`), then the system folder. Quick play from the Library list takes the same path (it opens the same play page).
- **B12** Snes9x needs at least 256 KiB of `STBIOS.bin` and 1 MiB of `BS-X.bin` (it reads exactly that much and fails the load otherwise, showing EmulatorJS's own start error). The docs give those sizes.
- **B13** The operator-core option and its messages stay SNES-worded (*SNES games need the Snes9x core files…*): the add-ons are SNES hardware, and `operatorCores.snes9x.system` is per core, not per system.
- **B14** Snes9x 1.63 sends a `.bs` to the BS-X path only when its header passes `is_bsx` at 0x7FC0 or 0xFFC0; any other `.bs` loads as a plain SNES cart (`LoadROMMem`), which may not boot. The app doesn't predict this: Play is always offered. With the BIOS, Snes9x boots the BS-X BIOS with the pack in its slot; whether a game then starts directly or from the BIOS's own menu isn't verified (§ Risks). `bs` and `st` are already in Snes9x's compiled `valid_extensions`, so `BrowserCoreExtensionsTests` needs no exception.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core (systems) | `src/RnRomManager.Systems.Snes/SatellaviewHandler.cs` (new), `SufamiTurboHandler.cs` (new) | B1–B4. Box art loaded through a shared loader: make `SnesHandler.LoadResource` `internal static` (as `NesHandler.LoadResource` is) and call it. |
| Core (systems) | `src/RnRomManager.Systems.Snes/Resources/boxart/satellaview.svg`, `.png`, `sufami.svg`, `.png` (new), `README.md` | B5. The `.csproj`'s `<EmbeddedResource Include="Resources/boxart/*.png" />` already picks up the PNGs. |
| Api | `src/RnRomManager.Api/AppHost.cs` | B6: two `services.AddSystemHandler<…>()` lines after `SnesHandler`. |
| Core (media) | `src/RnRomManager.Core/Media/IgdbPlatforms.cs` | B7. |
| Web | `src/RnRomManager.Web/src/app.css`, `src/lib/systemColours.ts` | B8. |
| Web | `src/RnRomManager.Web/src/lib/systemEmulators.json` | B10. |
| Web (play page) | `src/RnRomManager.Web/static/emulator/params.js` | B11 (`biosFor` and its JSDoc). |
| Tests | `tests/RnRomManager.Systems.Snes.Tests/SatellaviewHandlerTests.cs` (new), `SufamiTurboHandlerTests.cs` (new) | Contract, header, box art (as `FdsHandlerTests`). |
| Tests | `tests/RnRomManager.TestData/IngestSeeder.cs` | `SatellaviewDat` / `SufamiTurboDat` constants, `ingest/satellaview` and `ingest/sufami` folders, `[SatellaviewDat] = []` and `[SufamiTurboDat] = []` in its fixture DAT map. |
| Tests | Every integration fixture map that lists `[IngestSeeder.FdsDat]` (22 maps in the integration test files, from `AuditLibraryEditTests` to `ZipEntryEndpointsTests`; `ReviewEndpointsTests` has 3, `IngestReviewTests` 2, the rest 1 each; `DatEndpointsTests`' second match is the line-273 list in its own row) | Add both DATs (empty, except `IngestPipelineTests`, below), or the `DatsMissing` preflight fails. |
| Tests | `DatEndpointsTests` | DAT name count (line 149) 36 → 38 and its message ("27 libretro systems…"); the ordinal-sorted `/api/admin/dats` name list (line 273) gains `SatellaviewDat` and `SufamiTurboDat` before `SnesDat`; refresh request count (line 356) 118 → 128 (5 each) and its message. |
| Tests | `tests/RnRomManager.Core.Tests/Ingest/IngestSeederTests.cs` | `*.dat` count (line 65) 126 → 136. |
| Tests | `SystemStartupTests`, `IngestEndpointsTests` (`Pending_ListsEverySystem_IgnoringJunk`'s list), `LibraryEndpointsTests` (the id list sorted by `DisplayName` at line 165, the `embedRoms` count at line 166 `Has.Exactly(23)` → 25, the `displayName` list at line 187 gains `Satellaview` and `Sufami Turbo`, and the `TestCase` rows giving each system's `browserCore`) | New ids in their places. |
| Tests | `IngestPipelineTests` | B1–B3 end to end (Step 2). |
| Tests | `IgdbPlatformsTests`, `systemColours.test.ts`, `emulatorParams.test.ts`, `emulatorPlay.test.ts` | B7, B8, B11. |
| Docs | `README.md` supported-systems rows | Step 2 (`SupportedSystemsTableTests` builds its rows from the registered handlers, so they land with the registration). |
| Docs | `README.md` (prose), `docs/features.md`, `docs/configuration.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md`, `docs/zip-format.md` | Step 4. |

No migration: `system_id` is a `VARCHAR`. No config key: `Paths:Bios` and `Emulator:Snes9xCorePath` already exist. No API contract change beyond two more systems in `/api/systems` and the ingest listing.

## Risks
- **BS carts the core doesn't recognise (B14):** some `.bs` releases may not boot. No real dump is in the repo; Step 3's "Done when" asks the implementer to try one DAT-matched `.bs` and one `.st` locally if they own them, and to record the result (CRC, whether it booted directly or through the BS-X menu) in § Decisions, as the Lynx `.bll` check was; if they can't, the PR says so.
- **Test fixture churn:** 22 fixture maps change in Step 2 (found by searching for `[IngestSeeder.FdsDat]`), plus the count and list assertions named in § Changes, which that search doesn't find.
- **BS-X boot flow:** loading a pack with `BS-X.bin` starts the BS-X BIOS; if games then need to be launched from the BIOS's town menu rather than booting directly, the Play experience is worse but still works. The local check below records which it is, and `docs/features.md` says so.
- **IGDB lookup needs credentials:** if the implementer has none, both systems map to `19` (B7's fallback) and the PR says the ids weren't checked.

## Steps
- [ ] **1. Handlers and box art**: B1–B5. `SatellaviewHandler`, `SufamiTurboHandler`, the two SVG/PNG pairs, the box-art README, `SnesHandler.LoadResource` made `internal static`. Not registered yet. Files: `src/RnRomManager.Systems.Snes/*`, `tests/RnRomManager.Systems.Snes.Tests/SatellaviewHandlerTests.cs` (new), `SufamiTurboHandlerTests.cs` (new). Tests: the two new classes and `SnesHandlerTests`. Done when: the contract tests pin every B1 value; `SufamiTurboHandlerTests` covers a synthetic 512 KiB image with the magic (plausible, `headerValid` true), one without it (implausible), one with the magic and `SFC-ADX BACKUP` at 0x10 (implausible) and an empty span (implausible, no throw); `SatellaviewHandlerTests` asserts `ReadHeader` returns null; both box arts are valid 512×512 PNGs; no real ROM data is committed.
- [ ] **2. Register and catalogue**: B6–B10 plus ingest end to end. `AppHost`, `IgdbPlatforms` (B7 lookup), colours, `systemEmulators.json`, `IngestSeeder` and every integration fixture map, the ordered/sorted system lists and counts in tests (§ Changes), and the README supported-systems rows. `IngestPipelineTests` gains: a synthetic `.st` with the magic, listed in its fixture DAT as `Star Duel (Japan).st`, ingests as `sufami` under that name; an unlisted `.st` without the magic fails `InvalidHeader`; an unlisted `.bs` goes to Review as `NoDatMatch`. Files: as in § Changes. Tests: `SystemStartupTests`, `IgdbPlatformsTests`, `SystemEmulatorsCatalogTests`, `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests`, `IngestSeederTests`, `DatEndpointsTests`, `IngestEndpointsTests`, `IngestPipelineTests`, `LibraryEndpointsTests`, every other integration class whose fixture map changed, `systemColours.test.ts` (Docker needed for the integration classes). Done when: those pass; `ingest/satellaview/` and `ingest/sufami/` are created by the startup test host; § Decisions records the IGDB ids (or the fallback) and the final colours.
- [ ] **3. Browser play**: B11–B14. `params.js` `biosFor`. Files: `src/RnRomManager.Web/static/emulator/params.js`, `src/RnRomManager.Web/src/lib/emulatorParams.test.ts`, `src/RnRomManager.Web/src/lib/emulatorPlay.test.ts`. Tests: `emulatorParams.test.ts` (`biosFor('snes9x', …)` for `X (Japan).st`, `X (Japan).ST`, `X (Japan).bs`, `X (Japan).BS` and null for `X (USA).sfc`), `emulatorPlay.test.ts` (an `.st` release requests the operator-core HEAD, then `/api/emulator/bios/STBIOS.bin`, then the ROM, and sets `EJS_biosUrl` to that URL with `EJS_core: 'snes9x'`; a 404 on `BS-X.bin` shows B11's message naming `BS-X.bin` and requests no ROM). Done when: `npm run lint`, `npm run check` and those vitest files pass; the local check from § Risks is done or the reason it wasn't is noted for the PR.
- [ ] **4. Docs**: the doc rule. `README.md` (the Play list and its BIOS notes, the upgrade sentence's system list; the table rows landed in Step 2), `docs/features.md` (ingest paragraph: folders, extensions, size limits, the ST magic and `InvalidHeader`, `.bs` always to Review, the two new families in the `UnsupportedType` family list, and the SNES sentence "Satellaview, Sufami Turbo, `.swc`/`.fig` and interleaved or split dumps aren't supported" reworded to point `.bs`/`.st` at their folders; the Play section: Snes9x, the BIOS files, B14's limit and the boot flow found in Step 3), `docs/development.md` (the `RnRomManager.Systems.Snes` project-table row gains both handlers; the Play page paragraph's `biosFor` list gains `STBIOS.bin`/`BS-X.bin` for `snes9x` with `.st`/`.bs`), `docs/configuration.md` (`Paths:Bios` row: `STBIOS.bin` 256 KiB and `BS-X.bin` 1 MiB, both required for their system), `docs/deployment.md` (ingest-folder list, BIOS-folder bullet, an "Upgrading to the release that adds Satellaview and Sufami Turbo" section like FDS's), `docs/adding-a-system.md` (the `BrowserCore` row: both return `snes9x`, and `biosFor`'s new names; the box-art list; `ReadHeader` examples), `docs/zip-format.md` (folder list; Sufami Turbo `header` has only `headerValid`). Tests: `SupportedSystemsTableTests`. Done when: it passes, every behaviour above is in a doc, and `docs/codemap.md` is regenerated. No Playwright flow changes (no new UI).

## Decisions
- 2026-11-23 Where do `.bs`/`.st` go? → Two new systems with their own No-Intro DATs, not extra SNES extensions: the SNES DAT lists neither, and each needs its own BIOS (user)
- 2026-11-23 System ids and badges → `satellaview` / `BS-X` and `sufami` / `ST` (user)
- 2026-11-23 Unmatched-file checks → Sufami Turbo needs Snes9x's `BANDAI SFC-ADX` magic (else `InvalidHeader`); Satellaview has no check (user)
- 2026-11-23 IGDB ids → looked up at implement time; a missing platform falls back to SNES (19) with a stated test exception (user)
- 2026-11-23 Copier headers → not stripped (user)
- 2026-11-23 How does the pinned Snes9x find the BIOS? → `6ca2343` `retro_load_game`: `LoadBIOS` reads `STBIOS.bin` (0x40000 bytes) or `BS-X.bin` (0x100000) from the ROM's folder, then the system folder; the `.wasm` in the pinned `snes9x-wasm.data` (SHA-256 `eaa0bcfc…`) holds both names and reports version `1.63 6ca2343` (code)
- 2026-11-23 Review round 1: added the DAT/seeder/library count assertions, README rows moved to Step 2, `docs/development.md`, the `.text-bg-*` classes and `SystemColour.name` union, per-id IGDB fallback exclusions, the `SFC-ADX BACKUP` exclusion, fixture map count, BS-X boot-flow risk; round 2: fixture-map split corrected (reviewer)
- 2026-11-23 DAT facts → Satellaview 569 games, `.bs` 128 KiB–1 MiB; Sufami Turbo 13 games, `.st` 512 KiB or 1 MiB; both have all four metadat files (libretro-database master, 2026-11-23) (code)

## Kickoff prompt
```text
/spec implement 2026-11-23-satellaview-sufami-turbo.md
```
Starts the spec, or resumes it at the first unticked step.
