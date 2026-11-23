---
title: Core file type parity
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-23
---

# Core file type parity

## Summary
An audit of every system's ingest types against its browser core's `valid_extensions` (the lists in `BrowserCoreExtensionsTests.CoreExtensions`). One direction is already enforced: every type a system ingests is opened by its core (`EveryHandlerExtension_IsOpenedByItsBrowserCore`). This spec closes the main gaps in the other direction, where a core opens a type that ingest refuses:
- **Cartridge aliases:** Nintendo 64 `.bin`/`.u1`, Virtual Boy `.bin`, and Master System and Game Gear `.bin`/`.rom`.
- **VICE extras:** gzipped VICE images (`.d6z`, `.d7z`, `.d8z`, `.g6z`, `.x6z`, `.gz`) for C64, PET and VIC-20. They are inflated like Amiga `.adz`, so they DAT-match. C64 also gets `.nbz`, `.d2m`, `.d4m` and `.tcrt`.

A new test then makes the audit permanent. Every type a core opens must be ingested by a system on that core, or be listed with a reason.

## Out of scope
- Nintendo DS `.bin`: the DS DAT's 23 `.bin` entries are save data and seminar demos, which `DatStore` would start matching (D1).
- VICE `.g41`/`.g4z` (no VICE documentation for them), `.cmd`/`.vfl` (a command file and a fliplist, not game images), `.vsf` (a snapshot; File type gaps decided this), `.zip`/`.7z` and `.m3u`.
- `.d2m`/`.d4m`/`.tcrt` on PET and VIC-20, and `.nbz` on PET and VIC-20 (neither ingests `.nib`).
- New systems: SuperGrafx (`.sgx`), Amiga CD32/CDTV (PUAE's disc types), Sega Pico (`.pco`).
- Types File type gaps left out (NES `.unf`/`.unif`, PlayStation `.exe`, Amiga `.hdf`/`.lha`/`.fdi`/`.raw`/`.dms`) and other homebrew or unusual types (Lynx `.o`, PSP `.elf`/`.prx`, 7800 `.cdf`, Mega Drive `.mdx`/`.bms`/`.68k`/`.sgd`, PUAE `.hdz`/`.slave`/`.info`/`.uae`, Super Game Boy `.sgb`). P2 lists each one with its reason.
- No SPA, API, play page or Playwright change: these systems already play in-ZIP files whatever their stored name.

## Behaviour

### Cartridge aliases
- **A1** These `RomExtensions` gain the listed entries, appended so `RomExtensions[0]` doesn't change:
  - `n64`: `.z64`, `.v64`, `.n64`, `.bin`, `.u1`
  - `vb`: `.vb`, `.vboy`, `.bin`
  - `sms`: `.sms`, `.sg`, `.bin`, `.rom`
  - `gg`: `.gg`, `.sms`, `.bin`, `.rom`

  Nothing else in the handlers changes:
  - Size limits, headers and normalisation stay as they are. N64 still picks the byte order from the first bytes, so a `.bin` or `.u1` in any order is stored big-endian.
  - A hash-matched file takes the DAT ROM's name. A Review resolve takes `RomExtensions[0]` (`.z64`, `.vb`, `.sms`, `.gg`), as today. So a Game Gear game is never stored as `.bin`, and SMS Plus GX's `.gg` check in `params.js` still picks Game Gear mode.
  - The folder still decides the system (`ingest/n64/` and so on).
- **A2** `DatStore` keeps only DAT entries whose extension is in `RomExtensions`. Adding `.bin` to `n64` therefore loads the No-Intro N64 DAT's six `.bin` entries, all cheat-cartridge BIOS dumps (GameShark Pro, Action Replay Pro 64, GameBooster 64, Xplorer 64). A matching dump is ingested as a game under its DAT name (`… (Unl).bin`) and gets a **Play** button, like other matched files (D2). The VB, SMS and GG DATs have no `.bin` or `.rom` entries.

### VICE images
- **V1** `RomExtensions` gain these entries, appended in this order:
  - `c64`: `.d6z`, `.d7z`, `.d8z`, `.g6z`, `.x6z`, `.gz`, `.nbz`, `.d2m`, `.d4m`, `.tcrt`
  - `pet`: `.d6z`, `.d7z`, `.d8z`, `.g6z`, `.x6z`, `.gz`
  - `vic20`: `.d6z`, `.d7z`, `.d8z`, `.g6z`, `.x6z`, `.gz`

  Size limits are unchanged and checked on the file as dropped: C64 1 KiB–8 MiB, PET and VIC-20 256 bytes–8 MiB. A CMD `.d4m` (3,317,760 bytes) fits.
- **V2** Gzip handling. Amiga's inflate code moves into a new `internal static class GzipImage` in `RnRomManager.Systems.Commodore`, with `public static ReadOnlyMemory<byte> Inflate(ReadOnlyMemory<byte> rom, long maxBytes)`. It keeps `AmigaHandler.NormalizeForHashing`'s current behaviour:
  - Input without the `1F 8B` magic is returned unchanged.
  - A valid stream is inflated. If the output passes `maxBytes`, or the stream is corrupt (`InvalidDataException`), the input is returned unchanged.

  `AmigaHandler.NormalizeForHashing` becomes `GzipImage.Inflate(rom, MaxRomSizeBytes)`. `C64Handler`, `PetHandler` and `Vic20Handler` gain the same `NormalizeForHashing`. A gzipped image is then hashed against the DAT, header-read and stored inflated. An existing `.d64` that matches the same game is a `Duplicate`. `.nbz` is NIBTools' own LZ format, not gzip, so it's stored as dropped.
- **V3** New default interface member on `ISystemHandler`, right after `KeepsExtension`, documented as "The extension the stored bytes of a source named `fileName` have; null when the handler can't tell, and ingest fails the file `UnsupportedType`":

  ```csharp
  string? SourceExtension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant();
  ```

  `C64Handler`, `PetHandler` and `Vic20Handler` override it through a new shared `internal static class ViceImages` (tested through the handlers, as `C64Header` is; no `InternalsVisibleTo`) (`public static string? SourceExtension(string fileName, IReadOnlyList<string> romExtensions)`):
  - `.d6z` → `.d64`, `.d7z` → `.d71`, `.d8z` → `.d81`, `.g6z` → `.g64`, `.x6z` → `.x64`. libretro-vice treats all of these as floppies (`libretro-dc.c`), and VICE probes the image type, so a gzipped `.d80`/`.d82` stored as `.d81` still attaches.
  - `.gz` → the extension before `.gz` (`Game.d64.gz` → `.d64`), lower-cased, when it's one of `romExtensions` other than `.gz` and the five above. Otherwise the result is null.
  - Any other extension → itself, lower-cased.
- **V4** Ingest (`IngestPipeline.ProcessCoreAsync`): right after the `byExtension is null` check, a null `byExtension.SourceExtension(file.Name)` fails the file as `UnsupportedType`, with the details `'<file.Name>' doesn't say its image type (name it like 'Game.d64.gz')`. This covers loose files and archive entries. A `.gz` that names its type continues as V2.
- **V5** Review (`ReviewResolver`, where `originalExtension` is set today): `originalExtension = system.SourceExtension(item.OriginalFileName) ?? Path.GetExtension(item.OriginalFileName).ToLowerInvariant()`. Its uses are unchanged: the DAT-rom pick by extension, the FileSets and plain Dat filenames, and the Attach and Manual filenames. The Commodore handlers keep `KeepsSourceExtension` true, so a resolved `Game.d6z` is stored as `<title>.d64`, and `Game.d64.gz` as `<title>.d64`. Review suggestions still come from the file name; `Game.d64.gz` may suggest the title `Game.d64`, which the Admin edits (Risks).
- **V6** A corrupt or oversized gzip isn't inflated (V2). It goes to Review and is stored under its mapped name (for example `.d64`) with its compressed bytes. That is the same as a corrupt Amiga `.adz` stored as `.adf`. VICE may not open it.

### Parity guard
- **P1** `BrowserCoreExtensionsTests` gains `EveryCoreExtension_IsIngestedOrListed`. For each core in `CoreExtensions`, every extension it lists must be in the `RomExtensions` of at least one handler whose `BrowserCore` is that core, or be a key of a new `NotIngested` dictionary (`(string Core, string Extension)` → reason). A stale entry fails too: the core doesn't list that extension any more, or a handler on that core now ingests it. The check sits in a new `UningestedProblems(IEnumerable<ISystemHandler> handlers, IReadOnlyDictionary<string, string>? cores = null, IReadOnlyDictionary<(string Core, string Extension), string>? notIngested = null)` (null → `CoreExtensions` / `NotIngested`) that yields these messages:
  - `<core>: no system ingests .<ext>`
  - `<core> .<ext>: stale entry (<core> doesn't list it)`
  - `<core> .<ext>: stale entry (<system id> ingests it)`

  A new `UningestedProblemsAreNamed` test passes `cores` = `{gambatte: "gb|gbc|dmg", yabause: "cue|zip"}`, substitute handlers `Handler("gb", "gambatte", ".gb", ".gbc")` and `Handler("saturn", "yabause", ".cue")`, and `notIngested` = `{(gambatte, .gbc): "x", (yabause, .iso): "x"}`, and expects exactly (in any order): `gambatte: no system ingests .dmg`, `gambatte .gbc: stale entry (gb ingests it)`, `yabause: no system ingests .zip`, `yabause .iso: stale entry (yabause doesn't list it)`.
- **P2** `NotIngested` holds exactly these entries, with these shared reason constants:
  - `Archive` = "ingest unpacks .zip and .7z; the core never gets one": `puae`, `yabause`, `vice_x64sc`, `vice_xpet`, `vice_xvic` × `.zip`, `.7z` (yabause `.zip` only).
  - `Playlist` = "the play page builds its own .m3u for a disc or disk set": `.m3u` on `mednafen_pce`, `pcsx_rearmed`, `yabause`, `puae`, `genesis_plus_gx`, `picodrive`, `vice_x64sc`, `vice_xpet`, `vice_xvic`.
  - `OtherSystem` = "another system's type, ingested on that system's core": `mgba` `.gb`, `.gbc`; `genesis_plus_gx` `.sms`, `.gg`, `.sg`; `picodrive` `.gen`, `.smd`, `.md`, `.cue`, `.iso`, `.chd`, `.sms`, `.gg`, `.sg`; `smsplus` `.col`.
  - `NewSystem` = "would be a new system": `mednafen_pce` `.sgx` (SuperGrafx); `picodrive` `.sc` (SC-3000); `puae` `.cue`, `.ccd`, `.nrg`, `.mds`, `.iso`, `.chd` (CD32/CDTV); `picodrive` `.pco` (Pico).
  - `NotAGame` = "not a game image (a snapshot, config, command, fliplist or icon file)": `vice_*` `.vsf`, `.cmd`, `.vfl`; `puae` `.uae`, `.info`, `.slave`.
  - `NoDat` = "homebrew or unusual type no DAT lists; left out by the 2026-11-23 audit": `fceumm` `.unf`, `.unif`; `pcsx_rearmed` `.exe`; `puae` `.hdf`, `.hdz`, `.lha`, `.fdi`, `.raw`, `.dms`; `handy` `.o`; `ppsspp` `.elf`, `.prx`; `prosystem` `.cdf`; `mgba` `.sgb`; `genesis_plus_gx` `.mdx`, `.bms`, `.68k`, `.sgd`; `picodrive` `.68k`, `.sgd`; `vice_*` `.g41`, `.g4z`.
  - `desmume` `.bin`: "the DS DAT's .bin entries are save data and seminar demos, which DatStore would match (D1)".
  - `OtherCommodore` = "ingested only on another Commodore system here (the PET has no cartridge port; NIB, D2M/D4M and 8050/8250 images aren't taken for this system)": `vice_xpet` `.crt`, `.bin`, `.tcrt`, `.nib`, `.nbz`, `.d2m`, `.d4m`; `vice_xvic` `.nib`, `.nbz`, `.d2m`, `.d4m`, `.d80`, `.d82`.

  `vice_*` means one entry for each of `vice_x64sc`, `vice_xpet` and `vice_xvic`. The implementer regenerates the list from the code. Any difference from this list is a spec error to raise, not something to silently match.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Systems/ISystemHandler.cs` | `SourceExtension` default member (V3) |
| Core | `src/RnRomManager.Core/Ingest/IngestPipeline.cs` | null `SourceExtension` → `UnsupportedType` (V4) |
| Core | `src/RnRomManager.Core/Ingest/ReviewResolver.cs` | `originalExtension` from `SourceExtension` (V5) |
| Systems | `src/RnRomManager.Systems.Nintendo64/Nintendo64Handler.cs`, `src/RnRomManager.Systems.VirtualBoy/VirtualBoyHandler.cs`, `src/RnRomManager.Systems.Sega/MasterSystemHandler.cs`, `src/RnRomManager.Systems.Sega/GameGearHandler.cs` | A1 extensions; class comments that list formats name the new ones |
| Systems | `src/RnRomManager.Systems.Commodore/GzipImage.cs` (new), `ViceImages.cs` (new), `AmigaHandler.cs`, `C64Handler.cs`, `PetHandler.cs`, `Vic20Handler.cs` | V1–V3 |
| Tests | `tests/RnRomManager.Api.Tests/BrowserCoreExtensionsTests.cs` | P1, P2 |
| Docs | `README.md` (supported systems table, checked by `SupportedSystemsTableTests`), `docs/features.md` § Ingestion, `docs/adding-a-system.md` (interface excerpt and member table) | new types; the `.bin` folder list gains `ingest/n64/`, `ingest/vb/`, `ingest/sms/`, `ingest/gg/`; after the excerpt's `KeepsSourceExtension` line, `string? SourceExtension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant();  // the stored bytes' extension; null → UnsupportedType`, and a `SourceExtension` row after the table's `KeepsSourceExtension` row (V3's wording, plus "C64, PET and VIC-20 map gzipped VICE images to the image they hold") |
| Docs | `docs/zip-format.md` (folder list) | `n64/` becomes `Nintendo 64 (always stored big-endian, as .z64; a matched cheat-cartridge .bin keeps its DAT name)`; `c64/` lists every stored type: `.crt, .tap, .nib, .d64, .g64, .t64, .prg, .bin, .d71, .d81, .x64, .p00, .d80, .d82, .nbz, .d2m, .d4m, .tcrt` (gzipped images are stored inflated, so PET and VIC-20 are unchanged) |
| Docs | `docs/deployment.md` (`/app/ingest` volume row) | `ingest/n64/` lists `.z64`, `.v64`, `.n64`, `.bin` or `.u1`; `ingest/gg/` `.gg`, `.sms`, `.bin` or `.rom`; `ingest/sms/` `.sms`, `.sg`, `.bin` or `.rom`; the three Commodore entries become "(Commodore 64: `.crt`, `.tap`, `.nib`, `.nbz`, `.d64`, `.g64`, `.d2m`, `.d4m`, `.t64`, `.prg`, `.tcrt` or `.bin`, or a gzipped disk image: `.d6z`, `.d7z`, `.d8z`, `.g6z`, `.x6z` or `<name>.d64.gz`)", "(Commodore PET: `.prg` or `.d64`, or a gzipped disk image: …)" and "(Commodore VIC-20: `.a0`, `.20`, `.40`, `.60` or `.bin`, or a gzipped disk image: …)", where … is C64's gzipped list |

No migration, config key or API contract change. Existing library files are untouched.

## Risks
- **Gzip magic on any Commodore file:** a `.prg` whose load address is `$8B1F` starts with `1F 8B`. It is inflated only if it's also a valid gzip stream; otherwise V2 returns it unchanged. That case is practically impossible.
- **Tiny compressed images:** a gzipped, nearly empty C64 disk can be under 1 KiB and fails `TooSmall`. This is accepted: real games compress to more.
- **N64 cheat carts** become matchable games that don't play alone (D2).
- **Review title from `Game.d64.gz`:** the stem keeps `.d64`. A Manual resolve's suggested title can be edited; an Attach as Release stores `Game.d64 (Unverified).d64` (its stem comes from the file name). Accepted: renaming the file to `Game.d6z` avoids it, and the docs say so.

## Steps
- [x] **1. Cartridge aliases**: A1, A2. Files: the four handlers, `README.md`, `docs/features.md`, `docs/zip-format.md` (`n64/` line), `docs/deployment.md` (`n64`, `gg`, `sms` entries). Tests: `Nintendo64HandlerTests`, `VirtualBoyHandlerTests`, `MasterSystemHandlerTests`, `GameGearHandlerTests` (each already asserts its exact `RomExtensions`; update it to A1), `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests`. Done when: the four lists match A1 exactly; the README rows, the features.md `.bin` folder list and the deployment.md entries name the new types; features.md and zip-format.md say a matched N64 `.bin` keeps its DAT name (A2); and the named tests pass.
- [x] **2. Source extension hook**: the V3 default member, V4, V5. Files: `ISystemHandler.cs`, `IngestPipeline.cs`, `ReviewResolver.cs`, `docs/adding-a-system.md`. Tests: `SystemHandlerDefaultsTests` (new `SourceExtension_WithoutOverride_IsTheLowerCasedExtension`, on the class's real `Plain` handler); `ReviewResolverTests` (the private `Headerless` stub gains `public Func<string, string?>? MapSourceExtension { get; init; }` and `public string? SourceExtension(string fileName) => MapSourceExtension?.Invoke(fileName) ?? Path.GetExtension(fileName).ToLowerInvariant();`; new `Manual_StoresTheExtensionSourceExtensionNames`: `new Headerless(".aaa", ".bbb", ".bbz") { KeepsSourceExtension = true, MapSourceExtension = _ => ".bbb" }` with a pending `test.bbz` stores `<title>.bbb` on Manual); `IngestJobTests` (new `NullSourceExtension_FailsUnsupportedType`: a substitute handler whose `SourceExtension(Arg.Any<string>())` returns null fails the file `UnsupportedType` with V4's exact details. NSubstitute's auto value for a `string?` member is `""`, not null, so the existing substitute handlers keep passing V4 and need no change). Done when: those pass, every existing `ReviewResolverTests` still passes, and adding-a-system.md has the excerpt line and the table row.
- [x] **3. VICE images**: V1, V2, the V3 overrides, V6. Files: `GzipImage.cs` (new), `ViceImages.cs` (new), the four Commodore handlers, `README.md`, `docs/features.md`, `docs/zip-format.md` (`c64/` line), `docs/deployment.md` (`c64`, `pet`, `vic20` entries). Tests:
  - `C64HandlerTests`, `PetHandlerTests`, `Vic20HandlerTests`: V1 lists; `SourceExtension` covers every V3 mapping, `.gz` with a known, unknown and missing inner type, and upper case; `NormalizeForHashing` inflates a gzipped image and keeps a plain one (in C64, `NormalizeForHashing_ReturnsTheInputUnchanged` becomes `NormalizeForHashing_InflatesAGzip_AndKeepsAPlainImage`, as `AmigaHandlerTests`' pair does).
  - `AmigaHandlerTests`: unchanged, and still passing.
  - `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests`.
  - `IngestPipelineTests` (integration). The class's `[IngestSeeder.C64Dat]` is empty today; it gains `new FixtureGame("Gzip Orbit (Europe)", "Gzip Orbit (Europe).d64", C64Disk)` with a new `C64Disk = Atari(174_848, 6401)` (a 35-track D64; no other test reads this DAT). New `C64_D6z_IsInflated_AndStoredAsTheDatsD64` (modelled on `Amiga_Adz_IsInflated_AndStoredAsTheDatsAdf`: `orbit.d6z` holding the gzipped disk is `Ingested` as `c64`, stored as `Gzip Orbit (Europe).d64` with the plain bytes) and `C64_BareGz_FailsUnsupportedType` (`orbit.gz` fails `UnsupportedType` with V4's details).

  Done when: all of these pass, and features.md names the new types, the inflate, the `.gz` naming rule and the Attach stem limit (Risks); zip-format.md and deployment.md match § Changes.
- [x] **4. Parity guard**: P1, P2. Files: `BrowserCoreExtensionsTests.cs`. Tests: `BrowserCoreExtensionsTests` (all, including the new `EveryCoreExtension_IsIngestedOrListed` and `UningestedProblemsAreNamed`). Done when: both new tests pass and `NotIngested` matches P2.

## Decisions
- 2026-11-23 Which reverse gaps to close? → VICE compressed and extra types, and cartridge `.bin`/`.rom` aliases. Not Amiga CD32 (user)
- 2026-11-23 DS `.bin`? → Left out: the DAT's `.bin` entries are save data and demos (user) — D1
- 2026-11-23 N64 `.bin` makes six cheat-cart DAT entries matchable? → Accept (user) — D2
- 2026-11-23 Gzipped VICE images: store as is or inflate? → Inflate and DAT-match, with a `SourceExtension` hook for the stored extension (user)
- 2026-11-23 Other VICE types? → C64 `.d2m`/`.d4m`/`.tcrt` and bare `.gz` yes; `.g41`/`.g4z` no (user)
- 2026-11-23 Add a reverse guard test? → Yes (user)
- 2026-11-23 `.nbz`: gzip? → No. It's NIBTools' LZ format, so it's stored as is; libretro-vice routes it as a Nibbler image (code)
- 2026-11-23 `.d8z` maps to? → `.d81`. libretro-vice routes every `.??z` disk as a floppy and VICE probes the type (code)
- 2026-11-23 Bare `.gz` with no inner type? → `UnsupportedType` at ingest (code: Review can't name the stored bytes)
- 2026-11-23 Review fixes: injectable maps for UningestedProblems, handler-level SourceExtension tests, `.sc` → NewSystem, Attach stem limit in Risks, step 2 split, V4 unit test (reviewer)
- 2026-11-23 Review pass 2: P2 re-derived from the handlers and `CoreExtensions` (matches); `PetDrive` renamed `OtherCommodore` (VIC-20 `.d80`/`.d82` aren't C64-only); `vice_*` spelled out; `zip-format.md` and `deployment.md` added (they list these formats); the four A1 test classes already assert `RomExtensions`; `IngestPipelineTests`' C64 DAT is empty, so step 3 seeds one; test names, the `Headerless` hook and NSubstitute's `""` auto value pinned; `SourceExtension` placed after `KeepsExtension` (code)
- 2026-11-23 Review pass 3: deployment.md Commodore entries pinned as full text (the pass-2 wording nested backticks) (code)

## Kickoff prompt
```text
/spec implement 2026-11-23-core-file-type-parity.md
```
Starts the spec, or resumes it at the first unticked step.
