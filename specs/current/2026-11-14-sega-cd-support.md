---
title: Sega CD support
status: in-progress
created: 2026-11-14
---

# Sega CD support

## Summary
Adds Sega CD (`segacd`) as a disc system. Its bin/cue sets and standalone `.iso` images are imported against the Redump DAT and stored next to the game's ZIP, as Saturn and PlayStation discs are. Releases play in the browser on EmulatorJS's Genesis Plus GX core, the operator-supplied core Mega Drive already uses, through the existing play-disc endpoint, with a regional Sega CD BIOS the operator supplies.

## Out of scope
- PicoDrive for Sega CD, and any change to Mega Drive / Genesis or 32X play.
- Bundling Genesis Plus GX or any Sega CD BIOS.
- A header reader: like Saturn, `ReadHeader` returns null (no Header button, unmatched files go to Review, never `InvalidHeader`).
- `.chd`, `.ccd`, `.mds`, `.gdi`, audio as `.wav`/`.ape`/`.mp3` (the contract only groups bin/cue).
- Multi-disc swapping in one session (one disc per launch), Sega CD 32X titles, and save-state or backup-RAM export.
- Seeded Sega CD games in the Basic/Full test sets and an e2e play case (see § Decisions).
- Real-game boot checks before the PR: the maintainer does them after merge, as for the previous browser-play specs.

## Behaviour
- **B1 Handler.** `SegaCdHandler` in `RnRomManager.Systems.Sega`:

  | Member | Value |
  |---|---|
  | `Id` / `Family` | `segacd` / `segacd` |
  | `DisplayName` / `ShortName` | `Sega CD` / `SCD` |
  | `RomExtensions` | `.bin`, `.cue`, `.iso` (`[0]` = `.bin`) |
  | `MinRomSizeBytes` / `MaxRomSizeBytes` | 16 / 1 GiB (per file, as PlayStation and Saturn) |
  | `DatName` | `Sega - Mega-CD - Sega CD` (libretro `metadat/redump/`, and the thumbnails folder) |
  | `DatFolder` / `MetadatKinds` | `DatKind.Redump` / `[]` |
  | `EmbedRomsInZip` / `FileSets` | false / true |
  | `BrowserCore` | null until Step 7, then `genesis_plus_gx` |
  | `ReadHeader` | null always |
  | `DefaultBoxArtPng` | a new 512×512 Sega CD placeholder drawn for this project, with its SVG source in `Resources/boxart/` |

  Because `segacd` is its own family, a `.bin` in `ingest/md/` is never tried against the Sega CD DAT and a `.bin` in `ingest/segacd/` never against Mega Drive's.
