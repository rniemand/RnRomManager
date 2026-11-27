---
title: Amiga CD32 and CDTV support
status: in-progress
created: 2026-11-27
---

# Amiga CD32 and CDTV support

## Summary
Adds two disc systems to `RnRomManager.Systems.Commodore`: **Commodore Amiga CD32** (`cd32`) and **Commodore CDTV** (`cdtv`). Their Redump discs are imported against libretro's `Commodore - CD32` and `Commodore - CDTV` DATs and stored next to the game's ZIP, as PC-FX discs are. Releases play in the browser on PUAE (`puae`, already vendored for Commodore Amiga) through play-disc. The play URL carries the system, so the play page can load the right Kickstart ROMs from `Paths:Bios` and pick the right PUAE model.

## Out of scope
- **Multi-disc sets.** `DiscSets.MaxDiscs` stays 0 for both systems, so no **All discs** button, and `play-disc-set` refuses both systems because neither is in `FirmwareNames`. PUAE would also need the vendored `.m3u`-first edit in `downloadRom()`, which today covers only `yabause` and `genesis_plus_gx`. The DATs hold 2 CD32 and 8 CDTV `(Disc n)`/`(Disc A)` entries; each disc plays alone.
- **Library battery saves.** PUAE exposes no SRAM: `retro_get_memory_data` returns only `RETRO_MEMORY_SYSTEM_RAM`. A CD32/CDTV game's NVRAM goes to its own `<content>.nvr` in the core's save directory. The modal's battery export finds nothing (`empty`), which is silent, the same as for Amiga floppies today. NVRAM persistence across sessions is left to the maintainer's post-merge boot check.
- **Other content.** `.m3u`, `.gdi` and hard-disk images, a header reader (`ReadHeader` returns null), and bundling any Kickstart ROM.
- **Other PUAE models.** The Amiga Forever or TOSEC Kickstart file names that PUAE also searches for, and the CD32FR model.
- **Seeded games and e2e.** No seeded CD32/CDTV games in the Basic/Full sets and no e2e play case: a launch needs Kickstart ROMs. Vitest and the API tests cover the player, as for PC-FX.
- **Real-game boot checks.** The maintainer checks after merge that the Kickstart is found, the model is right and NVRAM is saved.

