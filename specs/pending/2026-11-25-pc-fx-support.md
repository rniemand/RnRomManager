---
title: PC-FX support
status: ready
created: 2026-11-25
---

# PC-FX support

## Summary
Adds the NEC PC-FX (`pcfx`) as a disc system in `RnRomManager.Systems.Nec`. Its Redump discs (cue/bin, CloneCD, MDS, TOC sets and standalone `.chd`) are imported against libretro's Redump DAT and stored next to the game's ZIP exactly as TurboGrafx-CD discs are. Releases play in the browser on Beetle PC-FX (`mednafen_pcfx`), newly vendored from the EmulatorJS 4.2.3 packages, through play-disc, with the `pcfx.rom` BIOS the operator supplies in `Paths:Bios`.

## Out of scope
- Multi-disc sets (`set=1` stays refused for `mednafen_pcfx`; Beetle PC-FX's compiled list has no `m3u`). Each disc plays on its own; discs are still grouped by title.
- `.nrg`, `.iso` and `.gdi` (not in TurboGrafx-CD's list either).
- A header reader: `ReadHeader` returns null, as for TurboGrafx-CD.
- Bundling any PC-FX BIOS, or the PC-FXGA.
- Seeded PC-FX games in the Basic/Full sets and an e2e play case (a launch needs the BIOS; vitest and the API tests cover the player, as for TurboGrafx-CD).
- Real-game boot checks (BIOS found, CD audio, backup-RAM saves): the maintainer does them after merge, as for previous browser-play specs.

## Behaviour
- **X1 Handler.** `PcfxHandler` (new) in `src/RnRomManager.Systems.Nec/PcfxHandler.cs`, built like `TurboGrafxCdHandler`:

  | Member | Value |
  |---|---|
  | `Id` / `Family` | `pcfx` / `pcfx` |
  | `DisplayName` / `ShortName` | `PC-FX` / `PC-FX` |
  | `RomExtensions` | `.bin`, `.cue`, `.chd`, `.ccd`, `.img`, `.sub`, `.mds`, `.mdf`, `.toc` (`[0]` = `.bin`; the same list and order as `TurboGrafxCdHandler`) |
  | `MinRomSizeBytes` / `MaxRomSizeBytes` | 16 / 1 GiB (the DAT's largest track is 773,622,192 bytes) |
  | `DatName` | `NEC - PC-FX` (libretro `metadat/redump/NEC - PC-FX.dat` and the libretro-thumbnails folder `NEC - PC-FX`; both checked 2026-11-25) |
  | `DatFolder` / `MetadatKinds` | `DatKind.Redump` / `[]` |
  | `EmbedRomsInZip` / `FileSets` | false / true |
  | `BrowserCore` | null until Step 5, then `mednafen_pcfx` (no Play link appears before Step 6 adds the core to the external core sets, since every PC-FX release is external) |
  | `ReadHeader` | null always |
  | `DefaultBoxArtPng` | a new 512×512 placeholder drawn for this project (`pcfx.svg` + `pcfx.png` in `Resources/boxart/`, named in that folder's `README.md`), loaded with `TurboGrafx16Handler.LoadResource("boxart.pcfx.png")` as `TurboGrafxCdHandler` does |

  Its own family keeps it apart from `tgcd`: a disc in `ingest/tgcd/` is never tried against the PC-FX DAT, and the other way round.
- **X2 Registration.** `services.AddSystemHandler<PcfxHandler>();` after `AddSystemHandler<FbneoHandler>()` in `AppHost.AddSystemHandlers`, so registry order ends `…, "mame2003", "fbneo", "pcfx"`. The Nec project is already referenced and in the Dockerfile restore layer. Startup creates `ingest/pcfx/` with its `_*` folders; `roms/pcfx/` is created by the first PC-FX release written. `/api/systems` (`DisplayName`, `OrdinalIgnoreCase`) lists `PC-FX` between `Nintendo Switch` and `PlayStation`. `/api/admin/dats` (`DatName`, `Ordinal`) lists `NEC - PC-FX` right after `NEC - PC Engine CD - TurboGrafx-CD` (space < `-`) and before `Nintendo - …`. A DAT refresh fetches one more file (Redump, no metadat); ingestion refuses with `DatsMissing` until **Admin → DATs → Refresh DATs**.
- **X3 Colour, icon, catalogs.**
  - `app.css`, a block directly after the `tgcd` block, laid out like it: `/* PC-FX system colour: plum, same in both themes; white on it is AA (~9.6:1). */`, `:root { --bs-pcfx: #7a1f5c; }`, `.text-bg-pcfx { color: #fff !important; background-color: var(--bs-pcfx) !important; }`. No other token uses `#7a1f5c`.
  - `systemColours.ts`: the union gains `'pcfx'` after `'tgcd'`; the map gains `pcfx: { name: 'pcfx', foreground: '#fff' }` after `tgcd`.
  - `icons.ts`: `'system-pcfx': 'bi bi-disc'` after `'system-tgcd'`.
  - `IgdbPlatforms.ById`: `["pcfx"] = 274` after `["tgcd"] = 150` (IGDB "PC-FX"; re-check the id in Step 2 as the TurboGrafx-CD spec's B10 did, and record it in § Decisions).
  - `systemEmulators.json`: key `pcfx` after `tgcd` with two entries copied from `tgcd`'s: Mednafen (`https://mednafen.github.io/releases/`, `["windows"]`) and RetroArch (`https://www.retroarch.com/?page=platforms`, the same five OSes) with `"note": "RetroArch core: Beetle PC-FX"`. No ares entry (ares has no PC-FX).
- **X4 Ingest.** Nothing new in Core: the `FileSets` rules apply as for TurboGrafx-CD. The DAT lists one `rom` per game, the data track (`(Track 2)`, `(Track 02)`, `(Track 3)`…), and repeats a game per serial with the same SHA-1, which `DatStore` already handles (first wins). A cue/TOC/CloneCD/MDS set is one release whose main file is the matched track; files keep their exact names next to the ZIP; a taken name is `NameTaken`; an unmatched `.bin` or a `.chd` goes to Review as `NoDatMatch`. Set-member Review items follow the existing `ReviewResolver` rules.
- **X5 play-disc.** `PlayDisc` gains `public const string PcfxSystemId = "pcfx"` (new), added to `SupportedSystems`. The shared 2 GiB `MaxBytes` cap applies. In `ShapeProblem`, the single-member TurboGrafx-CD branch becomes `members.Count == 1 && systemId is TurboGrafxCdSystemId or PcfxSystemId`, returning null for a `.chd` (`OrdinalIgnoreCase`) and otherwise `"a TurboGrafx-CD disc must be a .cue with its .bin tracks, or a .chd"` for `tgcd` (unchanged) or `"a PC-FX disc must be a .cue with its .bin tracks, or a .chd"` for `pcfx`. So a lone `.bin` is 409 `InvalidPlayDisc` with `title` `The disc can't be played: a PC-FX disc must be a .cue with its .bin tracks, or a .chd.` (`detail` null). Cue, TOC, CloneCD and MDS sets follow the existing multi-member rules (the cue is generated from the `.toc`/`.ccd`/`.mds`, which is never sent). `FirmwareNames` gains no entry, so `play-disc-set` refuses `pcfx` as it does `tgcd`. Beetle PC-FX opens only `cue|ccd|toc|chd` (its compiled-in `valid_extensions`, read 2026-11-25).
- **X6 Play eligibility.** `mednafen_pcfx` joins `LibraryEndpoints.ExternalBrowserCores` and `PlayDiscCores`, and `externalBrowserCores` and `playDiscCores` in `emulator.ts` (appended last in each). The **lone `.bin` rule** extends from `mednafen_pce` to `mednafen_pcfx`:
  - `ToListItemDto`'s `playable`: `core == "mednafen_pce"` becomes `core is "mednafen_pce" or "mednafen_pcfx"`; the comment gains "and Beetle PC-FX can't boot one".
  - `canPlay` in `emulator.ts`: `game.browserCore === 'mednafen_pce'` becomes `(game.browserCore === 'mednafen_pce' || game.browserCore === 'mednafen_pcfx')`; its doc comment names PC-FX X6.
  - `usesPlayDisc` then adds `&disc=1` for every external PC-FX release, and `play.js` loads it from play-disc (`params.disc`); no `play.js` change.
- **X7 Vendored core.** `@emulatorjs/core-mednafen_pcfx` `4.2.3`, exact version, in `package.json` (`npm install --save-exact`). `vendor-emulatorjs.mjs` adds `'mednafen_pcfx'` to `cores` after `'mednafen_ngp'` (non-threaded payloads and report only); no `edits` change (EmulatorJS 4.2.3's `requiresThreads` is only `ppsspp` and `dosbox_pure`; its `pcfx` group holds only `mednafen_pcfx`). `params.js` adds `'mednafen_pcfx'` to the allowlist after `'mednafen_ngp'`; not to `hidesCoreChooser`, `controlSchemeFor` (the default `pcfx` group is right) or `isolatedPlayers`. `EJS_disableCue` stays unset (the core isn't on EmulatorJS's disable list, and play-disc always sends a cue for a set). Battery saves use the generic flow (`core.json` `save: "srm"`). Pinned bytes (npm archive `core-mednafen_pcfx-4.2.3.tgz`, checked 2026-11-25):

  | File in npm package | Bytes | SHA-256 |
  |---|---:|---|
  | `mednafen_pcfx-wasm.data` | 953,008 | `7a49a92992d463afc1f414dc5f3eff99613ae9340fbf137a10b6df0ac890f29e` |
  | `mednafen_pcfx-legacy-wasm.data` | 951,926 | `77c01ee45a7c694f8397009636182b2a8b7557b0353c2ea7bade64ab4088ba3a` |
  | `reports/mednafen_pcfx.json` | 127 | `43404cd63ee017b7a459b1b495af156a6688db74d2285709652c4c8cbdc326d0` |

  Each payload holds `mednafen_pcfx_libretro.js`, `.wasm`, `build.json` (version `2.0.2`), `core.json` (extensions `cue, ccd, toc, chd`, `save: "srm"`) and `license.txt` (GPL v2, 341 lines). The `.wasm`'s compiled-in `valid_extensions` is `cue|ccd|toc|chd`.
- **X8 BIOS.** `biosFor('mednafen_pcfx', <any name>)` returns `'pcfx.rom'` (new first line of `biosFor`, whatever the release name). The play page's existing path then runs unchanged: `checkBios('pcfx.rom')` before any download, and on 404 it stops with `biosError`'s `This system needs its BIOS: ask an Admin to put pcfx.rom in the BIOS folder (Paths:Bios).`; then `EJS_biosUrl = /api/emulator/bios/pcfx.rom`. EmulatorJS 4.2.3 writes that file to `/pcfx.rom` (`downloadGameFile`, the content root), and Beetle PC-FX opens `retro_base_directory + "/" + MDFN_GetSettingS("pcfx.bios")` (`libretro.cpp` `LoadCommon` at `b517023`), the same root the TurboGrafx-CD spec verified for Beetle PCE. No size check (the existing `biosFor` cores have none). No `contentFileName` guard: no PC-FX content can be named `pcfx.rom` (no `.rom` extension is accepted).
- **X9 Assumption checked in Step 5.** Before writing `biosFor`, confirm from beetle-pcfx-libretro at `b5170235dd6c93a3ad3827f142036f2c27e354bd` that the `pcfx.bios` setting defaults to `pcfx.rom` (its settings table; the `.wasm` holds both strings). If it doesn't, stop and ask the user. Record the result in § Decisions.
- **X10 Licences.** Facts checked 2026-11-25:
  - **Source:** `https://github.com/EmulatorJS/beetle-pcfx-libretro/tree/b5170235dd6c93a3ad3827f142036f2c27e354bd` (2024-12-10; version string `v0.9.36.5 b517023` in the `.wasm`).
  - **Components compiled in** (`Makefile` sets `HAVE_CHD = 1` and `NEED_TREMOR = 1`; `Makefile.common` at that commit): Mednafen PC-FX code under `mednafen/`, `deps/libchdr`, `deps/lzma-19.00`, `deps/zlib-1.2.11`, Tremor, and `libretro-common`. Step 5 reads each component's licence at that commit and writes them like Beetle PCE's.
  - **Files** in `emulator-licenses/`, laid out like the `mednafen_pce-*` set: `mednafen_pcfx-LICENSE.txt` (the payload's `license.txt`, byte for byte), `mednafen_pcfx-NOTICE.txt` (as `mednafen_pce-NOTICE.txt`: source commit, build flags, copyright holders, licence, each component with its terms), `mednafen_pcfx-libchdr-LICENSE.txt`, `mednafen_pcfx-lzma-LICENSE.txt`, `mednafen_pcfx-tremor-LICENSE.txt` (copies of the source's files at that commit), and `mednafen_pcfx-libretro-common-LICENSE.txt` (as `mednafen_ngp-libretro-common-LICENSE.txt`). zlib's terms are quoted in the NOTICE if the source has no separate licence file, as Beetle PCE's NOTICE does.
  - **Provenance:** report `buildStart` 2025-06-14 and `build.json` `2.0.2`, the same build as the other 4.2.3 cores: `COMMON-BUILD.txt`'s package list gains `@emulatorjs/core-mednafen_pcfx` after `@emulatorjs/core-mednafen_ngp`, and its "for … Beetle PCE and Beetle NeoPop only" list gains Beetle PC-FX.
  - **Licence expression** (NOTICE, `licenses.json`, README): the one Beetle PCE's `licenses.json` entry uses, adjusted for the components Step 5 finds. `src/RnRomManager.Api/licenses.json` gains `{ "name": "@emulatorjs/core-mednafen_pcfx", "license": "<that expression>", "url": "https://github.com/EmulatorJS/beetle-pcfx-libretro" }` after the `@emulatorjs/core-mednafen_ngp` entry.
- **X11 README.** The supported-systems row (generated order, `SupportedSystemsTableTests`) between `Nintendo Switch` and `PlayStation`: `| PC-FX | `pcfx` | Next to ZIP | `.bin`, `.cue`, `.chd`, `.ccd`, `.img`, `.sub`, `.mds`, `.mdf`, `.toc` | Redump | — |`, its Play cell `` `mednafen_pcfx` `` from Step 5. Step 7 adds PC-FX to the intro's disc-system list, the Play list (one disc per launch, needs `pcfx.rom` in `data/bios/`), the upgrade/Refresh DATs note, and the attribution: Beetle PC-FX in both EmulatorJS core lists, with a sub-bullet after Beetle NeoPop's, `[Beetle PC-FX `b517023`](https://github.com/EmulatorJS/beetle-pcfx-libretro/tree/b5170235dd6c93a3ad3827f142036f2c27e354bd) (PC-FX): <X10's expression>`, its components in Beetle PCE's style, and "No PC-FX BIOS is included."

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | `src/RnRomManager.Systems.Nec/PcfxHandler.cs` (new), `Resources/boxart/pcfx.svg` + `pcfx.png` (new), `Resources/boxart/README.md` | X1. The `.csproj` already globs `Resources/boxart/*.png`. |
| Api | `src/RnRomManager.Api/AppHost.cs` | X2 |
| Core | `src/RnRomManager.Core/Media/IgdbPlatforms.cs` | X3 |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | X5 |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | X6 (core sets, lone `.bin` rule) |
| Api | `src/RnRomManager.Api/licenses.json` | X10 |
| Web | `src/app.css`, `src/lib/systemColours.ts`, `src/lib/components/icons.ts`, `src/lib/systemEmulators.json` | X3 |
| Web | `src/lib/emulator.ts` | X6 |
| Web | `package.json`, `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `static/emulator/params.js`, `emulator-licenses/mednafen_pcfx-*` (new), `emulator-licenses/COMMON-BUILD.txt` | X7, X8, X10 |
| Test data | `tests/RnRomManager.TestData/IngestSeeder.cs` | `PcfxDat = "NEC - PC-FX"` (new) after `TurboGrafxCdDat`, in `RedumpLayouts`; `pcfx` in the disc-folder loop; `[PcfxDat] = []` in the `FixtureDatWriter.Write` call |
| Tests (new) | `tests/RnRomManager.Systems.Nec.Tests/PcfxHandlerTests.cs` | X1 |
| Tests | see § Steps | |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/development.md`, `docs/adding-a-system.md` | Step 7 |

No migration (`system_id` is a `VARCHAR`). No new config key. API: additive (`/api/systems` and `/api/admin/dats` gain a row; `browserCore`/`playRomId`/`playDisc` are set for PC-FX games). Existing data is untouched.

## Risks
- **Every full-registry DAT manifest** must list `IngestSeeder.PcfxDat` once the handler is registered, or ingestion fails `DatsMissing`; Step 2 updates them all in one commit.
- **BIOS location** is read from source, not run (X8, X9); the maintainer's post-merge boot check confirms it.
- **Lone `.bin` rule now spans two cores.** The existing `tgcd` cases must pass unchanged; Step 6 adds `pcfx` cases beside them.
- **Image size.** About 1.9 MB more vendored output (two payloads).
- **Licences.** A new GPL core ships in the image; X10's files, `licenses.json` and the README attribution are required before Play is enabled (Step 5 precedes Step 6).

## Steps
Steps 2, 3, 4 and 6 run integration tests (category `Integration`), which need Docker. Playwright (`mvp.spec.ts`) runs only in Finish and needs no change.

- [ ] **1. PC-FX handler** (X1): `PcfxHandler` with `BrowserCore` null and its box art. `IngestSeeder.PcfxDat` added to `RedumpLayouts`; the seeder doesn't write it yet. Not registered.
  - Files: `PcfxHandler.cs`, `Resources/boxart/pcfx.svg`, `pcfx.png`, `Resources/boxart/README.md`, `tests/RnRomManager.Systems.Nec.Tests/PcfxHandlerTests.cs` (new), `IngestSeeder.cs`.
  - Tests: `PcfxHandlerTests` (new), copying `TurboGrafxCdHandlerTests`: every X1 value, `ReadHeader` null for any bytes, `NormalizeForHashing` unchanged, PNG valid and 512×512 and different from `tgcd.png`; with a registry holding only this handler, `DatCache` loads a Redump fixture catalog with one synthetic cue/bin game and finds its `.bin` by SHA-1, and loads an empty catalog.
  - Done when: those tests pass and the solution builds with warnings as errors.
- [ ] **2. Register PC-FX and update fixtures** (X2, X3, X11 row with Play `—`):
  - `AppHost.cs` line; `IgdbPlatforms.cs` (after checking the id); `systemEmulators.json`; `app.css`; `systemColours.ts`; `icons.ts`; the README row.
  - `IngestSeeder`: `pcfx` appended to the disc-folder loop (`…, "tgcd", "pcfx"`), `[PcfxDat] = []` after `[TurboGrafxCdDat] = []` in the `FixtureDatWriter.Write` dictionary.
  - Every test file listing `IngestSeeder.TurboGrafxCdDat` in a full-registry manifest adds `[IngestSeeder.PcfxDat] = []` after it: today `AuditLibraryEditTests`, `DatEndpointsTests`, `GamePlayedEndpointTests`, `ImageEndpointsTests`, `ImageUrlEndpointsTests`, `IngestArchiveTests`, `IngestDeleteEndpointsTests`, `IngestEndpointsTests`, `IngestPipelineTests`, `IngestRestoreEndpointsTests`, `IngestReviewTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `NoteEndpointsTests`, `ReportEndpointsTests`, `ReviewEndpointsTests`, `ZipEntryEndpointsTests`. `TurboGrafxCdHandlerTests` (one-entry manifest) stays. Before committing, grep `tests/` and check every hit of `IngestSeeder.TurboGrafxCdDat` in a full manifest also lists `PcfxDat`.
  - Counts and orders:
    - `DatEndpointsTests`: names 42 → 43, message "29 libretro systems, the 10 disc systems, Switch, the test titledb system, MAME and FBNeo"; the ordinal list gains `IngestSeeder.PcfxDat` right after `IngestSeeder.TurboGrafxCdDat`; refresh requests 140 → 141 (message: "…, 1 for Commodore 64, Commodore PET, Commodore VIC-20, Commodore Amiga and each disc system…" unchanged).
    - `IngestSeederTests`: `.dat` files 146 → 147.
    - `SystemStartupTests`: both id lists end `"mame2003", "fbneo", "pcfx"`; `IngestEndpointsTests.Pending_ListsEverySystem_IgnoringJunk` likewise.
    - `LibraryEndpointsTests`: the display-name-ordered id list gains `"pcfx"` between `"switch"` and `"psx"`; the `displayName` list gains `"PC-FX"` between `"Nintendo Switch"` and `"PlayStation"`; a new assert after the `tgcd` one, in its form: `Find("pcfx")` → `("PC-FX", "PC-FX")`; `embedRoms` true count stays 28.
    - `systemColours.test.ts`: a new case after the TurboGrafx-CD one, `it('gives PC-FX plum with white text', …)`: `systemColour('pcfx')` → `{ name: 'pcfx', foreground: '#fff' }`. `stores.svelte.test.ts`: `iconClass('system-pcfx')` is `bi bi-disc`.
  - Tests: the files above, `SeedIngestTests` (counts unchanged), `SupportedSystemsTableTests`, `IgdbPlatformsTests`, `SystemEmulatorsCatalogTests`; web `npm run lint`, `npm run check`.
  - Done when: those pass (Docker running) and `docker build .` succeeds.
- [ ] **3. Verify PC-FX ingest** (X4): Testcontainers cases in `IngestPipelineTests` with synthetic files (never real images), after the `TurboGrafxCd_*` tests. The class's manifest replaces step 2's `[IngestSeeder.PcfxDat] = []` with one fixture game whose single `rom` is `(Track 2).bin`, as the real DAT lists it:
  - `Pcfx_DatMatchedCueWithTwoBins_IsOneRelease_NamedByItsDataTrack` (new, copying `TurboGrafxCd_DatMatchedCueWithThreeBins_IsOneRelease_NamedByItsDataTrack`): a cue + 2 bins (`(Track 1)` audio, `(Track 2)` data) where the DAT lists only Track 2 → one release named after that entry, main file Track 2, files next to the ZIP under their exact names, info.json `files` lists the cue and Track 1, `SystemId` `pcfx`;
  - `Chd_Lone_GoesToReviewAsNoDatMatch_WithItsFoldersSystem` gains `[TestCase("pcfx")]` after `tgcd`'s;
  - `Pcfx_PceInItsFolder_IsUnsupportedType_AndMovedToFailed` (new, copying `TurboGrafxCd_HuCardInItsFolder_IsUnsupportedType_AndMovedToFailed`): a `.pce` in `ingest/pcfx/` → `(Failed, UnsupportedType)`, moved to `ingest/pcfx/_failed/` with its `.error.txt`;
  - `Pcfx_DatMatchedCueSetInTgcdFolder_GoesToReviewAsThreeNoDatMatchItems` (new): the same cue set dropped in `ingest/tgcd/` → three items (the cue and both `.bin`s), each `(Review, "NoDatMatch")` with `SystemId` `tgcd`, asserted with `RunIngestJobWithItemsAsync` as `CloneCd_UnmatchedSaturnSet_GoesToReviewAsThreeNoDatMatchItems` does (families stay apart).
  - Done when: `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~IngestPipelineTests"` passes.
- [ ] **4. PC-FX play-disc** (X5): `BrowserCore` stays null.
  - Files: `PlayDisc.cs`, `LibraryEndpointsTests.PlayDisc.cs`.
  - Tests (`LibraryEndpointsTests.PlayDisc`): a `PcfxGame` helper (new) beside `TurboGrafxCdGame`, `DiscGame(PlayDisc.PcfxSystemId, …)`; a pcfx cue set → 200 stored ZIP with the playback cue (copy `PlayDisc_ATurboGrafxCdCueSet_IsAStoredZip_WithThePlaybackCue`); the single-`.chd` `[TestCase]`s at line ~633 gain `("pcfx", "Chd Solo (Japan).chd")`; `PlayDisc_APcfxSingleFileThatIsNotAChd_Is409InvalidPlayDisc` (new, `x.bin` and `x.iso`) asserts X5's exact `title`; the existing `[TestCase("tgcd")]` at line ~1018 gains `[TestCase("pcfx")]`; a CloneCD disc plays (copy `PlayDisc_ATurboGrafxCdCloneCdDisc_Plays`); every tgcd case passes unchanged.
  - Done when: `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~LibraryEndpointsTests|FullyQualifiedName~PlayDiscCopyTests"` passes.
- [ ] **5. Vendor Beetle PC-FX, its BIOS and `BrowserCore`** (X7, X8, X9, X10, X1 `BrowserCore`, X11 Play cell): check X9 first. Then `npm install --save-exact @emulatorjs/core-mednafen_pcfx@4.2.3`, the `cores` and `params.js` allowlist entries, `biosFor`, the licence files, `COMMON-BUILD.txt`, `licenses.json`, the README attribution (X11's last sentence) and Play cell → `` `mednafen_pcfx` ``, and `PcfxHandler.BrowserCore => "mednafen_pcfx"` (`BrowserCoreExtensionsTests` needs a system using every recorded core). Nothing produces a PC-FX play link yet: every release is external and the core isn't in the external core sets until Step 6.
  - Files: `package.json`, `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `static/emulator/params.js`, `emulator-licenses/*`, `src/RnRomManager.Api/licenses.json`, `README.md` (attribution and Play cell), `PcfxHandler.cs`, and the tests below.
  - Tests:
    - `PcfxHandlerTests` (`BrowserCore` is `mednafen_pcfx`), `SupportedSystemsTableTests`.
    - `vendorEmulatorjs.test.ts`: `coreLicenses` (compared with the sorted `emulator-licenses` listing, so ordinal order) gains, directly after `'mednafen_pce-tremor-LICENSE.txt'`: `'mednafen_pcfx-LICENSE.txt'`, `'mednafen_pcfx-NOTICE.txt'`, `'mednafen_pcfx-libchdr-LICENSE.txt'`, `'mednafen_pcfx-libretro-common-LICENSE.txt'`, `'mednafen_pcfx-lzma-LICENSE.txt'`, `'mednafen_pcfx-tremor-LICENSE.txt'`; the "writes exactly the B9 files" core list gains `'mednafen_pcfx'` after `'mednafen_ngp'`; a new case "vendors exactly the pinned Beetle PC-FX files and no threaded payload (PC-FX)" after the Beetle NeoPop case, copying it with X7's sizes and SHA-256s.
    - `emulatorParams.test.ts`: `accepts the %s core` gains `'mednafen_pcfx'`; the `hidesCoreChooser` "keeps it for %s" list gains it; `it('keeps the default for mednafen_pcfx (PC-FX)', …)`: `controlSchemeFor('mednafen_pcfx', 'X (Japan).cue')` is null; `biosFor('mednafen_pcfx', name)` is `'pcfx.rom'` for `X (Japan).cue`, `X (Japan).chd` and `X (Japan) (Track 2).bin`, and `biosFor('mednafen_pce', 'X (Japan).cue')` stays null.
    - `emulatorPlay.test.ts`: a new `describe('play page PC-FX')`: with `core=mednafen_pcfx&disc=1`, the requests are `/api/emulator/bios/pcfx.rom` then play-disc; `EJS_biosUrl` is `/api/emulator/bios/pcfx.rom`; `EJS_externalFiles` and `EJS_disableCue` are unset; a 404 on the BIOS shows X8's message and makes no play-disc or loader request.
    - `BrowserCoreExtensionsTests`: `CoreExtensions["mednafen_pcfx"] = "cue|ccd|toc|chd"` in the "Vendored" block after `mednafen_pce`; `Exceptions` gains, after the `tgcd` entry of each group, `[("pcfx", ".bin")]` with `tgcd`'s reason, `[("pcfx", ".img")]` and `[("pcfx", ".sub")] = CloneCdMember`, `[("pcfx", ".mds")]` and `[("pcfx", ".mdf")] = MdsTocMember`; the stale-exception expectation list (line ~240) gains the matching `"pcfx .<ext>: stale exception (no such system)"` lines in the same order.
    - `LicenseInfoTests`.
  - Done when: `npm run lint`, `npm run check`, `npx vitest run src/lib/vendorEmulatorjs.test.ts src/lib/emulatorParams.test.ts src/lib/emulatorPlay.test.ts` and `npm run build` pass; `BrowserCoreExtensionsTests`, `LicenseInfoTests`, `SupportedSystemsTableTests` and `dotnet test tests/RnRomManager.Systems.Nec.Tests` pass; the vendored output holds both payloads, `reports/mednafen_pcfx.json` and the six licence files, and no `mednafen_pcfx-thread-*` file.
- [ ] **6. Enable PC-FX Play** (X6): the API's and SPA's external and play-disc core sets; the lone `.bin` rule for both cores.
  - Files: `LibraryEndpoints.cs`, `emulator.ts`, and the tests below.
  - Tests:
    - `LibraryEndpointsTests`: `GameDetail_BrowserCore_IsTheSystemHandlers` gains `[TestCase("pcfx", "mednafen_pcfx")]` after `tgcd`'s; `Games_QuickPlay_IsThePrimaryReleaseWhenPlayable` gains `[TestCase("pcfx", true, true, "mednafen_pcfx")]` after `tgcd`'s; `Games_QuickPlay_TurboGrafxCd_ALoneBinIsNotPlayable` becomes `Games_QuickPlay_DiscOnlyCores_ALoneBinIsNotPlayable(string system, string core, string fileName, string? setFile, bool playable)`, its four cases run for both `("tgcd", "mednafen_pce")` and `("pcfx", "mednafen_pcfx")`.
    - `emulator.test.ts`: `describe('canPlay on mednafen_pcfx (PC-FX X6)')` copying the `mednafen_pce` block's external cases (set `.bin` with `files` → true; `.chd` → true; `.BIN` with `files` null or `[]` → false); `usesPlayDisc('mednafen_pcfx', { externalFileName: 'X (Japan).cue' })` is true.
  - Done when: those pass, and `npm run lint` and `npm run check` pass.
- [ ] **7. Docs**:
  - `README.md`: X11's intro, Play and upgrade-note items (row and attribution already in).
  - `docs/features.md`: PC-FX in the systems list and the Play list; a Play sub-bullet after TurboGrafx-CD's: "**PC-FX** uses Beetle PC-FX (`mednafen_pcfx`), which ships in the image. One disc per launch: a cue, TOC, CloneCD or MDS set, or a `.chd`; a lone `.bin` doesn't play. It needs `pcfx.rom` in the BIOS folder (`Paths:Bios`)."; the ingestion folder list and the drop-files paragraph gain `ingest/pcfx/` (the formats of X1, 16 bytes–1 GiB per file).
  - `docs/deployment.md`: the `/app/ingest` row gains `ingest/pcfx/`; the BIOS list gains `pcfx.rom` (PC-FX, required); a section after the Neo Geo Pocket one, laid out like it: "Upgrading to the release that adds PC-FX:" with "**Refresh DATs after upgrading.** PC-FX discs are identified against libretro's Redump `NEC - PC-FX` DAT (no metadat). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `ingest/pcfx/` is created at startup." and "Browser play needs `pcfx.rom` in `data/bios/`; the Beetle PC-FX core ships in the image."
  - `docs/zip-format.md`: `pcfx/                 PC-FX (discs next to the ZIP)` after the `tgcd/` layout line; `pcfx` in the `system` id list (no `header`).
  - `docs/adding-a-system.md`: PC-FX in the disc-systems paragraph (`.Nec`) and the `.chd` limits sentence; the `BrowserCore` row gains "`pcfx` returns `mednafen_pcfx` (vendored, needs `pcfx.rom` via `biosFor`; the lone `.bin` rule applies as for `tgcd`)" and `mednafen_pcfx` in the external/play-disc core list; `biosFor`'s example list gains `mednafen_pcfx` → `pcfx.rom`; the box-art list gains PC-FX.
  - `docs/development.md` (Browser emulators): the vendoring list gains `@emulatorjs/core-mednafen_pcfx`, the play-page core list `mednafen_pcfx`, and the external and play-disc core lists.
  - Done when: every item is in its doc, `SupportedSystemsTableTests` passes and `pwsh scripts/codemap.ps1` is clean.

## Decisions
- 2026-11-25 Core availability → `@emulatorjs/core-mednafen_pcfx@4.2.3` exists on npm (GPL-2.0, non-threaded payloads present) and EmulatorJS 4.2.3 maps `pcfx` → `mednafen_pcfx`, so it is vendored like Beetle PCE, not operator-supplied (code)
- 2026-11-25 Name → `PC-FX`, short `PC-FX`, id `pcfx` (user)
- 2026-11-25 Formats → the same list as TurboGrafx-CD, including `.mds`/`.mdf` and `.sub` (user)
- 2026-11-25 BIOS hand-over → `biosFor` (`EJS_biosUrl`) as requested; written to `/pcfx.rom`, where `LoadCommon` looks (code: EmulatorJS 4.2.3 `downloadGameFile`, beetle-pcfx `libretro.cpp` at `b517023`)
- 2026-11-25 Lone `.bin` → not playable and refused by play-disc, as for TurboGrafx-CD; Beetle PC-FX opens only `cue|ccd|toc|chd` (code)
- 2026-11-25 DAT facts → `NEC - PC-FX`, 93 entries, one data track each (Track 2/02/3…), 14 serial duplicates handled by `DatStore`'s first-wins (code: libretro-database read 2026-11-25)
- 2026-11-25 Seeded games and e2e → none, as for TurboGrafx-CD (code)
- 2026-11-25 Review round 1 → `BrowserCore`, the extension exceptions and the README Play cell move to Step 5 (`BrowserCoreExtensionsTests` needs a system for every recorded core); `roms/pcfx/` isn't made at startup; `coreLicenses` is ordinal, so the `mednafen_pcfx-*` files follow `mednafen_pce-*`; Step 3's tests named, the `tgcd`-folder case gives three Review items, the lone `.chd` reuses `Chd_Lone_…` (reviewer)
- 2026-11-25 Review round 2 → clean (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-25-pc-fx-support.md
```
Starts the spec, or resumes it at the first unticked step.