- **B2 Registration.** `AddSystemHandler<SegaCdHandler>()` goes after `NintendoDsHandler` in `AppHost.AddSystemHandlers`, so `segacd` is last in registry order. Startup creates `roms/segacd/` and `ingest/segacd/` with their standard subfolders. `/api/systems` lists it (sorted by `DisplayName`: after Saturn, before Super Nintendo). The README row sits between Saturn and SNES. It gets its own badge colour (`--bs-segacd`, a hex used by no other token, ≥ 4.5:1 with its foreground) and the `bi bi-disc` icon, as Saturn does. After upgrading, an Admin runs **Refresh DATs** once (one Redump file, no metadat); until then ingestion returns `DatsMissing`.
- **B3 Ingest.** Nothing new in Core: the `FileSets` rules apply as for Saturn and PlayStation. A `.cue` with its `.bin` tracks is one release; files keep their exact names next to the ZIP; a taken name is `NameTaken`; a DAT hit on the data track names the game; an unmatched `.bin`/`.iso` goes to Review as `NoDatMatch`. `.cue`/`.bin` Review items follow the existing `ReviewResolver` set rules (not dismissable; resolvable when `ReviewSets.FindAsync` finds their set).
- **B4 play-disc.** `PlayDisc.MaxBytesBySystem` gains `segacd` → 1 GiB (inclusive; above it 409 `PlayDiscTooLarge`). Sega CD takes PlayStation's shapes unchanged: a single `.bin` or `.iso` streamed as-is, or exactly one `.cue` with up to 99 bins returned as a stored ZIP with the cue rewritten. Anything else is 409 `InvalidPlayDisc`.
- **B5 Play eligibility.** `genesis_plus_gx` joins `LibraryEndpoints.ExternalBrowserCores` and `externalBrowserCores` in `emulator.ts`. An `md` release can't have an external primary in practice: its family embeds ROMs (the registry refuses a mixed family). So the existing `LibraryEndpointsTests` case `[TestCase("md", true, false, "genesis_plus_gx")]` (a hand-made external `md` primary) flips to playable (`true`). The eligibility rule stays per-core, the same in the API and the SPA. A Sega CD release then shows **Play**, and the games list's `playRomId`/`playRomFileName` are set for it.
- **B6 Play URL.** The Genesis Plus GX core serves both Mega Drive (ROM from `/download`) and Sega CD (disc from play-disc), so the core alone can't choose.
  - `playUrl(gameId, rom, core, disc)` gains a required `disc: boolean`. When it's true, `&disc=1` is appended after `name`. The game page passes `rom.externalFileName !== null`.
  - The `/api/games` list item gains `playDisc` (bool): true when `playRomId` is set and the primary is external (`game.PrimaryIsExternal`), false otherwise. `GameListItem` in `types.ts` gains `playDisc: boolean`. `GameResults.svelte` passes it to `EmulatorModal`, which gains a required `disc` prop and passes it to `playUrl`; the game page passes `rom.externalFileName !== null`.
  - PlayStation, PSP and Saturn URLs also gain `&disc=1`.
  - `parsePlayParams` accepts `disc` absent or exactly `1`, and returns null for any other value or a repeated `disc`. `PlayParams` gains `disc: boolean`.
  - `play.js` loads from play-disc when the core is `pcsx_rearmed`, `ppsspp` or `yabause` (as today) **or** `params.disc` is true. Mega Drive URLs have no `disc` and load exactly as today.
- **B7 Sega CD launch.** For `genesis_plus_gx` with `disc`, the player, in order:
  1. runs the operator-core HEAD check for `genesis_plus_gx` (unchanged messages, except the system label below);
  2. fetches the three regional BIOS files from `/api/emulator/bios/<name>` **one after another, in the order `bios_CD_U.bin`, `bios_CD_E.bin`, `bios_CD_J.bin`**, each with `loadOptionalBios(name, 131072, segaCdBiosSizeError(name))`:
     - a 404 skips that file;
     - a file that isn't exactly 128 KiB stops the launch with `segaCdBiosSizeError(name)`: "The Sega CD BIOS <name> in the BIOS folder (Paths:Bios) must be 128 KiB: ask an Admin to replace it.";
     - any other failure stops the launch with `optionalBiosError`'s message.

     The first error stops the launch even if an earlier file loaded, and the later files aren't requested;
  3. if none was found, stops with `segaCdBiosMissing`: "Sega CD games need a BIOS: ask an Admin to put bios_CD_U.bin, bios_CD_E.bin or bios_CD_J.bin in the BIOS folder (Paths:Bios)." No disc, loader or core file is requested;
  4. loads the disc from play-disc (`discError` unchanged: its non-PSP wording names no system and says 1 GiB);
  5. sets `EJS_externalFiles` with every found BIOS at `/<name>`, `EJS_paths` from `operatorCorePaths('genesis_plus_gx')`, and `EJS_disableCue = false` (upstream 4.2.3 disables cue generation for Genesis Plus GX; without it a lone `.bin` would load as a cartridge). The chooser stays hidden and the control scheme stays `segaMD` (the Sega CD uses the Mega Drive pad).

  A disc whose region's BIOS is missing fails inside the core; the app doesn't read the disc's region.

  `contentFileName(core, game, name)` keeps its signature. For `genesis_plus_gx`, a content file named after any of the three BIOS names (any case) becomes `<game id>-<name>`, as `scph5501.bin` does for PlayStation. This applies with or without `disc`: renaming a Mega Drive cartridge with that name is harmless, and it means a disc never overwrites firmware. All three names pass `IsBiosName`.