## Behaviour
- **A1 Handlers.** `Cd32Handler` and `CdtvHandler` (both new), in `src/RnRomManager.Systems.Commodore/`, built like `PcfxHandler`:

  | Member | `Cd32Handler` | `CdtvHandler` |
  |---|---|---|
  | `Id` / `Family` | `cd32` / `cd32` | `cdtv` / `cdtv` |
  | `DisplayName` / `ShortName` | `Commodore Amiga CD32` / `CD32` | `Commodore CDTV` / `CDTV` |
  | `RomExtensions` | `.bin`, `.cue`, `.nrg`, `.chd`, `.iso`, `.ccd`, `.img`, `.sub`, `.mds`, `.mdf`, `.toc` (`[0]` = `.bin`; the same list and order as `SaturnHandler`) | same |
  | `MinRomSizeBytes` / `MaxRomSizeBytes` | 16 / 1 GiB (the DAT's largest `.bin` is 643,311,984 bytes) | 16 / 1 GiB (largest 767,163,600) |
  | `DatName` | `Commodore - CD32` (libretro `metadat/redump/Commodore - CD32.dat`, 205 games, one `.bin` each; libretro-thumbnails `Commodore_-_CD32`) | `Commodore - CDTV` (65 games, one `.bin` each; `Commodore_-_CDTV`) |
  | `DatFolder` / `MetadatKinds` | `DatKind.Redump` / `[]` | same |
  | `EmbedRomsInZip` / `FileSets` | false / true | same |
  | `BrowserCore` | null until Step 6, then `puae` | same |
  | `ReadHeader` | null always | same |
  | `DefaultBoxArtPng` | `BoxArt.Lazy(typeof(Cd32Handler), "boxart.cd32.png")`; new 512×512 placeholder `cd32.svg` + `cd32.png` | `BoxArt.Lazy(typeof(CdtvHandler), "boxart.cdtv.png")`; `cdtv.svg` + `cdtv.png` |

  `Resources/boxart/README.md`: the source sentence becomes "`c64.svg`, `c128.svg`, `pet.svg`, `plus4.svg`, `vic20.svg`, `amiga.svg`, `cd32.svg` and `cdtv.svg` are the sources (made for this project, MIT)." Each system has its own family, so a disc in `ingest/cd32/` is never tried against the CDTV DAT, and the other way round. The class doc comment states the DAT facts above. Both `BrowserCore` comments read `/// <summary>CD32 A1: PUAE, vendored; needs the operator's Kickstart ROMs.</summary>` (rule references use the `CD32` prefix for both systems, as `CD32 A10` does).
- **A2 Registration.** In `AppHost.AddSystemHandlers`, `services.AddSystemHandler<Cd32Handler>();` then `services.AddSystemHandler<CdtvHandler>();` go last. Today that's after `SegaPicoHandler`; if another spec has appended handlers since, they still go last. The Commodore project is already referenced and is in the Dockerfile restore layer. Startup creates `ingest/cd32/` and `ingest/cdtv/` with their `_*` folders. `/api/systems` (`DisplayName`, `OrdinalIgnoreCase`) lists `Commodore Amiga`, `Commodore Amiga CD32`, `Commodore CDTV`, `Commodore PET` in that order. `/api/admin/dats` (`DatName`, `Ordinal`) lists `Commodore - CD32` and `Commodore - CDTV` between `Commodore - Amiga` and `Commodore - PET` (the order is `Commodore - 64`, `Commodore - Amiga`, `Commodore - CD32`, `Commodore - CDTV`, `Commodore - PET`). A DAT refresh fetches two more Redump files. Ingestion refuses with `DatsMissing` until **Admin → DATs → Refresh DATs** has run.
- **A3 Colour, icon, catalogs.**
  - `app.css` gets two blocks directly after the `amiga` block, laid out like it:
    - `/* Amiga CD32 system colour: a deep CD32 violet, same in both themes; white on it is AAA (~12:1). */`, `:root { --bs-cd32: #3d2b6b; }`, `.text-bg-cd32 { color: #fff !important; background-color: var(--bs-cd32) !important; }`
    - `/* CDTV system colour: a dark walnut, same in both themes; white on it is AAA (~12:1). */`, `--bs-cdtv: #40362a`, `.text-bg-cdtv` the same way.

    No other token uses either value.
  - `systemColours.ts`: `cd32: { name: 'cd32', foreground: '#fff' }` and `cdtv: { name: 'cdtv', foreground: '#fff' }` directly after `amiga`, and `'cd32'` and `'cdtv'` join the `SystemColour` name union in `src/lib/types/SystemColour.ts` after `'amiga'`.
  - `src/lib/icons/icons.ts`: `'system-cd32': 'bi bi-disc'` and `'system-cdtv': 'bi bi-disc'` after `'system-pcfx'`.
  - `IgdbPlatforms.ById`: `["cd32"] = 114` after `["c64"] = 15`, and `["cdtv"] = 158` after it. The map is keyed alphabetically. IGDB lists "Amiga CD32" as 114 and "Commodore CDTV" as 158 (from RomM's IGDB platform table, read 2026-11-27). No other key uses either id.
  - `systemEmulators.json`: keys `cd32` and `cdtv` after `c64`, keeping the map alphabetical. Each gets a copy of `amiga`'s three entries in its order: WinUAE (`["windows"]`), FS-UAE, and RetroArch (`"note": "RetroArch core: PUAE"`).
- **A4 Ingest.** Nothing new in Core: the `FileSets` rules apply as for PC-FX. Each DAT lists one `bin` per game. That's either a whole single-track disc with no track suffix, or `(Track 01)` for a multi-track disc. A cue, TOC, CloneCD or MDS set is one release whose main file is the matched `.bin`, and files keep their exact names next to the ZIP. An unmatched `.bin`, a `.chd`, an `.iso` or an `.nrg` goes to Review as `NoDatMatch`. `DiscSets` takes `.nrg` as a main file on `saturn` only, which stays as it is, since there are no sets here.
- **A5 play-disc.** `PlayDisc` gains `public const string Cd32SystemId = "cd32"` and `public const string CdtvSystemId = "cdtv"`, both added to `SupportedSystems`. The shared 2 GiB `MaxBytes` cap applies. `ShapeProblem` gains a branch after the TurboGrafx-CD/PC-FX one: `members.Count == 1 && systemId is Cd32SystemId or CdtvSystemId` returns null for a single `.iso`, `.chd` or `.nrg` (`OrdinalIgnoreCase`; `HasExtension` for `.nrg`). Otherwise it returns `"a CD32 disc must be a .cue with its .bin tracks, or an .iso, .chd or .nrg"` (`cd32`) or `"a CDTV disc must be a .cue with its .bin tracks, or an .iso, .chd or .nrg"` (`cdtv`). So a lone `.bin` is 409 `InvalidPlayDisc` with `title` `The disc can't be played: a CD32 disc must be a .cue with its .bin tracks, or an .iso, .chd or .nrg.` An `.nrg` then takes the existing `NrgDiscAsync` path: a ZIP of a generated cue and `track-01.bin`. Cue, TOC, CloneCD and MDS sets follow the existing multi-member rules, and the stored `.toc`/`.ccd`/`.mds` is never sent. `FirmwareNames` gains no entry. PUAE's compiled `valid_extensions` (`BrowserCoreExtensionsTests`) lists `cue|ccd|nrg|mds|iso|chd`, so a generated cue, an `.iso` and a `.chd` all open. EmulatorJS 4.2.3's `downloadRom()` picks the cue from the ZIP, since PUAE's `core.json` lists `cue`. A single non-archive file is passed through as the only file. `puae` is on EmulatorJS's disable-cue list, so a lone `.bin` would never get a generated cue, which is why it's refused.
- **A6 Play URL.** `parsePlayParams` takes `system`, which is either absent or exactly `cd32` or `cdtv`, and given once. It's accepted only with `core=puae`, `disc=1` and no `set`; anything else returns null ("Invalid emulator link."). `PlayParams` gains `system: 'cd32' | 'cdtv' | null`, and its typedef is updated. Every existing link stays valid. `params.js` exports `amigaCdSystems = ['cd32', 'cdtv']` (new). In `emulator.ts`, `playUrl` gains a last parameter `system: string | null = null`, and appends `&system=<system>` right after the `&set=1` line when `system` is `'cd32'` or `'cdtv'`. Only the modal needs it. `puae` never opens in a new tab, so the new-tab calls (`GameResults.svelte` `playAction`'s link, `GlobalSearch.svelte` `play`, `ReleaseTable.svelte`'s new-tab link) stay unchanged. A quick play from search opens the game page, whose modal passes the system.
  - `EmulatorModal.svelte`: a new prop `system?: string | null`, default null, after `discSet` in `Props`. Its iframe uses `playUrl(gameId, rom, core, disc || discSet !== null, playSave, discSet, system)`. The three mounts pass `system={game.system}` (`routes/games/[id]/+page.svelte`, `GameSaves.svelte`) and `system={playing?.game.system ?? null}` (`GameResults.svelte`).
- **A7 Kickstart ROMs.** Names and sizes come from libretro-uae `2245d34` (`libretro/libretro-core.h`, `uae_kickstarts`; `libretro-core.c`, `retro_config_kickstart`). PUAE looks for its Kickstarts in the system directory. EmulatorJS 4.2.3 sets none, so it's the content root `/`, where `EJS_externalFiles` writes. In `play.js`, an Amiga CD launch is `params.core === 'puae' && params.system !== null`. Before any disc request it fetches, one at a time, through `loadOptionalBios`:
  - **CD32:**
    - `kick40060.CD32` with `loadOptionalBios('kick40060.CD32', cd32KickstartMaxBytes, cd32KickstartSizeError, false)`, where `cd32KickstartMaxBytes = 1048576`. A 404 stops with `cd32BiosMissing`.
    - If its length is ≤ 524,288 bytes (PUAE only looks for the extended ROM when the Kickstart is ≤ 512 KiB, which is how a 1 MiB combined ROM works on its own), then `kick40060.CD32.ext` with `loadOptionalBios('kick40060.CD32.ext', 524288, cd32ExtSizeError)` (exact). A 404 stops with `cd32ExtMissing`.
    - Every file found goes into `EJS_externalFiles` at `/<name>`.
  - **CDTV:**
    - `kick34005.A500`, then `kick34005.CDTV`, each with `loadOptionalBios(name, 262144, cdtvBiosSizeError(name))` (exact 256 KiB).
    - A 404 on either stops with `cdtvBiosMissing`.
    - Both go into `EJS_externalFiles` at `/kick34005.A500` and `/kick34005.CDTV`. PUAE's CDTV model uses KS 1.3 `kick34005.A500` as its Kickstart and `kick34005.CDTV` as its extended ROM.
  - Any other status, network error or wrong size stops the launch at that file, with `loadOptionalBios`'s existing messages for 401 and other failures.
  - New exports in `params.js`, exact text:
    - `cd32BiosMissing` = `'CD32 games need a Kickstart: ask an Admin to put kick40060.CD32 in the BIOS folder (Paths:Bios).'`
    - `cd32KickstartSizeError` = `'The CD32 Kickstart kick40060.CD32 in the BIOS folder (Paths:Bios) is empty or larger than 1 MiB: ask an Admin to replace it.'`
    - `cd32ExtMissing` = `'This CD32 Kickstart needs its extended ROM: ask an Admin to put the 512 KiB kick40060.CD32.ext in the BIOS folder (Paths:Bios), or use the 1 MiB combined kick40060.CD32.'`
    - `cd32ExtSizeError` = `'The CD32 extended ROM kick40060.CD32.ext in the BIOS folder (Paths:Bios) must be 512 KiB: ask an Admin to replace it.'`
    - `cdtvBiosMissing` = `'CDTV games need two BIOS files: ask an Admin to put kick34005.A500 and kick34005.CDTV in the BIOS folder (Paths:Bios).'`
    - `cdtvBiosSizeError(name)` returns `` `The CDTV BIOS ${name} in the BIOS folder (Paths:Bios) must be 256 KiB: ask an Admin to replace it.` ``
    - `cd32KickstartMaxBytes`, `cd32ExtBytes` (524288) and `cdtvBiosBytes` (262144).
  - No `contentFileName` guard: no CD32/CDTV content can carry those names, since none of `.CD32`, `.ext`, `.A500` or `.CDTV` is an accepted extension.
  - Amiga floppy launches (no `system`) load no Kickstart, as today (built-in AROS).
- **A8 Model.** For an Amiga CD launch, `play.js` adds `puae_model: 'CD32'` (`cd32`) or `puae_model: 'CDTV'` (`cdtv`) to `EJS_defaultOptions`, and sets `EJS_hideSettings = ['puae_model']`. `puae` isn't in `hidesCoreChooser`, so nothing overwrites it. On `Automatic`, PUAE would pick CD32 for any CD, and CDTV only when `CDTV` is in the content path (`libretro-core.c`, the `DC_IMAGE_TYPE_CD` branch). Redump names rarely contain it, so the model is set explicitly. A value the user saved for this game earlier still wins: EmulatorJS's `getCoreSettings` skips a default it already has. Hiding the setting stops anyone saving a wrong model. No other PUAE option is set.
- **A9 Disk wording.** The floppy wording applies only to sets:
  - `discError`'s branch becomes `if (diskSetCores.includes(core) && set)`.
  - `play.js` shows `'Downloading disks…'` only when `diskSetCores.includes(params.core) && params.set`.
  - Floppy launches always have `set=1` (Amiga and Plus/4 floppies are in the ZIP otherwise), so nothing changes for them. A CD32/CDTV disc gets the disc wording (`Downloading disc…`, `discError`'s non-PSP messages). `setMedia` needs no change: CD32/CDTV releases never have a `discSet`.
- **A10 Play eligibility.**
  - `puae` joins `LibraryEndpoints.ExternalBrowserCores` and `PlayDiscCores`, and `externalBrowserCores` and `playDiscCores` in `emulator.ts`, appended last in each.
  - The **lone `.bin` rule** extends to `puae`:
    - In `LibraryDtos.ToListItemDto`, `core is "mednafen_pce" or "mednafen_pcfx"` becomes `core is "mednafen_pce" or "mednafen_pcfx" or "puae"`. Its comment gains ", and (CD32 A10) PUAE has cue generation off, so a lone CD32/CDTV .bin never plays".
    - In `canPlay`, `(game.browserCore === 'mednafen_pce' || game.browserCore === 'mednafen_pcfx')` gains `|| game.browserCore === 'puae'`. Its doc comment gains "CD32 A10: PUAE (Amiga CD32 and CDTV) follows the same rule; Amiga floppies are never external."
  - `usesPlayDisc` then gives `&disc=1` for every external CD32/CDTV release.
  - An Amiga floppy is never external, so its eligibility doesn't change.
  - The `ExternalBrowserCores` doc comment gains ", (CD32 A10) PUAE for Amiga CD32 and CDTV" right after "(PC-FX X6) Beetle PC-FX for PC-FX".
- **A11 BIOS Info.** `BiosInfo.Catalog` gains three groups after `PC-FX`:
  - `new("Commodore Amiga CD32", Need.All, [UpTo("kick40060.CD32", MiB)], "PUAE needs a CD32 Kickstart for every game: the 512 KiB ROM with kick40060.CD32.ext, or the 1 MiB combined ROM.")`
  - `new("Commodore Amiga CD32 (extended ROM)", Need.Optional, [Exact("kick40060.CD32.ext", 512 * KiB)], "Needed unless kick40060.CD32 is the 1 MiB combined ROM.")`. Its `ok` mirrors the play page (A7): in `Check`, before the groups, `var cd32Kick = CheckFile(root, folderExists, Catalog.Single(g => g.System == Cd32Group).Files[0]);`, and for the group whose `System` is `Cd32ExtGroup` the generic `ok` is ANDed with `files[0].Status == "Valid" || cd32Kick is not { Status: "Valid", Size: <= 512 * KiB }` (`private const string Cd32Group = "Commodore Amiga CD32"` and `Cd32ExtGroup = "Commodore Amiga CD32 (extended ROM)"`, used in the catalog entries too). So a 512 KiB Kickstart without the `.ext` shows this group not ok, while a 1 MiB combined one, or no Kickstart at all (the CD32 group already shows that), leaves it ok.
  - `new("Commodore CDTV", Need.All, [Exact("kick34005.A500", 256 * KiB), Exact("kick34005.CDTV", 256 * KiB)], "PUAE needs Kickstart 1.3 and the CDTV extended ROM for every game.")`

  The Admin BIOS tab renders the catalog generically, so it needs no SPA change.
- **A12 README.**
  - Two supported-systems rows, in the generated order (`SupportedSystemsTableTests`) after `Commodore Amiga`:
    - `| Commodore Amiga CD32 | `cd32` | Next to ZIP | `.bin`, `.cue`, `.nrg`, `.chd`, `.iso`, `.ccd`, `.img`, `.sub`, `.mds`, `.mdf`, `.toc` | Redump | — |`
    - `| Commodore CDTV | `cdtv` | … | Redump | — |`

    Their Play cells become `` `puae` `` in Step 6.
  - Prose edits in Step 8:
    - In the intro, "a PlayStation, Saturn, Sega CD, 3DO, TurboGrafx-CD or PC-FX bin/cue set" gains ", Amiga CD32 or CDTV" after `PC-FX`, and so does the `.chd` sentence.
    - The **Game detail** Play list gains `Commodore Amiga CD32` and `Commodore CDTV` after `PC-FX`, and after PC-FX's clause (ending "needs `pcfx.rom` in `data/bios/`") comes the clause "; CD32 and CDTV play one disc per launch, up to 2 GiB, a cue set, an `.iso`, a `.chd` or an `.nrg` (never a lone `.bin`), and need Kickstart ROMs in `data/bios/` (`kick40060.CD32`, plus `kick40060.CD32.ext` unless it is the 1 MiB combined ROM; `kick34005.A500` and `kick34005.CDTV` for CDTV)".
    - The **Game detail** sentence "PlayStation, Saturn, Sega CD, 3DO, TurboGrafx-CD and PC-FX also play a CloneCD, MDS or TOC rip" gains ", Amiga CD32 and CDTV" after `PC-FX` ("…, TurboGrafx-CD, PC-FX, Amiga CD32 and CDTV also play…").
    - The upgrade note's system list gains both names after `PC-FX`.
    - In the attribution, the PUAE line's `(Commodore Amiga)` becomes `(Commodore Amiga, Amiga CD32 and CDTV)`; its closing "No Commodore Kickstart ROM is included." already covers the Kickstarts

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | `src/RnRomManager.Systems.Commodore/Cd32Handler.cs`, `CdtvHandler.cs` (new); `Resources/boxart/cd32.svg`, `cd32.png`, `cdtv.svg`, `cdtv.png` (new); `Resources/boxart/README.md` | A1 |
| Api | `src/RnRomManager.Api/AppHost.cs` | A2 |
| Core | `src/RnRomManager.Core/Media/IgdbPlatforms.cs` | A3 |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | A5 |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs`, `LibraryDtos.cs` | A10 |
| Api | `src/RnRomManager.Api/Endpoints/BiosInfo.cs` | A11 |
| Web | `src/app.css`, `src/lib/systemColours.ts`, `src/lib/types/SystemColour.ts`, `src/lib/icons/icons.ts`, `src/lib/systemEmulators.json` | A3 |
| Web | `static/emulator/params.js`, `static/emulator/play.js` | A6–A9 |
| Web | `src/lib/emulator.ts`, `src/lib/components/modals/EmulatorModal.svelte`, `src/lib/components/games/GameResults.svelte`, `GameSaves.svelte`, `src/routes/games/[id]/+page.svelte` | A6, A10 |
| Test data | `tests/RnRomManager.TestData/IngestSeeder.cs` | `Cd32Dat = "Commodore - CD32"` and `CdtvDat = "Commodore - CDTV"` (new) after `PcfxDat`; both appended to `RedumpLayouts` (its doc comment's disc-system count +2) and to `EmptyDats` after `PcfxDat`; `"cd32", "cdtv"` appended to the disc-folder loop; `[Cd32Dat] = [], [CdtvDat] = []` after `[PcfxDat] = []` in the seed `FixtureDatWriter.Write` call |
| Tests | see § Steps | |
| Docs | `README.md`, `docs/features.md`, `docs/zip-format.md`, `docs/deployment.md`, `docs/configuration.md`, `docs/development.md`, `docs/adding-a-system.md` | Step 7 |

There's no migration (`system_id` is a `VARCHAR`), no new config key and no new npm package. The API changes are additive: `/api/systems` and `/api/admin/dats` gain two rows, `/api/admin/bios` gains three groups, and `browserCore`/`playRomId`/`playDisc` are set for CD32/CDTV games. Existing data and existing play links are untouched.

## Risks
- **Every full-registry DAT manifest must list both new DATs** once the handlers are registered, or ingestion fails with `DatsMissing`. Step 2 updates them all in one commit.
- **Parallel specs.** Another system spec may merge first (SuperGrafx, Pocket Challenge V2 and Pico already have). Every count below is given as a change ("+2"), and positions are given relative to an anchor rather than a line number. Whoever merges second re-derives the absolute values.
- **Kickstart, model and NVRAM are read from source, not run** (A7, A8). The maintainer's post-merge boot check confirms them. Until then, the risk is an in-core "Kickstart ROM not found" message, not data loss.
- **`puae` joins the external and play-disc sets.** Amiga floppies are always embedded, so the `external === null` path is unchanged. Step 7's tests prove that an `.adf`, `.adz` or `.ipf` keeps its eligibility.
- **`discError`/status wording change (A9).** The existing `puae`/`vice_xplus4` cases all pass `set=true` and keep their text. Step 6 adds the `set=false` case.

## Steps
Steps 2, 3, 4, 5 and 7 run integration tests (category `Integration`), which need Docker. Playwright runs only in Finish and needs no change.

- [x] **1. CD32 and CDTV handlers** (A1, with `BrowserCore` null): the two handlers and their box art. `IngestSeeder.Cd32Dat`/`CdtvDat` are added to `RedumpLayouts` and `EmptyDats`, but the seeder doesn't write them yet. Not registered.
  - Files: `Cd32Handler.cs`, `CdtvHandler.cs`, the four box-art files, `Resources/boxart/README.md`, `tests/RnRomManager.Systems.Commodore.Tests/Cd32HandlerTests.cs` and `CdtvHandlerTests.cs` (new), `IngestSeeder.cs`.
  - Tests: `Cd32HandlerTests` and `CdtvHandlerTests` (new), copying `PcfxHandlerTests`:
    - every A1 value;
    - `ReadHeader` is null for any bytes;
    - `NormalizeForHashing` leaves the bytes unchanged;
    - the PNG is valid, 512×512 and different from `amiga.png` and from each other;
    - with a registry holding only that handler, `DatCache` loads a Redump fixture catalog with one synthetic cue/bin game and finds its `.bin` by SHA-1, and loads an empty catalog.
  - Done when: `dotnet test tests/RnRomManager.Systems.Commodore.Tests` passes and the solution builds with warnings as errors.
- [x] **2. Register CD32 and CDTV, and update fixtures** (A2, A3, and A12's rows with Play `—`):
  - The `AppHost.cs` lines, `IgdbPlatforms.cs`, `systemEmulators.json`, `app.css`, `systemColours.ts`, `types/SystemColour.ts`, `icons.ts` and the README rows.
  - `IngestSeeder`: the disc-folder loop and the `FixtureDatWriter.Write` entries (Changes table).
  - Every test file whose full-registry manifest lists `IngestSeeder.PcfxDat` adds `[IngestSeeder.Cd32Dat] = [], [IngestSeeder.CdtvDat] = []` after it. Today those are `AuditLibraryEditTests`, `DatEndpointsTests`, `GamePlayedEndpointTests`, `ImageEndpointsTests`, `ImageUrlEndpointsTests`, `IngestArchiveTests`, `IngestDeleteEndpointsTests`, `IngestEndpointsTests`, `IngestPipelineTests`, `IngestRestoreEndpointsTests`, `IngestReviewTests`, `LibraryEditTests`, `LibraryEndpointsTests`, `LibraryMoveMergeTests`, `MediaRetryEndpointsTests`, `NoteEndpointsTests`, `ReportEndpointsTests`, `ReviewEndpointsTests` and `ZipEntryEndpointsTests`. `PcfxHandlerTests` (a one-entry manifest) stays as it is. Before committing, grep `tests/` and check that every full manifest listing `IngestSeeder.PcfxDat` also lists both new DATs.
  - Counts and orders:
    - `DatEndpointsTests`: the names count +2, with its message's "the 10 disc systems" becoming "the 12 disc systems". The ordinal list gains `IngestSeeder.Cd32Dat` and `IngestSeeder.CdtvDat` where `Ordinal` puts them (after `IngestSeeder.AmigaDat`). Refresh requests +2.
    - `IngestSeederTests`: `.dat` files +2.
    - `SystemStartupTests`: both id lists end `…, "cd32", "cdtv"`. `IngestEndpointsTests.Pending_ListsEverySystem_IgnoringJunk` likewise.
    - `LibraryEndpointsTests`: the display-name-ordered id list gains `"cd32", "cdtv"` right after `"amiga"`, and the `displayName` list gains `"Commodore Amiga CD32", "Commodore CDTV"` right after `"Commodore Amiga"`. Two new asserts after the `pcfx` one, in its form: `Find("cd32")` → `("Commodore Amiga CD32", "CD32")` and `Find("cdtv")` → `("Commodore CDTV", "CDTV")`. The `embedRoms` true count is unchanged.
    - `IgdbPlatformsTests` needs no change (ids are distinct).
    - `systemColours.test.ts`: `expect(systemColour('cd32')).toEqual({ name: 'cd32', foreground: '#fff' });` and the same for `cdtv`, directly after the `amiga` line in `it('gives Commodore 64 its palette light blue with white text (C64 B5)', …)`.
    - `src/lib/stores/stores.svelte.test.ts`: `system-cd32` and `system-cdtv` → `'bi bi-disc'` after the `system-pcfx` line.
  - Tests: the files above, plus `SeedIngestTests` (counts unchanged), `SupportedSystemsTableTests`, `IgdbPlatformsTests` and `SystemEmulatorsCatalogTests`; on the web side, `npm run lint` and `npm run check`.
  - Done when: those pass (Docker running) and `docker build .` succeeds.
- [ ] **3. Verify CD32/CDTV ingest** (A4): Testcontainers cases in `IngestPipelineTests` with synthetic files (never real images), after the `Pcfx_…` cases. Two new fields after `FoxData`: `ChickAudio2 = new SyntheticDiscBuilder { Seed = 3201, Size = 32 * 1024 }.Build()` and `ChickData = new SyntheticDiscBuilder { Seed = 3202 }.Build()` (seeds unused in the class). The class manifest replaces Step 2's `[IngestSeeder.Cd32Dat] = []` with `[new FixtureGame("Disc Chick (Europe)", "Disc Chick (Europe) (Track 01).bin", ChickData)]`.
  - `Cd32_DatMatchedCueWithTwoBins_IsOneRelease_NamedByItsDataTrack` (new, copying `Pcfx_DatMatchedCueWithTwoBins_IsOneRelease_NamedByItsDataTrack`):
    - Input in `ingest/cd32/`: `Disc Chick (Europe).cue` + `Disc Chick (Europe) (Track 01).bin` (MODE1/2352, `ChickData`) + `Disc Chick (Europe) (Track 02).bin` (AUDIO, `ChickAudio2`).
    - All three are `Ingested` with `SystemId` `cd32`.
    - `roms/cd32/D/` holds the two tracks, the cue and `Disc Chick.zip`.
    - info.json has `system` `cd32`, `fileName` and `externalFileName` Track 01, method `Dat`, `header` null, and `files` the cue and Track 02.
  - `Chd_Lone_GoesToReviewAsNoDatMatch_WithItsFoldersSystem` gains `[TestCase("cd32")]` and `[TestCase("cdtv")]` after `pcfx`'s.
  - `Cd32_AdfInItsFolder_IsUnsupportedType_AndMovedToFailed` (new, copying `Pcfx_PceInItsFolder_IsUnsupportedType_AndMovedToFailed`) with an `.adf`.
  - `Cd32_DatMatchedCueSetInCdtvFolder_GoesToReviewAsThreeNoDatMatchItems` (new): the same three files in `ingest/cdtv/` give three `(Review, "NoDatMatch")` items with `SystemId` `cdtv`, asserted with `RunIngestJobWithItemsAsync`.
  - Done when: `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~IngestPipelineTests"` passes.
- [ ] **4. CD32/CDTV play-disc** (A5): `BrowserCore` stays null.
  - Files: `PlayDisc.cs`, `LibraryEndpointsTests.PlayDisc.cs`.
  - Tests (`LibraryEndpointsTests.PlayDisc`), with `Cd32Game` and `CdtvGame` helpers (new) beside `PcfxGame`, as `DiscGame(PlayDisc.Cd32SystemId, …)` / `CdtvSystemId`:
    - `PlayDisc_ACd32CueSet_IsAStoredZip_WithThePlaybackCue` (new, copying `PlayDisc_APcfxCueSet_IsAStoredZip_WithThePlaybackCue`).
    - `PlayDisc_AStandaloneChd_StreamsUnchanged` gains `[TestCase("cd32", "Chd Solo (Europe).chd")]`, `[TestCase("cdtv", "Chd Solo (Europe).chd")]`, `[TestCase("cd32", "Chd Solo (Europe).iso")]` and `[TestCase("cdtv", "Chd Solo (Europe).iso")]` after its `pcfx` case (it already streams the `saturn` `.iso` case byte for byte).
    - `PlayDisc_ACd32Nrg_StreamsACueAndTrack01WithoutTheFooter` (new, copying one case of `PlayDisc_ASaturnNrg_StreamsACueAndTrack01WithoutTheFooter` with `Cd32Game`).
    - `PlayDisc_AnAmigaCdSingleFileThatCantPlay_Is409InvalidPlayDisc(string system, string name)` (new; cases `("cd32", "Solo (Europe).bin")`, `("cd32", "Solo (Europe).BIN")`, `("cd32", "Solo (Europe).img")`, `("cdtv", "Solo (Europe).bin")`): asserts code `InvalidPlayDisc` and A5's exact `title` for that system.
    - `PlayDisc_ATocSet_IsTheGeneratedCueAndTheAliasedBins_AndNoToc` gains `[TestCase("cd32")]` after `[TestCase("pcfx")]`.
    - `PlayDisc_ACd32CloneCdDisc_Plays` (new, copying `PlayDisc_APcfxCloneCdDisc_Plays` with `PlayDisc.Cd32SystemId`).
    - Every existing case passes unchanged.
    - `ShapeProblem`'s doc comment gains "CD32 A5: Amiga CD32 and CDTV take a cue, TOC, CloneCD or MDS set, or a single `.iso`, `.chd` or `.nrg`."
  - Done when: `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~LibraryEndpointsTests|FullyQualifiedName~PlayDiscCopyTests"` passes.
- [ ] **5. BIOS Info** (A11): the three catalog groups and the CD32 extended-ROM rule in `BiosInfo.Check`.
  - Files: `BiosInfo.cs`, `tests/RnRomManager.Api.Tests/Integration/EmulatorEndpointsTests.BiosInfo.cs`.
  - Tests (`EmulatorEndpointsTests.BiosInfo`):
    - `BiosInfo_reports_valid_wrong_size_missing_and_other_files` adds: `Group(info, "Commodore Amiga CD32")` `ok` false (absent); `"Commodore Amiga CD32 (extended ROM)"` `ok` true (no Kickstart, so not needed); `"Commodore CDTV"` `ok` false.
    - `BiosInfo_amiga_cd_kickstarts` (new): a 512 KiB `kick40060.CD32` alone → CD32 group ok, extended-ROM group not ok (`kick40060.CD32.ext` `Missing`); adding a 512 KiB `kick40060.CD32.ext` → both ok; replacing the Kickstart with a 1 MiB one and deleting the `.ext` → both ok; 256 KiB `kick34005.A500` with a 128 KiB `kick34005.CDTV` → `kick34005.CDTV` `WrongSize`, CDTV group not ok.
  - Done when: `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~EmulatorEndpointsTests"` passes (Docker running).
- [ ] **6. Play page and `BrowserCore`** (A6's `parsePlayParams` part, A7, A8, A9, A1's `BrowserCore`, and A12's Play cells). Nothing produces a CD32/CDTV play link yet: every release is external, and `puae` isn't in the external core sets until Step 7.
  - Files: `params.js`, `play.js`, `Cd32Handler.cs`, `CdtvHandler.cs`, `README.md` (Play cells), `BrowserCoreExtensionsTests.cs`, and the vitest files below.
  - Tests:
    - `Cd32HandlerTests`/`CdtvHandlerTests`: `BrowserCore` is `puae`. `SupportedSystemsTableTests`.
    - `BrowserCoreExtensionsTests`:
      - `Exceptions` gains, for `cd32` then `cdtv` after the `pcfx` entry of each group:
        - `.bin` with `pcfx`'s reason, `"a cue set's data track; a lone .bin never plays (canPlay, play-disc)"`;
        - `.img` and `.sub` with `CloneCdMember`;
        - `.mdf` and `.toc` with `MdsTocMember`.
      - PUAE lists `cue`, `nrg`, `chd`, `iso`, `ccd` and `mds`, so those need no entry.
      - The stale-exception list in `ProblemsAreNamed` gains the matching `"cd32 .<ext>: stale exception (no such system)"` and `"cdtv …"` lines in the same order.
      - `NotIngested` drops the `(NewSystem, ["puae"], [".cue", ".ccd", ".nrg", ".mds", ".iso", ".chd"])` group, which CD32 now ingests. `EveryCoreExtension_IsIngestedOrListed` passes without it.
    - `emulatorParams.test.ts`:
      - `parsePlayParams` accepts `system=cd32` and `system=cdtv` with `core=puae&disc=1` (`.system` set), and returns null for:
        - `system` without `disc`;
        - with `set=1`;
        - with `core=pcsx_rearmed`;
        - repeated;
        - `system=amiga`;
        - `system=CD32` (case).
      - Existing links give `system` null.
      - `discError(500, 'IoError', 'puae', false)` is `"Couldn't load the disc (HTTP 500)."`, and `discError(409, 'InvalidPlayDisc', 'puae')` is the existing disc message, `"This disc can't be played in the browser: its cue sheet or files are invalid or don't match."`.
      - `cdtvBiosSizeError('kick34005.CDTV')` returns A7's text.
    - `emulatorPlay.test.ts`: a new `describe('play page Amiga CD32 and CDTV (CD32 A7, A8)')` after `describe('play page Amiga disk sets (Amiga F7)')`, with a local helper `playAmigaCd(system: 'cd32' | 'cdtv')` (new, beside `playDisks`, as it is built) that sets `game`, `rom` `12`, `core` `puae`, `name` `Disc Chick (Europe) (Track 01).bin`, `disc` `1` and `system`, then awaits `started` and returns the status (`play(core, name, disc)` can't add `system`); the play-disc URL is `/api/games/${game}/roms/12/play-disc`. Cases:
      - CD32 with a 512 KiB `kick40060.CD32` and a 512 KiB `.ext`: requests go `kick40060.CD32` → `kick40060.CD32.ext` → play-disc. `EJS_externalFiles` has both keys. `EJS_defaultOptions.puae_model` is `'CD32'`. `EJS_hideSettings` is `['puae_model']`. The status shows `Downloading disc…`.
      - CD32 with a 1 MiB `kick40060.CD32`: no `.ext` request.
      - CD32: a 404 on `kick40060.CD32` shows `cd32BiosMissing`; a 404 on `.ext` (after a 512 KiB Kickstart) shows `cd32ExtMissing`; a 1 MiB + 1 byte Kickstart shows `cd32KickstartSizeError`. None of these makes a play-disc or loader request.
      - CDTV: requests go `kick34005.A500` → `kick34005.CDTV` → play-disc, and `puae_model` is `'CDTV'`. A 404 on either shows `cdtvBiosMissing`. A 128 KiB `kick34005.CDTV` shows `cdtvBiosSizeError('kick34005.CDTV')`.
      - The existing Amiga disk-set cases pass unchanged: no Kickstart request, `Downloading disks…`, no `puae_model`.
  - Done when: `npm run lint`, `npm run check`, `npx vitest run src/lib/emulatorParams.test.ts src/lib/emulatorPlay.test.ts` and the `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests` and `dotnet test tests/RnRomManager.Systems.Commodore.Tests` runs pass.
- [ ] **7. Enable CD32/CDTV Play** (A6's `playUrl` part, A10): the API's and SPA's external and play-disc core sets, the lone `.bin` rule, `playUrl`'s `system`, and its callers.
  - Files: `LibraryEndpoints.cs`, `LibraryDtos.cs`, `emulator.ts`, `EmulatorModal.svelte`, `GameResults.svelte`, `GameSaves.svelte`, `routes/games/[id]/+page.svelte`, and the tests below.
  - Tests:
    - `LibraryEndpointsTests`:
      - `GameDetail_BrowserCore_IsTheSystemHandlers` gains `[TestCase("cd32", "puae")]` and `[TestCase("cdtv", "puae")]` after `pcfx`'s.
      - `Games_QuickPlay_IsThePrimaryReleaseWhenPlayable` gains `[TestCase("cd32", true, true, "puae")]` after `pcfx`'s.
      - `Games_QuickPlay_DiscOnlyCores_ALoneBinIsNotPlayable` gains its four cases for `("cd32", "puae")` after the `pcfx` ones.
      - The existing `[TestCase("amiga", false, true, "puae")]` and the `.ipf` case calling `AssertQuickPlayAsync("amiga", …)` pass unchanged.
    - `emulator.test.ts`:
      - `describe('canPlay on puae (CD32 A10)')` after `describe('canPlay on mednafen_pcfx (PC-FX X6)')`: an external set `.bin` with `files` → true; an external `.chd` and `.iso` → true; an external `.BIN` with `files` null or `[]` → false; an embedded `.adf` → true and an embedded `.ipf` → false (unchanged).
      - The `'usesPlayDisc is true for an external %s release'` list gains `'puae'` last.
      - In `describe('playUrl')`, a new case: `playUrl('g-8', { id: 2, fileName: 'Disc Chick (Europe) (Track 01).bin' }, 'puae', true, null, null, 'cd32')` is `/emulator/play.html?game=g-8&rom=2&core=puae&name=Disc%20Chick%20(Europe)%20(Track%2001).bin&disc=1&system=cd32`. Also `system` `'amiga'` or null adds nothing.
    - `EmulatorModal.test.ts`: the iframe `src` carries `&system=cdtv` when mounted with `system="cdtv"`, `disc` true and core `puae`.
  - Done when: those pass, and `npm run lint` and `npm run check` pass.
- [ ] **8. Docs**:
  - `README.md`: A12's intro, Play, upgrade-note and attribution items (the rows are already in).
  - `docs/features.md`:
    - The systems list (line 3) gains both names after `PC-FX`, and so does the **Play** list.
    - A Play sub-bullet after PC-FX's: "**Commodore Amiga CD32** and **Commodore CDTV** use PUAE (`puae`), which ships in the image. One disc per launch, up to 2 GiB: a cue, TOC, CloneCD or MDS set, an `.iso`, a `.chd` or an `.nrg`; a lone `.bin` doesn't play. CD32 needs `kick40060.CD32` (512 KiB, with the 512 KiB `kick40060.CD32.ext`, or the 1 MiB combined ROM) and CDTV needs `kick34005.A500` and `kick34005.CDTV` (256 KiB each) in the BIOS folder (`Paths:Bios`); the play page names a missing file. Multi-disc sets don't play as one session, and game saves stay in the browser."
    - The ingestion folder list and the drop-files paragraph gain `ingest/cd32/` and `ingest/cdtv/` (A1's formats, 16 bytes–1 GiB per file).
    - The Redump DAT sentence, the **Sets** bullet and the **CHD** bullet gain CD32 and CDTV after PC-FX.
    - The `play-disc` API bullet gains them in its system lists, with "; CD32 and CDTV: a single `.iso`, `.chd` or `.nrg`".
  - `docs/deployment.md`:
    - The `/app/ingest` row gains `` `ingest/cd32/` and `ingest/cdtv/` (Amiga CD32 and CDTV: a `.cue` with its `.bin` tracks, `.iso`, `.chd` or `.nrg`) `` after the `ingest/pcfx/` entry.
    - The `/app/bios` row gains `` ; `kick40060.CD32` (512 KiB with `kick40060.CD32.ext`, or 1 MiB combined) for CD32 and `kick34005.A500` + `kick34005.CDTV` (256 KiB each) for CDTV, required `` after the PC-FX entry.
    - Under **Upgrades**, a block after "Upgrading to the release that adds PC-FX:", laid out like it: "Upgrading to the release that adds Amiga CD32 and CDTV:", with "**Refresh DATs after upgrading.** CD32 and CDTV discs are identified against libretro's Redump `Commodore - CD32` and `Commodore - CDTV` DATs (no metadat). Until **Admin → DATs → Refresh DATs** has downloaded them, every import is refused with `DatsMissing`. `ingest/cd32/` and `ingest/cdtv/` are created at startup." and "Browser play needs the Kickstart ROMs above in `data/bios/`; PUAE already ships in the image."
    - The disc-images sentence in the backup section gains them after PC-FX.
  - `docs/configuration.md`: the `Paths:Bios` row gains the same Kickstart clause before "; for Arcade (FinalBurn Neo)".
  - `docs/zip-format.md`:
    - `  cd32/` and `  cdtv/` layout lines after `pcfx/`, with the same padding and the wording "Amiga CD32 (external, exact names: a .cue or .toc with its .bin tracks, a CloneCD .ccd/.img/.sub, an MDS .mds/.mdf, or a .chd, .iso or .nrg)" (and "Commodore CDTV" for `cdtv/`).
    - `cd32` and `cdtv` in the `system` id list.
    - Both names in the "`header`: always null" sentence, and in the line-5 list of external systems.
  - `docs/adding-a-system.md`:
    - The disc-systems paragraph gains "Amiga CD32 and CDTV in `.Commodore`".
    - The `.chd` sentence gains them.
    - The `BrowserCore` row gains "`cd32` and `cdtv` return `puae` (also the Amiga floppy core; the play URL's `system` tells the play page which Kickstarts and model to use)", and `puae` joins the external/play-disc core list.
    - The box-art list gains both.
  - `docs/development.md` (Browser emulators):
    - The **Play page** bullet's `parsePlayParams` list gains "an optional `system` (`cd32` or `cdtv`, only with `puae` and `disc=1`, no `set`)".
    - The **`disc` flag** bullet gains "Amiga CD32 and CDTV on `puae`".
    - The **Multi-disc sets** bullet's `playUrl(gameId, rom, core, disc, save, setName)` becomes `playUrl(gameId, rom, core, disc, save, setName, system)`, with "; `system` adds `&system=cd32|cdtv` for an Amiga CD launch".
    - The **Multi-disc sets** bullet's sentence about floppy wording gains "(only with `set`; a single CD32/CDTV disc keeps disc wording)".
    - A new bullet after **PC-FX**: "**Amiga CD32 and CDTV (`puae` with `disc` and `system`):** PUAE plays these discs from `play-disc`. Before the disc, the play page loads the Kickstart ROMs through `loadOptionalBios` and hands them over in `EJS_externalFiles` at the content root, which is PUAE's system directory, since EmulatorJS sets none. For CD32, `kick40060.CD32` (1 byte–1 MiB) is loaded first, then `kick40060.CD32.ext` (exactly 512 KiB) when the Kickstart is 512 KiB or less: PUAE uses a larger one as the combined ROM. For CDTV, `kick34005.A500` and `kick34005.CDTV` are loaded (exactly 256 KiB each). The page sets `EJS_defaultOptions.puae_model` to `CD32` or `CDTV` and hides `puae_model`, since on `Automatic` PUAE picks CDTV only from a path containing `CDTV`. A lone `.bin` never gets a Play button: PUAE is on EmulatorJS's disable-cue list."
  - Done when: every item is in its doc, `SupportedSystemsTableTests` passes and `pwsh scripts/codemap.ps1` is clean.

## Decisions
- 2026-11-27 Add CD32 and CDTV after all? → Yes, both, as two disc systems on PUAE (user; reverses 2026-11-23's "Not Amiga CD32")
- 2026-11-27 Ids, names and project → `cd32` "Commodore Amiga CD32" / `CD32` and `cdtv` "Commodore CDTV" / `CDTV`, in `RnRomManager.Systems.Commodore`, each its own family; the names sort next to "Commodore Amiga" (code)
- 2026-11-27 Formats → Saturn's list (`.bin`, `.cue`, `.nrg`, `.chd`, `.iso`, `.ccd`, `.img`, `.sub`, `.mds`, `.mdf`, `.toc`): PUAE opens `cue|ccd|nrg|mds|iso|chd`, play-disc turns TOC/CloneCD/MDS/NRG into a cue (code)
- 2026-11-27 Lone `.bin` → not playable and refused by play-disc: EmulatorJS 4.2.3 disables cue generation for `puae` and PUAE doesn't list `bin` (code)
- 2026-11-27 Kickstart names and sizes → libretro-uae `2245d34` `uae_kickstarts`: CD32 `kick40060.CD32` (+ `kick40060.CD32.ext` when ≤ 512 KiB), CDTV `kick34005.A500` + `kick34005.CDTV`; searched in the system directory, which is the content root under EmulatorJS (code)
- 2026-11-27 How the play page knows the system → a `system` query param (`cd32`|`cdtv`, `puae` + `disc` only), added by `playUrl` from `EmulatorModal`'s new `system` prop (`game.system`); the new-tab links never serve `puae`; the release name and core can't tell CD32 from CDTV, and fetching the game from `play.js` would add a request and new error text (code)
- 2026-11-27 Model → `EJS_defaultOptions.puae_model` explicit and `puae_model` hidden: `Automatic` defaults every CD to CD32 and picks CDTV only from a path containing `CDTV` (code)
- 2026-11-27 Disk wording → only for sets, so a CD32/CDTV disc gets disc wording without knowing the system (code)
- 2026-11-27 DAT facts → CD32 205 games, CDTV 65, one `.bin` each, largest 643,311,984 / 767,163,600; IGDB 114 / 158; libretro-thumbnails folders exist (code: read 2026-11-27)
- 2026-11-27 Independent review → R1 DAT order is `Commodore - 64`, Amiga, CD32, CDTV, PET; R2 DatEndpointsTests message 10 → 12; R3 README PUAE line already says no Kickstart is included; R4 named `playAmigaCd` helper; R5 BIOS Info's extended-ROM group follows the Kickstart's size; R6 BIOS Info is its own Step 5 (Steps renumbered to 8); R7 README CloneCD/MDS/TOC sentence and development.md's `playUrl` signature (reviewer)
- 2026-11-27 Review → self-reviewed against the code (this fork may not start a Plan reviewer): the new-tab `playUrl` callers need no `system`; the quick-play and core tests already hold Amiga floppy cases; the DAT ordinal order pinned; counts given as deltas because parallel system specs move them (reviewer)

- 2026-11-27 Multi-disc sets for CD32/CDTV? → Out of scope; each disc plays alone (user)
- 2026-11-27 Deep review → handlers now end with `SegaPicoHandler`; BIOS Info extended-ROM `ok` rule fixed for a missing Kickstart (null `Size`); `.iso` play-disc cases folded into `PlayDisc_AStandaloneChd_StreamsUnchanged`; `systemEmulators.json` entry order, doc-comment and README clause placement, `BrowserCore` comment text and the `discError` text pinned; stale parallel-spec list updated (reviewer)
- 2026-11-27 Step 2 → the colour names also need the `SystemColour` union in `types/SystemColour.ts`, or `npm run check` fails (code)

## Kickoff prompt
```text
/spec implement 2026-11-27-amiga-cd32-cdtv.md
```
Starts the spec, or resumes it at the first unticked step.
