---
title: TurboGrafx-CD support
status: in-progress
created: 2026-11-20
---

# TurboGrafx-CD support

## Summary
Adds TurboGrafx-CD / PC Engine CD (`tgcd`) as a disc system. Its Redump bin/cue sets and standalone `.chd` images are imported against the Redump DAT and stored next to the game's ZIP, as Sega CD discs are. Releases play in the browser on the already-vendored Beetle PCE core (`mednafen_pce`, shared with `tg16` HuCards) through play-disc, with a System Card BIOS the operator supplies.

It also tidies two UI spots (rules U1–U4, added at the user's request): the signed-in footer's About and Shortcuts buttons become icon-only, the Play dialog's save and screenshot buttons move to its footer, and the play page's loading label shows the file size.

## Out of scope
- CloneCD (`.ccd`/`.img`/`.sub`), `.iso`, `.toc`, and audio as `.wav`/`.ogg`: ingest and play-disc group only `.cue` with `.bin` tracks.
- SuperGrafx (`.sgx`) and Arcade Card firmware (Beetle PCE emulates the Arcade Card itself; `pce_arcadecard` stays at its `enabled` default).
- Multi-disc sets (`set=1` stays refused for `mednafen_pce`), and any change to TurboGrafx-16 HuCard play.
- Bundling any System Card BIOS.
- A header reader: `ReadHeader` returns null, as for Sega CD.
- Seeded TurboGrafx-CD games in the Basic/Full test sets and an e2e play case (vitest covers the player, as for Sega CD).
- Real-game boot checks (System Card found, CD audio, CD backup-RAM battery saves): the maintainer does them after merge, as for the previous browser-play specs.

## Behaviour
- **B1 Handler.** `TurboGrafxCdHandler` (new) in `RnRomManager.Systems.Nec`:

  | Member | Value |
  |---|---|
  | `Id` / `Family` | `tgcd` / `tgcd` |
  | `DisplayName` / `ShortName` | `TurboGrafx-CD / PC Engine CD` / `TGCD` |
  | `RomExtensions` | `.bin`, `.cue`, `.chd` (`[0]` = `.bin`) |
  | `MinRomSizeBytes` / `MaxRomSizeBytes` | 16 / 1 GiB (per file, as Sega CD) |
  | `DatName` | `NEC - PC Engine CD - TurboGrafx-CD` (libretro `metadat/redump/`, and the libretro-thumbnails folder; both verified 2026-11-20) |
  | `DatFolder` / `MetadatKinds` | `DatKind.Redump` / `[]` |
  | `EmbedRomsInZip` / `FileSets` | false / true |
  | `BrowserCore` | null until Step 6, then `mednafen_pce` |
  | `ReadHeader` | null always |
  | `DefaultBoxArtPng` | a new 512×512 placeholder drawn for this project (`boxart.tgcd.png`), with its SVG source `tgcd.svg`, in `Resources/boxart/`, loaded through the handler's own `LoadResource` as `TurboGrafx16Handler` does |

  Because `tgcd` is its own family, nothing in `ingest/tg16/` is tried against the TurboGrafx-CD DAT, and the other way round (`tg16` accepts only `.pce` anyway).
- **B2 Registration.** `AddSystemHandler<TurboGrafxCdHandler>()` goes after `Nintendo3DsHandler` in `AppHost.AddSystemHandlers`, so `tgcd` is last in registry order. Startup creates `roms/tgcd/` and `ingest/tgcd/` with their standard subfolders. `/api/systems` lists it by `DisplayName` (`OrdinalIgnoreCase`): after `TurboGrafx-16 / PC Engine`, before `Virtual Boy`. The README row sits between those two. Badge colour `--bs-tgcd: #4c3a99` (indigo, the same in both themes; white on it is AA, ~8.9:1; no other token uses that hex), foreground `#fff`, and the `bi bi-disc` icon (`'system-tgcd'`). `IgdbPlatforms.ById` gains `["tgcd"] = 150` (IGDB "Turbografx-16/PC Engine CD"; see B10). `systemEmulators.json` gains `"tgcd"` with a copy of the `"tg16"` entries (Mednafen, ares, RetroArch) whose RetroArch note becomes `RetroArch core: Beetle PCE` (Beetle PCE FAST also plays CDs, but the copy names the core this app uses). After upgrading, an Admin runs **Refresh DATs** once (one Redump file, no metadat); until then ingestion returns `DatsMissing`.
- **B3 Ingest.** Nothing new in Core: the `FileSets` rules apply as for Sega CD. A `.cue` with its `.bin` tracks is one release whose main file is the data track (as for every disc system); files keep their exact names next to the ZIP; a taken name is `NameTaken`; a DAT hit on a track names the game; an unmatched `.bin` or `.chd` goes to Review as `NoDatMatch`. `.cue`/`.bin` Review items follow the existing `ReviewResolver` set rules.
- **B4 play-disc.** `PlayDisc` gains `public const string TurboGrafxCdSystemId = "tgcd"` (new), added to `SupportedSystems`. The shared 2 GiB `MaxBytes` cap applies. `OpenDiscAsync` and `ShapeProblem` take `string systemId` (the game's) in place of `bool psp`: `HandleAsync` and `HandleSetAsync` pass `game.SystemId` to `OpenDiscAsync`, which passes it on to `ShapeProblem`, and `ShapeProblem` derives `psp` from `systemId == PspSystemId`. `HandleAsync`'s `var psp` local, used only for that call, goes. `ShapeProblem` gets a TurboGrafx-CD branch for `systemId == TurboGrafxCdSystemId`, checked after the name-uniqueness check: one member is valid only when it ends in `.chd` (`OrdinalIgnoreCase`); several members follow the existing cue-set rule (one `.cue`, the rest `.bin`, the main file a `.bin`, at most 99). A lone `.bin` (or any other single file) is 409 `InvalidPlayDisc` whose ProblemDetails `title` (built by `PlayDisc.Invalid`; `detail` stays null) is `The disc can't be played: a TurboGrafx-CD disc must be a .cue with its .bin tracks, or a .chd.` Beetle PCE boots only a `.cue`/`.ccd`/`.chd`/`.toc`/`.m3u` as a CD and would run a bare `.bin` as a HuCard (source read 2026-11-20). The psx, psp, saturn, segacd and 3do shapes are unchanged.
- **B5 Play eligibility.** `mednafen_pce` joins `LibraryEndpoints.ExternalBrowserCores` and `PlayDiscCores`, and `externalBrowserCores` and `playDiscCores` in `emulator.ts`. A `tg16` release can't be external in practice (its family embeds ROMs), so no existing case changes. **Lone `.bin` rule:** an external release whose main file ends in `.bin` (any case) and that has no attached files is not playable on `mednafen_pce`. The rule is per core, like the `.nrg` rule, and is the same in the API and the SPA:
  - `GameListItem` gains `bool PrimaryHasFiles = false` (new, last parameter): true when the primary release has `rom_files` rows. `GameRepository`'s list query (`LibraryRepositories.cs`) selects `(SELECT EXISTS(SELECT 1 FROM rom_files f WHERE f.rom_id = r.id) FROM roms r WHERE r.game_id = g.id AND r.is_primary = 1 ORDER BY r.id LIMIT 1) AS PrimaryHasFiles`, next to `PrimaryIsExternal`; the query's row class gains `public bool? PrimaryHasFiles { get; init; }` next to `PrimaryIsExternal`, and the `new GameListItem(...)` mapping passes `r.PrimaryHasFiles == true`. `/api/games`' `playable` adds `&& !(core == "mednafen_pce" && game.PrimaryIsExternal && !game.PrimaryHasFiles && game.PrimaryRomFileName?.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) == true)`, as the `.nrg` rule reads `PrimaryRomFileName`.
  - `canPlay(game, rom)` in `emulator.ts` widens `rom` to `Pick<RomDetail, 'externalFileName'> & Partial<Pick<RomDetail, 'files'>>` (so the existing `emulator.test.ts` calls without `files` still type-check) and returns false for `game.browserCore === 'mednafen_pce'` with an `externalFileName` ending in `.bin` (any case) and `files` missing, null or empty. The game page's callers pass a full `RomDetail`.
- **B6 Play URL.** Unchanged code: `usesPlayDisc` adds `&disc=1` for an external `mednafen_pce` release once B5 adds the core, and `play.js` then loads it from play-disc; `tg16` cartridges have no `disc` and load from `/download` as today. `parsePlayParams` needs no change (`mednafen_pce` is already allowed; `set` stays refused for it).
- **B7 TurboGrafx-CD launch.** For `mednafen_pce` with `disc` (`isTurboGrafxCd` (new) in `play.js`), the player, in order:
  1. fetches the System Card **one candidate after another, in the order `syscard3.pce`, `syscard3u.pce`, `syscard2.pce`, `syscard2u.pce`, `syscard1.pce`** (`turboGrafxCdCards` (new) in `params.js`), each with `loadOptionalBios(name, turboGrafxCdBiosMaxBytes, turboGrafxCdBiosSizeError(name), false)`, and **stops at the first one found** (the rest aren't requested). A 404 skips a file; a file that is empty or larger than 1 MiB stops the launch with `turboGrafxCdBiosSizeError(name)`: "The TurboGrafx-CD BIOS <name> in the BIOS folder (Paths:Bios) is empty or larger than 1 MiB: ask an Admin to replace it."; any other failure stops it with `optionalBiosError`'s message;
  2. if none was found, stops with `turboGrafxCdBiosMissing`: "TurboGrafx-CD games need a System Card BIOS: ask an Admin to put syscard3.pce in the BIOS folder (Paths:Bios)." No `gexpress.pce`, disc or loader request follows. A System Card is required even when `gexpress.pce` exists, because the app can't tell a Games Express disc apart;
  3. fetches `gexpress.pce` the same way (optional: a 404 is fine; the size and other errors stop the launch as in step 1);
  4. loads the disc from play-disc (`discError` unchanged, non-PSP wording);
  5. sets `EJS_externalFiles = { '/syscard3.pce': <the card found>, '/gexpress.pce': <if found> }`. The card always goes in under `syscard3.pce`, the file Beetle PCE's default `pce_cdbios` value (`System Card 3`) opens. The core never decodes a US card differently, so `syscard3u.pce` bytes work under that name (source read 2026-11-20). The core detects Games Express discs itself and opens `gexpress.pce` for them. It also sets `EJS_hideSettings = ['pce_cdbios']`, so no player can pick a card file that isn't there. No core option is set: EmulatorJS 4.2.3 stores every option per game, and a stored value overrides `EJS_defaultOptions`. `EJS_disableCue` stays unset (the core is on EmulatorJS's disable list, and a play-disc set always carries its cue).
- **B8 Bounded BIOS read.** `loadOptionalBios(name, size, sizeError, exact = true)` (`play.js`) gains the `exact` parameter. With `exact` true it behaves as today. With false, `size` is a maximum: more bytes than `size`, or zero bytes, gives `{ error: sizeError }`, and otherwise it returns `{ bios: <ArrayBuffer of exactly the bytes read> }`. Beetle PCE accepts a System Card of any size and strips a 512-byte copier header itself (`HuC_Load`), so dumps of 256 KiB and 256 KiB + 512 both work. `turboGrafxCdBiosMaxBytes = 1048576` (new).
- **B9 Content name guard.** `contentFileName` also renames, for core `mednafen_pce` (with or without `disc`), a content file whose name is any of `turboGrafxCdBiosNames` (new: the five card names plus `gexpress.pce`, any case) to `<game id>-<name>`, as for the other disc cores. All six names pass `IsBiosName`.
- **B10 Assumptions checked in Step 5 and Step 2.** Before writing the player code (Step 5), check outside the repo and record the results in § Decisions; if any is false, stop and ask the user:
  - EmulatorJS 4.2.3's `addToMenu` (`emulator.js` ~4669) receives the core option key (`pce_cdbios`) as `id` for core options, so `EJS_hideSettings` hides it;
  - with EmulatorJS 4.2.3's RetroArch (no `system_directory` in its `retroarch.cfg`), Beetle PCE's firmware path resolves to the content root `/`, where `EJS_externalFiles` writes the cards. Yabause and Opera already rely on this.

  In Step 2, confirm the IGDB platform id 150 with an authenticated `POST /v4/platforms` query (`fields id,name,slug; where slug = "turbografx-16-slash-pc-engine-cd";`) if credentials are configured, else from IGDB's platform page. If it isn't 150, use the confirmed id and record it.

### UI changes (U)
- **U1 Footer Shortcuts button.** In `AppFooter.svelte` the Shortcuts button shows only an icon: `keyboard` (new in `icons.ts`: `'bi bi-keyboard'`) in place of `help`, and the `<span class="footer-label">Shortcuts</span>` goes. It gains `aria-label="Shortcuts"` (so its accessible name stays `Shortcuts`, which `mvp.spec.ts` and `AppFooter.test.ts` find it by) and keeps `title="Keyboard Shortcuts"`. Its placement, `ms-auto` rules and hiding while the password must be changed are unchanged. The `help` icon mapping stays (other code may use it).
- **U2 Footer About button.** The About button shows only the app logo, `<img src="/favicon.svg" alt="" width="16" height="16" class="footer-logo">` (the same file as the browser tab icon; `alt=""` because the button names itself), in place of `<Icon name="info" />`, and its `footer-label` span goes. It gains `aria-label="About"` and `title="About"`. `.footer-logo` (new, in the component's `<style>`) is `vertical-align: -0.125em`, matching Bootstrap Icons' baseline, so the row's height doesn't change.
- **U3 Play dialog footer.** `ConfirmModal` gains an optional `footer?: Snippet` prop. When given, it renders first in `.modal-footer`, inside `<div class="d-flex flex-wrap gap-2 me-auto">`, before the Close/Cancel button in both modes, so it sits on the left with Close/Cancel (and the confirm button when `onconfirm` is set) on the right. Without it the footer is unchanged. `EmulatorModal` moves its whole button row (**Save state to library**, **Save battery to library**, **Screenshot**, with their current conditions, classes, icons, labels and `disabled={exporting}`) from the `d-flex flex-wrap gap-2 mb-2` div above the game into that `footer` snippet, still rendered only while `open && rom && core`. The div above the game goes, so the 4:3 frame starts at the top of the body. The screenshot preview's **Save as Snap** and **Discard** stay over the game. The frame's `max-width: calc((100vh - 14rem) * 4 / 3)` is unchanged: the footer was already part of the 14 rem chrome.
- **U4 Loading size.** `play.js`'s `readContent` label (`#downloaded`) shows the size:
  - with a known total, `<percent> of <total size>`, e.g. `50% of 512.0 MiB` (the percent as today, floored and capped at 100%);
  - with no total, the running amount as a size, e.g. `12.5 MiB`, in place of `13,107,200 bytes`;
  - before the first chunk, `0% of <total size>` with a known total, and `0 B` without one.

  Sizes use `contentSize(bytes)` (new, exported from `params.js`), which copies `formatSize` in `src/lib/format.ts` (`512 B`, `1.5 KiB`, `16.0 MiB`, `8.0 GiB`, one decimal): the static play page can't import from `src/lib`. A comment on each names the other so they stay in step. Both `play.html` and `play-psp.html` show it; the label is recomputed per chunk only when its text changes, as today.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | `src/RnRomManager.Systems.Nec/TurboGrafxCdHandler.cs` (new), `Resources/boxart/tgcd.svg` + `tgcd.png` (new), `Resources/boxart/README.md` | New handler (B1). The `.csproj` already globs `Resources/boxart/*.png`. |
| Api | `src/RnRomManager.Api/AppHost.cs` | Registration (B2). The Nec project is already referenced and in the Dockerfile restore layer. |
| Core | `src/RnRomManager.Core/Media/IgdbPlatforms.cs` | `tgcd` → 150 (B2, B10). |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | `TurboGrafxCdSystemId`, `SupportedSystems`, the `ShapeProblem` branch (B4). |
| Core / Data | `src/RnRomManager.Core/Library/GameListItem.cs`, `src/RnRomManager.Data/Repositories/LibraryRepositories.cs` | `PrimaryHasFiles` (B5). |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | `mednafen_pce` in both core sets; the lone `.bin` rule in `playable` (B5). |
| Test data | `tests/RnRomManager.TestData/IngestSeeder.cs` | `TurboGrafxCdDat = "NEC - PC Engine CD - TurboGrafx-CD"` (new) in `RedumpLayouts` (comment: "nine disc systems"); `tgcd` in the disc-folder loop; an empty catalog in the `FixtureDatWriter.Write` call. |
| Web | `static/emulator/params.js`, `static/emulator/play.js` | B7–B9 constants, messages, guard; the launch path and `loadOptionalBios`'s `exact`. |
| Web | `src/lib/emulator.ts` | Core lists and `canPlay` (B5). |
| Web | `src/lib/systemColours.ts`, `src/app.css`, `src/lib/components/icons.ts`, `src/lib/systemEmulators.json` | Colour, icon, emulator links (B2). |
| Web | `src/lib/components/AppFooter.svelte`, `src/lib/components/icons.ts` | Icon-only About and Shortcuts buttons (U1, U2). |
| Web | `src/lib/components/ConfirmModal.svelte`, `src/lib/components/EmulatorModal.svelte` | `footer` snippet; the Play dialog's buttons move into it (U3). |
| Web | `static/emulator/params.js`, `static/emulator/play.js` | `contentSize` and the sized loading label (U4). |
| Docs | `README.md`, `docs/adding-a-system.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/features.md`, `docs/development.md` | See Step 9. |

No migration (`system_id` is a `VARCHAR`; `PrimaryHasFiles` is a computed column of the list query). No new config key. API contract: additive. `/api/systems` gains a row, and `browserCore`/`playRomId`/`playDisc` are set for TurboGrafx-CD games. `GameListItem` gains a defaulted parameter, so existing constructor calls compile unchanged.

## Risks
- **Shared core.** Beetle PCE now serves two systems. The `disc` flag keeps HuCards on their current path. No vitest covers the HuCard play page today, so Step 5 adds one; the existing `LibraryEndpointsTests` `tg16` cases and the e2e Quick Play case (`mvp.spec.ts`, "Play: TurboGrafx-16 / PC Engine Quick Play…") pass unchanged.
- **BIOS location and setting hiding** are inferred from source, not run. Step 5 checks them first (B10), and the maintainer's post-merge boot check confirms them.
- **Green commits.** `SupportedSystemsTableTests` compares README rows with handlers, so Step 2 adds the row with `—` and Step 6 switches it to `` `mednafen_pce` ``. `IgdbPlatformsTests` and `SystemEmulatorsCatalogTests` require every registered system, so Step 2 adds both entries with the registration. Every full-registry DAT manifest in the API integration tests must list `IngestSeeder.TurboGrafxCdDat` once the handler is registered; Step 2 updates them all in one commit.
- **`BrowserCoreExtensionsTests`** (PR rniemand/RnRomManager#168, not on `main` when this spec was written): `mednafen_pce`'s compiled-in list has no `bin`. If that test is on `main` when Finish merges `origin/main`, add the exception `[("tgcd", ".bin")] = "a cue set's data track; a lone .bin never plays (canPlay, play-disc)"` in the merge commit.
- **Licences.** Nothing new is bundled (`mednafen_pce` is already vendored with its notices); no change to `licenses.json`. The README states that no System Card BIOS is included.

## Steps
Steps 2, 3, 4 and 6 run integration tests (category `Integration`), which need Docker. Playwright (`mvp.spec.ts`) runs only in Finish.

- [x] **1. TurboGrafx-CD handler** (B1): `TurboGrafxCdHandler` with `BrowserCore` null, and its box art (SVG + 512×512 PNG rendered as the Nec boxart README says). `IngestSeeder.TurboGrafxCdDat` constant, added to `RedumpLayouts`; the seeder doesn't write it yet, so its output is unchanged.
  - Files: `TurboGrafxCdHandler.cs`, `Resources/boxart/tgcd.svg`, `tgcd.png`, `Resources/boxart/README.md`, `tests/RnRomManager.Systems.Nec.Tests/TurboGrafxCdHandlerTests.cs` (new), `IngestSeeder.cs`.
  - Tests: `TurboGrafxCdHandlerTests` (new), copying `SegaCdHandlerTests`: every B1 value, `ReadHeader` null for any bytes, `NormalizeForHashing` unchanged, PNG valid and 512×512; with a registry holding only this handler, `DatCache` loads a `FixtureDatWriter` Redump catalog with one synthetic cue/bin game and finds its `.bin` by SHA-1, and loads an empty catalog.
  - Done when: those tests pass and the solution builds with warnings as errors.
- [x] **2. Register TurboGrafx-CD and update fixtures** (B2, B10 IGDB):
  - Registration after `Nintendo3DsHandler`. README row `| TurboGrafx-CD / PC Engine CD | `tgcd` | Next to ZIP | `.bin`, `.cue`, `.chd` | Redump | — |` between TurboGrafx-16 / PC Engine and Virtual Boy.
  - `IgdbPlatforms.ById["tgcd"]` (after checking B10's id); `systemEmulators.json` `"tgcd"` entry; colour (`systemColours.ts` union and map, `app.css` block with the contrast comment); icon (`icons.ts`).
  - `IngestSeeder`: `tgcd` in the disc-folder loop and `[TurboGrafxCdDat] = []` in the Basic/Full `FixtureDatWriter.Write` call.
  - Every API integration test file with a full-registry manifest (every file that lists `IngestSeeder.ThreeDsDat`) adds `[IngestSeeder.TurboGrafxCdDat] = []`, or ingestion's preflight fails `DatsMissing`: `IngestPipelineTests` (whose Sega CD catalog is filled in; Step 3 replaces its empty TurboGrafx-CD catalog with a fixture game), `AuditLibraryEditTests`, `DatEndpointsTests`, `GamePlayedEndpointTests`, `ImageEndpointsTests`, `ImageUrlEndpointsTests`, `IngestArchiveTests`, `IngestDeleteEndpointsTests`, `IngestEndpointsTests`, `IngestRestoreEndpointsTests`, `IngestReviewTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `NoteEndpointsTests`, `ReportEndpointsTests`, `ReviewEndpointsTests`, `ZipEntryEndpointsTests`.
  - Counts and lists:
    - `DatEndpointsTests`: DAT names 35 → 36, message "25 libretro systems, the 9 disc systems, Switch and the test titledb system"; `TurboGrafxCdDat` in the ordinal list right after `TurboGrafx16Dat` (`NEC - PC Engine - …` < `NEC - PC Engine CD - …` < `Nintendo - …`); refresh requests 117 → 118.
    - `IngestSeederTests`: `.dat` files 125 → 126.
    - `SystemStartupTests`: `tgcd` appended to both id lists.
    - `IngestEndpointsTests.Pending_ListsEverySystem_IgnoringJunk`: `tgcd` last.
    - `LibraryEndpointsTests`: the display-name-ordered id list gains `tgcd` after `tg16`; the name list gains `TurboGrafx-CD / PC Engine CD` after `TurboGrafx-16 / PC Engine`; the `/api/systems` row for `tgcd` reads `TurboGrafx-CD / PC Engine CD`/`TGCD`.
    - `systemColours.test.ts` and `stores.svelte.test.ts` (`iconClass('system-tgcd')` is `bi bi-disc`).
  - Files: `AppHost.cs`, `README.md` (row only), `IgdbPlatforms.cs`, `IngestSeeder.cs`, the test files above, `systemColours.ts`, `app.css`, `icons.ts`, `systemEmulators.json`. Mechanical manifest lines don't count toward the line budget.
  - Before committing, grep `tests/` for `IngestSeeder.ThreeDsDat` and check every hit also lists `TurboGrafxCdDat`.
  - Tests: the updated integration files (`IngestPipelineTests` included), `IngestSeederTests`, `SeedIngestTests` (Basic/Full counts unchanged), `SystemStartupTests`, `SupportedSystemsTableTests`, `IgdbPlatformsTests`, `SystemEmulatorsCatalogTests`, `systemColours.test.ts`, `stores.svelte.test.ts`; web `npm run lint` and `npm run check`.
  - Done when: those pass and `pwsh scripts/build-docker.ps1` builds.
- [x] **3. Verify TurboGrafx-CD ingest** (B3): Testcontainers cases in `IngestPipelineTests.cs` and `ReviewEndpointsTests.cs` with synthetic files (never real images):
  - a cue + 3 bins (`(Track 01)` audio, `(Track 02)` data, `(Track 03)` audio) where the fixture DAT lists only Track 02, as libretro's TurboGrafx-CD DAT lists one track per game, usually Track 02 → one release named after that DAT entry, its main file Track 02, files next to the ZIP under their exact names, info.json `files` lists the cue and the other tracks;
  - an unmatched standalone `.chd` → Review `NoDatMatch` with system `tgcd`, resolvable with **Manual**, stored as `.chd`;
  - a `.pce` dropped in `ingest/tgcd/` fails `UnsupportedType`: it's moved to `ingest/tgcd/_failed/` with its `.error.txt`, and nothing is stored under `roms/tgcd/`;
  - a file over 1 GiB is `TooLarge` (the existing size-stub pattern).
  - Done when: those cases and the existing ingest suites pass.
- [x] **4. TurboGrafx-CD play-disc** (B4): `BrowserCore` stays null.
  - Files: `PlayDisc.cs`, `LibraryEndpointsTests.PlayDisc.cs`.
  - Tests: `LibraryEndpointsTests.PlayDisc`: tgcd cue set → 200 stored ZIP with the rewritten cue; single `.chd` → 200 streamed as-is; single `.bin` → 409 `InvalidPlayDisc` whose `title` is B4's exact text (as `PlayDisc_ALoneNrg_NamesTheSingleFileShapes` asserts on `title`); two cues → 409 `InvalidPlayDisc`; psx, psp, saturn, segacd and 3do cases unchanged. `PlayDiscCopyTests`.
  - Done when: those pass.
- [ ] **5. TurboGrafx-CD player** (B7–B10): first check B10's two EmulatorJS assumptions and record them in § Decisions (stop and ask if either fails). Then:
  - `params.js`: `turboGrafxCdCards`, `turboGrafxCdBiosNames`, `turboGrafxCdBiosMaxBytes`, `turboGrafxCdBiosSizeError(name)`, `turboGrafxCdBiosMissing`, and the `contentFileName` guard.
  - `play.js`: `loadOptionalBios`'s `exact` (B8), `isTurboGrafxCd`, the B7 order and `EJS_externalFiles`/`EJS_hideSettings` for that path only.
  - Nothing produces a TurboGrafx-CD play link yet, since `BrowserCore` is null.
  - Files: `params.js`, `play.js`, `emulatorParams.test.ts`, `emulatorPlay.test.ts`.
  - Tests:
    - `emulatorParams.test.ts`: a new `describe('TurboGrafx-CD (B7, B9)')`: `contentFileName` renames each of the six names in any case for `mednafen_pce` with and without `disc`, and leaves other names alone; the size and missing messages.
    - `emulatorPlay.test.ts`: a new `play page TurboGrafx-CD`:
      - only `syscard3.pce` present: requests are `syscard3.pce`, `gexpress.pce`, then play-disc; `EJS_externalFiles` has `/syscard3.pce` only;
      - only `syscard2u.pce` present: requests are `syscard3.pce`, `syscard3u.pce`, `syscard2.pce`, `syscard2u.pce` (stopping there, no `syscard1.pce`), then `gexpress.pce`; its bytes are passed as `/syscard3.pce`;
      - `syscard3.pce` and `gexpress.pce` present: both external files;
      - a 262,656-byte card (256 KiB + 512) is accepted and passed whole;
      - no card: `turboGrafxCdBiosMissing`, and no `gexpress.pce`, disc or loader request;
      - an empty card, or one of 1 MiB + 1 bytes: `turboGrafxCdBiosSizeError` with that name, no later request;
      - a 500 on a card: `optionalBiosError`;
      - `gexpress.pce` empty, or 1 MiB + 1 bytes, after a valid `syscard3.pce`: `turboGrafxCdBiosSizeError('gexpress.pce')`, and no play-disc request; a 500 on `gexpress.pce`: `optionalBiosError`;
      - `EJS_hideSettings` is `['pce_cdbios']`, `EJS_disableCue` is unset, `EJS_defaultOptions` has no `pce_cdbios`.
    - `emulatorPlay.test.ts`: a new `play page TurboGrafx-16` (no such case exists yet): `mednafen_pce` without `disc` makes no BIOS request, loads the ROM from `/download`, and sets no `EJS_hideSettings` and no `EJS_externalFiles`.
    - The existing PlayStation, Saturn, Sega CD and 3DO cases pass unchanged (they call `loadOptionalBios` with `exact` defaulted).
  - Done when: `npm run lint`, `npm run check` and those vitest files pass.
- [ ] **6. Enable TurboGrafx-CD Play** (B5): `TurboGrafxCdHandler.BrowserCore = "mednafen_pce"`; `mednafen_pce` in the API's and SPA's external and play-disc core sets; `PrimaryHasFiles`; the lone `.bin` rule in `playable` and `canPlay`; README Play cell → `` `mednafen_pce` ``.
  - Files: `TurboGrafxCdHandler.cs`, `GameListItem.cs`, `LibraryRepositories.cs`, `LibraryEndpoints.cs`, `emulator.ts`, `README.md` (cell only), and the tests below.
  - Tests:
    - `TurboGrafxCdHandlerTests` and `SupportedSystemsTableTests`.
    - `LibraryEndpointsTests`:
      - `GameDetail_BrowserCore_IsTheSystemHandlers` gains `tgcd` → `mednafen_pce`;
      - `Games_QuickPlay_IsThePrimaryReleaseWhenPlayable` gains `("tgcd", true, true, "mednafen_pce")` with `playDisc` true (the helper's default primary `x.gb` isn't a `.bin`, so the rule doesn't apply);
      - a new test `Games_QuickPlay_TurboGrafxCd_ALoneBinIsNotPlayable` (new): a `tgcd` primary with `file_name` and `external_file_name` `x.bin` and one inserted `rom_files` row (`x.cue`) is playable with `playDisc` true; the same primary with no `rom_files` rows has `playRomId` null; a lone `x.chd` is playable;
      - `("tg16", false, true, "mednafen_pce")` unchanged with `playDisc` false.
    - `GameQueryTests` (Data.Tests): `PrimaryHasFiles` is true for a primary with `rom_files` rows and false without.
    - `emulator.test.ts`: `canPlay` for `mednafen_pce`: external `.cue`-set main `.bin` with `files` → true; external `.chd` → true; external `.BIN` with `files` null or `[]` → false; an embedded `.pce` (TurboGrafx-16) → true.
    - `game-page.test.ts`: for a `tgcd` game with `browserCore: 'mednafen_pce'` and an external set release, the release's Play opens the modal and its iframe `src` has `core=mednafen_pce` and `&disc=1`; a lone `.bin` release shows no Play button.
  - Done when: those tests pass, and `npm run lint` and `npm run check` pass.
- [ ] **7. Icon-only footer buttons** (U1, U2): independent of the TurboGrafx-CD steps.
  - Files: `AppFooter.svelte`, `icons.ts`, `AppFooter.test.ts`, `stores.svelte.test.ts`.
  - Tests:
    - `AppFooter.test.ts`: the Shortcuts button is found by name `Shortcuts`, has title `Keyboard Shortcuts`, contains an `i.bi.bi-keyboard` and no text content or `.footer-label`; the About button is found by name `About`, has title `About`, contains `img[src="/favicon.svg"][alt=""]` and no `.footer-label`. The existing "keeps one unwrapped row whose labels are footer-label spans" case keeps its job-link `.footer-label` check and drops the About/Shortcuts loop (renamed "keeps one unwrapped row; only the job link has a footer-label"). "the Shortcuts button opens the shortcuts; its visible text names it, title stays (B26)" becomes "…; its aria-label names it, title stays", asserting `aria-label="Shortcuts"`. The hide-while-password-change and Admin-order cases pass unchanged.
    - `stores.svelte.test.ts`: `iconClass('keyboard')` is `bi bi-keyboard`.
    - `mvp.spec.ts` (runs in Finish): the `Shortcuts` and `About` role lookups pass unchanged.
  - Done when: `npm run lint`, `npm run check` and those vitest files pass.
- [ ] **8. Play dialog footer and loading size** (U3, U4): independent of the TurboGrafx-CD steps.
  - Files: `ConfirmModal.svelte`, `EmulatorModal.svelte`, `params.js`, `play.js`, `ConfirmModal.test.ts`, `EmulatorModal.test.ts`, `emulatorParams.test.ts`, `emulatorPlay.test.ts`, `tests/e2e/specs/rom-progress.ts`.
  - Tests:
    - `ConfirmModal.test.ts`: with a `footer` snippet, its content is inside `.modal-footer` in a `.me-auto` wrapper before the Close button (info mode) and before the Cancel button (with `onconfirm`); without one the footer holds only the Close/Cancel button (and the confirm button when `onconfirm` is set).
    - `EmulatorModal.test.ts`: **Save state to library**, **Save battery to library** (when offered) and **Screenshot** (Admin) are inside `.modal-footer`, and nothing but the frame (and the screenshot preview) is in `.modal-body`; the existing visibility and `disabled` cases pass with only their container lookups changed.
    - `emulatorParams.test.ts`: `contentSize` gives `512 B`, `1.5 KiB` (1536), `16.0 MiB`, `8.0 GiB`, matching `formatSize` for the same inputs (import both and compare over a list of sizes).
    - `emulatorPlay.test.ts`: the known-length progress cases expect `50% of 4 B` and `100% of 4 B` in place of `50%`/`100%`; the X-Content-Bytes and over-100% cases likewise; the unknown-length case expects `2 B` (and a 1,572,864-byte body ends at `1.5 MiB`) in place of the long byte count; the label before the first chunk is `0% of 4 B` with a total and `0 B` without.
    - `tests/e2e/specs/rom-progress.ts` (`expectRomDownloadProgress`, used by `mvp.spec.ts` at its three ROM-progress flows; runs in Finish): the expected texts become the regexes `/^0% of \d+(\.\d)? (B|KiB|MiB|GiB)$/`, and the same with `25%` and `75%` (no import from the web app, which the e2e package doesn't reach; `emulatorParams.test.ts` already pins `contentSize`'s exact output).
  - Done when: `npm run lint`, `npm run check` and those vitest files pass.
- [ ] **9. Docs**:
  - `README.md`: intro list of disc systems, the Play bullet (TurboGrafx-CD: one disc per launch, a cue with its tracks or a `.chd`, needs a System Card such as `syscard3.pce` in `data/bios/`), and the upgrade/Refresh DATs list. The Beetle PCE attribution line already says "No PC Engine CD System Card BIOS is included": keep it, adding "(TurboGrafx-16 / PC Engine and TurboGrafx-CD / PC Engine CD)" to the systems in its parentheses.
  - `docs/features.md`: TurboGrafx-CD ingest and play rules, the System Card order and renaming, the hidden `pce_cdbios` setting, Games Express discs, and the lone `.bin` rule. Fix the statements that become false: the TurboGrafx-16 Play bullet's "PC Engine CD and SuperGrafx aren't supported" becomes "SuperGrafx isn't supported; PC Engine CD discs are the TurboGrafx-CD system"; the `tg16` ingest paragraph's "SuperGrafx, TurboGrafx-CD / PC Engine CD and bit-reversed (undecrypted) US dumps aren't supported" drops TurboGrafx-CD / PC Engine CD and points to `ingest/tgcd/`; the drop-files paragraph gains TurboGrafx-CD discs (a `.cue` with its `.bin` tracks, or a `.chd`) into `ingest/tgcd/`, and its size list gains "16 bytes–1 GiB for TurboGrafx-CD".
  - `docs/deployment.md`: the DAT refresh after upgrade and the BIOS files.
  - `docs/zip-format.md`: `tgcd` in the layout and `system` list (no `header`).
  - `docs/adding-a-system.md`: TurboGrafx-CD in the disc-systems text, the `BrowserCore` row (`tgcd` returns `mednafen_pce`, shared with `tg16`; the lone `.bin` rule), and the box-art list.
  - `docs/development.md` (Browser emulators): the TurboGrafx-CD launch path, `loadOptionalBios`'s `exact`, and `mednafen_pce` in the external and play-disc core lists.
  - Tests: `SupportedSystemsTableTests`.
  - U1–U4 in `docs/features.md`: the Footer bullet's **About** and **Shortcuts** become icon-only (About: the app logo; Shortcuts: a keyboard icon, tooltip "Keyboard Shortcuts"; drop "a help icon with the text "Shortcuts""); the Play bullet's progress sentence gives the new label ("50% of 512.0 MiB", or the size received so far); the **Screenshot** bullet's "the Play dialog's toolbar has a **Screenshot** button after the save buttons" and the **Play dialog** saves bullet place those buttons in the dialog's footer, left of **Close**. `docs/development.md`: `AppFooter`'s icon-only buttons, `ConfirmModal`'s `footer` snippet, and `readContent`'s label with `contentSize`.
  - Done when: every file above has the stated content and the README row reads `| TurboGrafx-CD / PC Engine CD | `tgcd` | Next to ZIP | `.bin`, `.cue`, `.chd` | Redump | `mednafen_pce` |`.

## Decisions
- 2026-11-20 Build approach → through this spec, like Sega CD and 3DO (user)
- 2026-11-20 Name → `TurboGrafx-CD / PC Engine CD`, short `TGCD`, id `tgcd`, mirroring `tg16` (user)
- 2026-11-20 Which System Cards → any of `syscard3`, `syscard3u`, `syscard2`, `syscard2u`, `syscard1.pce` (first found), plus optional `gexpress.pce` (user)
- 2026-11-20 How the card reaches the core → passed as `/syscard3.pce` (the default option's file), `pce_cdbios` hidden, no core option set, since a stored per-game option would outlive a card swap (user)
- 2026-11-20 A lone `.bin` release → not playable (per-core rule in quick play and `canPlay`), and play-disc refuses it (user)
- 2026-11-20 Formats → `.bin`/`.cue` sets and `.chd`; CloneCD out of scope (user)
- 2026-11-20 Beetle PCE facts → read from beetle-pce-libretro at `e1ac90d` (the vendored binary's commit): `pce_cdbios` values map to `syscard3.pce`, `syscard3u.pce`, `syscard2.pce`, `syscard2u.pce`, `syscard1.pce`, `gexpress.pce` and default to `System Card 3`; the option is read only at load; the card loads at any size with a 512-byte header stripped; nothing decodes US cards differently; Games Express discs are detected and use `gexpress.pce`; only `.cue`/`.ccd`/`.chd`/`.toc`/`.m3u` load as a CD; a missing card makes `retro_load_game` fail (code)
- 2026-11-20 EmulatorJS 4.2.3 stores core options per game (`getLocalStorageKey`: game id, core group, game name) and a stored value wins over `EJS_defaultOptions` (code)
- 2026-11-20 System Card required even with only `gexpress.pce` → yes; the app can't detect a Games Express disc (code)
- 2026-11-20 Seeded games and e2e → empty catalog in Basic/Full, no e2e play case, as for Sega CD (code)
- 2026-11-20 Which track the DAT lists → one `rom` per game, usually `(Track 02)` (the data track; Track 01 is usually an audio track), no `.cue`; no cross-game SHA-1 repeats seen, so the existing one-listed-member set rule names sets correctly (code: libretro `metadat/redump` DAT read 2026-11-20)
- 2026-11-20 Review round 1 → manifests chosen by `ThreeDsDat` (adds `IngestPipelineTests`); `InvalidPlayDisc` text is the `title`; `ShapeProblem`/`OpenDiscAsync` take the system id; `canPlay`'s `files` optional; `PrimaryHasFiles` row property and mapping named; a new HuCard play-page test, quick-play test with `rom_files`, and `gexpress.pce` error cases; `.pce` in `ingest/tgcd/` is `UnsupportedType`; docs statements that become false listed; RetroArch note pinned (reviewer)
- 2026-11-20 Add the footer and Play dialog changes to this spec → yes, as rules U1–U4 and Steps 7–8, independent of the TurboGrafx-CD steps (user)
- 2026-11-20 About icon → the app logo (`favicon.svg`); Shortcuts → `bi-keyboard`; both keep their accessible names via `aria-label` (user; names: code, so `mvp.spec.ts` lookups stay)
- 2026-11-20 Which Play dialog buttons move → all three (state, battery, screenshot), to the footer's left (user)
- 2026-11-20 Loading label → `50% of 512.0 MiB`; with no total, the size received so far (user)
- 2026-11-20 U review → `rom-progress.ts` e2e helper updated (regex, no cross-package import); the starting label pinned; Close/Cancel wording (reviewer)
- 2026-11-20 B10 IGDB id → 150 kept but unconfirmed: IGDB's public pages refuse automated reads (403) and no IGDB credentials were used without the user's say-so; the maintainer checks it (code)
- 2026-11-20 Review round 2 → `HandleAsync`/`HandleSetAsync` pass the system id through `OpenDiscAsync` to `ShapeProblem`; round 3 clean (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-20-turbografx-cd-support.md
```
Starts the spec, or resumes it at the first unticked step.