- **B8 Operator-core wording.** `operatorCores.genesis_plus_gx.system` becomes `Genesis and Sega CD games`, so the 404 message reads "Genesis and Sega CD games need the Genesis Plus GX core files: ask an Admin to set them up (Emulator:GenesisCorePath)." The other Genesis messages are unchanged.
- **B9 Assumptions verified in Step 6.** Before writing the player code, check (outside the repo, `npm pack @emulatorjs/core-genesis_plus_gx@4.2.3` and the Genesis Plus GX libretro source at the commit its binaries name):
  - the core's `core.json` extensions include `cue` and `iso`, so EmulatorJS picks the cue from a set's ZIP and an `.iso` directly;
  - with no `system_directory` set by EmulatorJS 4.2.3, the core looks for `bios_CD_*.bin` in the content root `/`, where `EJS_externalFiles` writes them.

  Record both in § Decisions. If either is false, stop and ask the user.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | `src/RnRomManager.Systems.Sega/SegaCdHandler.cs`, `Resources/boxart/segacd.svg` + `.png`, `Resources/boxart/README.md`, `.csproj` (embedded resource if not globbed) | New handler (B1). |
| Api | `src/RnRomManager.Api/AppHost.cs` | Registration (B2). The Sega project is already referenced and in the Dockerfile restore layer. |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | `segacd` in `MaxBytesBySystem` (B4). |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | `genesis_plus_gx` in `ExternalBrowserCores` (B5). |
| Test data | `tests/RnRomManager.TestData/IngestSeeder.cs` | `SegaCdDat` constant, added to `RedumpLayouts` (its comment becomes "seven disc systems"). Basic/Full add `segacd` to the disc-folder loop, so `ingest/segacd/` is created, and write an empty Sega CD Redump catalog (no counts change). |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | `playDisc` on `/api/games` list items (B6). |
| Web | `src/lib/emulator.ts`, `src/lib/api/types.ts`, `src/lib/components/GameResults.svelte`, `src/lib/components/EmulatorModal.svelte`, `src/routes/games/[id]/+page.svelte`, `static/emulator/params.js`, `static/emulator/play.js` | `externalBrowserCores`; `playUrl`'s `disc` argument and `playDisc` pass-through; `parsePlayParams` `disc`; Sega CD BIOS constants and messages; `contentFileName` guard; operator label; Sega CD launch path (B5–B8). |
| Web | `src/lib/systemColours.ts`, `src/app.css`, `src/lib/components/icons.ts` | Badge colour and disc icon (B2). |
| Docs | `README.md`, `docs/adding-a-system.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/features.md`, `docs/configuration.md`, `docs/development.md` | See Step 8. |

No migration: `system_id` is a `VARCHAR`. No new config key. API contract: additive. `/api/systems` gains a row, `/api/games` items gain `playDisc`, and `browserCore`/`playRomId` are set for Sega CD games.

## Risks
- **Shared core.** Genesis Plus GX now serves two systems. B6's `disc` flag keeps Mega Drive on its current path; existing Genesis vitest and e2e cases must pass unchanged apart from B8's label.
- **Core assumptions.** Cue selection and BIOS location are inferred from EmulatorJS 4.2.3 and the core source; Step 6 checks them first (B9).
- **Region mismatch.** With "any one BIOS" a disc of another region fails inside the core with EmulatorJS's generic error. Documented in `docs/features.md`, not solved.
- **Green commits.** `SupportedSystemsTableTests` compares README rows with handlers, so Step 2 adds the row with `—` and Step 7 switches it to `genesis_plus_gx`. Every full-registry DAT manifest in the API integration tests must list `IngestSeeder.SegaCdDat` once the handler is registered, or ingestion fails `DatsMissing`; Step 2 updates them all in one commit.
- **Licences.** Nothing is bundled; no change to `licenses.json`. The README states that no Sega CD BIOS is included.

## Steps
Steps 2, 3, 4, 5 and 7 run integration tests (category `Integration`), which need Docker. Playwright (`mvp.spec.ts`) runs only in Finish.

- [x] **1. Sega CD handler** (B1): `SegaCdHandler` with `BrowserCore` null and its box art (SVG source + 512×512 PNG). Add the `IngestSeeder.SegaCdDat` constant and add it to `RedumpLayouts`. The seeder doesn't write it yet: every catalog it writes comes from its own system list, so its output is unchanged.
  - Files: `SegaCdHandler.cs`, `Resources/boxart/`, `tests/RnRomManager.Systems.Sega.Tests/SegaCdHandlerTests.cs`, `tests/RnRomManager.TestData/IngestSeeder.cs`.
  - Tests: `SegaCdHandlerTests`: every B1 value, `ReadHeader` null for any bytes, `NormalizeForHashing` unchanged, PNG valid and 512×512; with a registry holding only this handler, `DatCache` loads a `FixtureDatWriter` Redump catalog with one synthetic cue/bin game and finds its `.bin` by SHA-1, and loads an empty catalog.
  - Done when: those tests pass and the solution builds with warnings as errors.
- [x] **2. Register Sega CD and update fixtures** (B2):
  - Registration after `NintendoDsHandler`; README row `| Sega CD | `segacd` | Next to ZIP | `.bin`, `.cue`, `.iso` | Redump | — |` between Saturn and SNES.
  - `IngestSeeder`: `segacd` in the disc-folder loop and an empty Sega CD catalog for Basic and Full. If Step 1 shows `RedumpLayouts` membership changes seeder output, that check moves here.
  - Every API integration test file whose full-registry manifest lists `IngestSeeder.NintendoDsDat` adds `[IngestSeeder.SegaCdDat] = []` (keep intentional omissions). Update the counts that grow by one Redump file and one system: `DatEndpointsTests` (refresh requests 102 → 103; DAT names 27 → 28 with the message "the 7 disc systems"; `SegaCdDat` in the ordered DAT-name list between `MegaDriveDat` and `SaturnDat`, since the list is ordered by DAT name and `Sega - Mega Drive…` < `Sega - Mega-CD…` < `Sega - Saturn`), `IngestSeederTests` (`.dat` files +1), `SystemStartupTests` (`segacd` appended to both id lists), `IngestEndpointsTests.Pending_ListsEverySystem_IgnoringJunk` (`segacd` last), `LibraryEndpointsTests` (`/api/systems` lists `Sega CD`/`SCD` between Saturn and Super Nintendo), and the ordered system list in `mvp.spec.ts`.
  - Colour (`systemColours.ts`, `app.css`, with the contrast ratio in the comment) and icon (`icons.ts`).
  - Files: `AppHost.cs`, `README.md` (row only), `IngestSeeder.cs`, the test files above, `systemColours.ts`, `app.css`, `icons.ts`, `systemColours.test.ts`. Mechanical manifest lines don't count toward the line budget.
  - Tests: the updated integration files, `IngestSeederTests`, `SeedIngestTests` (Basic/Full counts unchanged), `SystemStartupTests`, `SupportedSystemsTableTests`, `systemColours.test.ts`; web `npm run lint` and `npm run check`.
  - Done when: those pass and `pwsh scripts/build-docker.ps1` builds.
- [x] **3. Verify Sega CD ingest** (B3): Testcontainers cases in `IngestPipelineTests.cs` and `ReviewEndpointsTests.cs` with synthetic files (never real images):
  - a DAT-matched cue + 2 bins → one release, files next to the ZIP under their exact names, info.json `files` lists the tracks;
  - an unmatched standalone `.iso` → Review `NoDatMatch` with system `segacd`, resolvable with **Manual**, stored as `.iso`;
  - family isolation: a 64 KiB `.bin` with no `SEGA` header at 0x100 whose SHA-1 is in the Sega CD DAT, dropped in `ingest/md/`, is not matched against the Sega CD DAT. It goes to Review as `NoDatMatch` with system `md`: Mega Drive's header is optional, so a headerless file isn't `InvalidHeader`. Nothing is stored under `roms/segacd/`;
  - a file over 1 GiB is `TooLarge` (use a sparse file or the existing size-stub pattern, not 1 GiB of data).
  - Done when: those cases and the existing ingest suites pass.
- [x] **4. Sega CD play-disc** (B4): `segacd` in `MaxBytesBySystem`. `BrowserCore` stays null.
  - Files: `PlayDisc.cs`, `LibraryEndpointsTests.PlayDisc.cs`.
  - Tests: `LibraryEndpointsTests.PlayDisc`: Sega CD single `.bin`, single `.iso`, cue set; exactly 1 GiB → 200, one byte over → 409 `PlayDiscTooLarge`; two cues → 409 `InvalidPlayDisc`; psx, psp and saturn cases unchanged; `PlayDisc_CapsEachSystem` gains `["segacd"] = 1L << 30`. `PlayDiscCopyTests`.
  - Done when: those pass.
- [x] **5. Play-URL `disc` flag** (B6): no Sega CD link exists yet; PlayStation, PSP and Saturn gain `&disc=1`.
  - `LibraryEndpoints`: `playDisc` on list items. `types.ts`: `GameListItem.playDisc`.
  - `emulator.ts`: `playUrl(gameId, rom, core, disc)`.
  - `EmulatorModal.svelte` gains a required `disc: boolean` prop and passes it to `playUrl`. The game page passes `disc={playRom?.externalFileName != null}` to the modal and `rom.externalFileName !== null` to the PSP new-tab `href`. `GameResults.svelte` passes `playing?.playDisc ?? false` to the modal and `game.playDisc` to its PSP `href`.
  - `params.js`: `disc` in `parsePlayParams`/`PlayParams`. `play.js`: disc loading when `params.disc`.
  - Files: those, plus `EmulatorModal.test.ts`, `GameResults.test.ts`, `game-page.test.ts`, `emulator.test.ts`, `emulatorParams.test.ts`, `emulatorPlay.test.ts`, `LibraryEndpointsTests.cs`, `tests/e2e/specs/mvp.spec.ts`.
  - Tests:
    - `LibraryEndpointsTests`: `playDisc` is true for an external playable `psx` primary, and false for an embedded `gb` one and for a non-playable game.
    - `emulator.test.ts`: `playUrl` adds `&disc=1` after `name` only when `disc` is true.
    - `emulatorParams.test.ts`: `disc` absent → false, `1` → true, and `0`, `true` or a repeated `disc` → null.
    - `EmulatorModal.test.ts`: existing renders pass `disc: false`; with `disc: true`, the iframe `src` ends in `&disc=1`.
    - `game-page.test.ts`: the PSX modal iframe `src` and the PSP `href` gain `&disc=1`.
    - `GameResults.test.ts`: the quick-play URLs, plus a `playDisc: true` item whose modal `src` has `&disc=1`.
    - `emulatorPlay.test.ts`: `play page PlayStation` and `play page Saturn` pass with `disc=1` in their URLs.
    - `mvp.spec.ts` (runs in Finish): the four `$`-anchored play-URL regexes for Disc Runner (game page and Library quick play), Sky Rally and Pocket Ninja end in `&disc=1$`; the Mega Drive, Master System, 32X, SNES and bundled-core regexes stay as they are.
  - Done when: `npm run lint`, `npm run check`, those vitest files and the `LibraryEndpointsTests` cases pass.
- [x] **6. Sega CD player** (B7–B9): first verify B9 and record it in § Decisions (stop and ask if it fails). Then:
  - `params.js`: `segaCdBios` (the three names, in U, E, J order), `segaCdBiosBytes = 131072`, `segaCdBiosSizeError(name)`, `segaCdBiosMissing`, the `contentFileName` guard and B8's label.
  - `play.js`: the B7 order, `EJS_externalFiles` with every BIOS found, and `EJS_disableCue = false` for the Sega CD path only.
  - Nothing produces a Sega CD play link yet, since `BrowserCore` is null.
  - Files: `params.js`, `play.js`, `emulatorParams.test.ts`, `emulatorPlay.test.ts`, `tests/e2e/specs/mvp.spec.ts`.
  - Tests:
    - `emulatorParams.test.ts`: a new `describe('Sega CD (B7, B8)')`:
      - `contentFileName` renames the three BIOS names in any case, both with `disc` (Sega CD) and for a non-disc `genesis_plus_gx` cartridge;
      - the size and missing messages;
      - B8's Genesis label.

      The Genesis suite passes with only that label updated.
    - `emulatorPlay.test.ts`: a new `play page Sega CD`:
      - the request order is the core HEAD, `bios_CD_U.bin`, `bios_CD_E.bin`, `bios_CD_J.bin`, then play-disc;
      - one, two or three BIOS files present: all of them are passed as external files;
      - no BIOS file: `segaCdBiosMissing`, and no disc or loader request;
      - `bios_CD_E.bin` the wrong size after a valid `bios_CD_U.bin`: that file's message, and no `bios_CD_J.bin` or disc request;
      - a 500: `optionalBiosError`;
      - a core 404: no BIOS request;
      - `EJS_disableCue === false`, `EJS_paths` set and scheme `segaMD`.

      `play page Genesis and Master System` passes with only B8's label updated (line ~514). Mega Drive still has no `disc`, loads its ROM from `/download` and leaves `EJS_disableCue` unset.
    - `mvp.spec.ts:677`: the Genesis missing-core text gets B8's label (runs in Finish).
  - Done when: `npm run lint`, `npm run check` and those vitest files pass.
- [x] **7. Enable Sega CD Play** (B5): `SegaCdHandler.BrowserCore = "genesis_plus_gx"`; `genesis_plus_gx` in both external-core sets; README Play cell → `` `genesis_plus_gx` ``.
  - Files: `SegaCdHandler.cs`, `LibraryEndpoints.cs`, `emulator.ts`, `README.md` (cell only), and the tests below.
  - Tests:
    - `SegaCdHandlerTests` and `SupportedSystemsTableTests`.
    - `LibraryEndpointsTests`:
      - `GameDetail_BrowserCore_IsTheSystemHandlers` gains `segacd` → `genesis_plus_gx`;
      - `Games_QuickPlay_IsThePrimaryReleaseWhenPlayable` gains `("segacd", true, true, "genesis_plus_gx")` with `playDisc` true;
      - `[TestCase("md", true, false, "genesis_plus_gx")]` becomes `("md", true, true, "genesis_plus_gx")` (B5);
      - `("md", false, true, …)` still plays from the ZIP, with `playDisc` false.
    - `emulator.test.ts`: `canPlay` is true for `genesis_plus_gx` with an external `.cue` or `.iso`.
    - `game-page.test.ts`: for a `segacd` game with `browserCore: 'genesis_plus_gx'` and an external `.cue` release, clicking the release's Play button opens the modal, and its iframe `src` has `core=genesis_plus_gx` and `&disc=1`.
  - Done when: those tests pass.
- [ ] **8. Docs**:
  - `README.md`: intro line (Sega CD bin/cue sets kept together), the Play bullet (Sega CD: one disc per launch, up to 1 GiB, standalone `.bin`/`.iso` or a cue with its tracks, needs the Genesis Plus GX files and at least one of `bios_CD_U.bin`/`bios_CD_E.bin`/`bios_CD_J.bin` in `data/bios/`), the upgrade/Refresh DATs list, and the attribution note that no Sega CD BIOS is included.
  - `docs/features.md`: Sega CD ingest and play rules, the BIOS files and the region-mismatch note; the quoted Genesis missing-core message gets B8's "Genesis and Sega CD games" label.
  - `docs/configuration.md`: the Genesis Plus GX section also enables Sega CD.
  - `docs/deployment.md`: the DAT refresh after upgrade and the BIOS files.
  - `docs/zip-format.md`: `segacd` in the layout and `system` list (no `header`).
  - `docs/adding-a-system.md`: Sega CD in the disc-systems paragraph, the `BrowserCore` row (a shared core, the `disc` flag, `EJS_disableCue`), the box-art list.
  - `docs/development.md`: the play page's `disc` parameter and Sega CD BIOS handling.
  - Tests: `SupportedSystemsTableTests`.
  - Done when: every file above has the stated content and the README table row reads `| Sega CD | `segacd` | Next to ZIP | `.bin`, `.cue`, `.iso` | Redump | `genesis_plus_gx` |`.

## Decisions
- 2026-11-14 Which core plays Sega CD? → Genesis Plus GX, reusing `Emulator:GenesisCorePath` (user)
- 2026-11-14 Sega CD BIOS → any one of `bios_CD_U.bin`/`bios_CD_E.bin`/`bios_CD_J.bin` required; all found are passed to the core (user)
- 2026-11-14 Formats → `.bin`, `.cue`, `.iso`, like PlayStation (user)
- 2026-11-14 Name → `Sega CD`, short name `SCD`, id `segacd` (user)
- 2026-11-14 Header → none, like Saturn (code: matches the other bin/cue disc systems; keeps scope small)
- 2026-11-14 How does the shared core know it's a disc? → a `disc=1` play-URL flag for external releases; the core-based checks stay (code)
- 2026-11-14 How does Library quick play know a release is external? → a new `playDisc` field on `/api/games` items, and `playUrl` takes an explicit `disc` argument (reviewer)
- 2026-11-14 An external `md` primary with `genesis_plus_gx` now external-capable → flip that test case to playable; the registry makes the state impossible, and eligibility stays per-core in the API and SPA alike (reviewer)
- 2026-11-14 Sega CD BIOS fetch → sequential U, E, J; the first error stops the launch (reviewer)
- 2026-11-14 `contentFileName` guard → by core only (`genesis_plus_gx`), so it renames with or without `disc`; it's harmless for Mega Drive (reviewer)
- 2026-11-14 Player work size → two steps, 5 (URL `disc` flag) and 6 (Sega CD player), each its own commit and tick (reviewer)
- 2026-11-14 Seeded games and e2e → empty Sega CD catalog in Basic/Full (no count changes), no e2e play case; vitest covers the player, as for the DS steps before its staged cartridge (code)
- 2026-11-14 B9 cue/iso → verified: `@emulatorjs/core-genesis_plus_gx@4.2.3`'s `core.json` (in `genesis_plus_gx-wasm.data`, build 2.0.2) lists `cue` and `iso` among its extensions (code)
- 2026-11-14 B9 BIOS location → verified: the package names no source commit, so EmulatorJS/Genesis-Plus-GX `master` `libretro/libretro.c` was read. Without a system directory it falls back to the content's folder (`g_rom_dir`) and joins `bios_CD_E/U/J.bin` to it; the binary carries those names. EmulatorJS 4.2.3's `retroarch.cfg` sets no `system_directory` and writes content and `EJS_externalFiles` at `/`, so the BIOS files sit next to the disc (code)

## Kickoff prompt
```text
/spec implement 2026-11-14-sega-cd-support.md
```
Starts the spec, or resumes it at the first unticked step.
