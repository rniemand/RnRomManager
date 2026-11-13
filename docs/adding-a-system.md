# Adding a system

Everything system-specific lives behind one interface, `ISystemHandler` (`src/RnRomManager.Core/Systems/ISystemHandler.cs`). Core, the database, the API and the UI enumerate the registered handlers for folders, DATs, file extensions, size limits and filters; nothing outside a `RnRomManager.Systems.X` project knows about Game Boy. Adding SG-1000 means a new handler (or a project), one registration line, tests and docs, with no changes to the core.

## The contract

```csharp
public interface ISystemHandler
{
    string Id { get; }                            // "gb": folder name, info.json `system`, DB value
    string DisplayName { get; }                   // "Game Boy"
    string ShortName => DisplayName;              // "GB": game pickers' system badge
    string Family { get; }                        // "gameboy": handlers sharing classification and DATs
    IReadOnlyList<string> RomExtensions { get; }  // [".gb"], lower-case with the dot; [0] is used for renames
    long MinRomSizeBytes { get; }                 // smaller files fail as TooSmall
    long MaxRomSizeBytes { get; }                 // larger files fail as TooLarge
    string DatName { get; }                       // "Nintendo - Game Boy": DAT file names + thumbnail folder
    bool EmbedRomsInZip { get; }                  // true: ROMs inside the game ZIP; false: files next to it (no default)
    DatSource DatSource => DatSource.Libretro;    // Libretro (DATs, SHA1) or TitleDb (titledb, title id)
    string DatFolder => DatKind.NoIntro;          // libretro folder of the main DAT: "no-intro" or "redump"
    IReadOnlyList<string> MetadatKinds => DatKind.Metadat;  // metadat files fetched: all four, or [] (none)
    bool FileSets => false;                       // true: exact external names, bin/cue sets, Review limits
    string? BrowserCore => null;                  // EmulatorJS core for the Play button, e.g. "gambatte"
    ReadOnlyMemory<byte> DefaultBoxArtPng { get; }  // 512×512 placeholder box art
    RomHeader? ReadHeader(ReadOnlySpan<byte> rom);  // null = no readable header
    ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => rom;
}

public sealed record RomHeader(
    string? ClassifiedSystemId,                   // which handler Id the header says this ROM is for
    string? Title,                                // used for review suggestions
    bool IsPlausible,                             // false → InvalidHeader when the ROM matches no DAT
    IReadOnlyDictionary<string, object?> Fields); // stored as info.json `header`, shown as a key/value table
```

What each member drives:

| Member | Used for |
|---|---|
| `Id` | `roms/<Id>/` and `ingest/<Id>/` (created at startup with their letter and `_*` folders), `info.json` `system`, the database `system_id` (a `VARCHAR`, so no migration), `/api/systems` (sorted by `DisplayName`), the UI's system filter and the `system-<Id>` icon (unmapped ids fall back to a controller icon). |
| `ShortName` | The compact system badge in game pickers (`GameTypeahead`) and `systemShortName` in `GET /api/games`. Defaults to `DisplayName`; every shipped handler overrides it (`GB`, `SNES`, `2600`, `GG`, …). |
| `Family` | Which handlers' headers and DATs are tried for a file. GB and GBC share the `gameboy` family, so a file in either ingest folder is checked against both DATs, and the header decides where it's stored. GBA and N64 are their own `gba` and `n64` families: a `.gba` file in a GB folder fails as `UnsupportedType`. |
| `RomExtensions` | Which ingest files are picked up (case-insensitive). An unmatched file whose extension doesn't fit its classified system is renamed to `RomExtensions[0]`. It also filters the system's No-Intro DAT: entries whose file extension isn't in the list (case-insensitive) are left out of hash matching, review DAT search, suggestions and base titles, since no accepted file could match them. NES lists only `.nes`, so the DAT's headerless `.unh` copies are ignored; N64 drops its few `.bin`/`.cmd`/`.sm` entries. |
| `MinRomSizeBytes` / `MaxRomSizeBytes` | Validation before hashing, per file (each track of a disc set). Ingest checks a file's size against the first member of its family that lists the file's extension, so family members sharing an extension should share limits (GameCube and Wii both allow 32 KiB–9 GiB, or a large Wii disc would fail `TooLarge` against GameCube's). For an embedded system the whole ROM is read into memory for `ReadHeader`, bounded by the maximum; for an external one the file is hashed in one streamed read and `ReadHeader` gets only its first MiB, so the maximum can be large (64 GiB for Switch). For an external family, only the members whose `RomExtensions` and size range fit the file take part in identifying it. |
| `EmbedRomsInZip` | Where the ROMs live (see [ZIP format](zip-format.md#external-rom-files)). Required, with no default, so every handler says it. Every handler of one `Family` must agree: the registry refuses a mixed family at startup, naming it. An external system never normalises bytes (`NormalizeForHashing` isn't called), keeps the source file's extension, and its ROMs download, move and get checked as files. |
| `DatSource` | `Libretro` (the default) fetches the libretro files below (one to five) and matches by SHA1. `TitleDb` fetches the shared [titledb](https://github.com/blawar/titledb) file (`Sources:TitleDbUrl`) once per refresh, stores a compact copy per handler (`cache/dats/titledb/<DatName>.json`), matches by the title id from the header's `titleId` field or the file name, and takes media from the URLs titledb lists. Two systems may never share a `DatName`, whatever their source. |
| `DatName` | For a libretro system, the DAT files fetched per system: `<DatFolder>/<DatName>.dat` plus each metadat file `MetadatKinds` lists (`releaseyear`, `developer`, `publisher`, `genre`), all relative to `Sources:DatBaseUrl` and cached in `cache/dats/<kind>/`; so one to five files. Also the libretro-thumbnails folder name. For a titledb system, the name of its compact file and its row on **Admin → DATs**. |
| `DatFolder` | The libretro folder of the main DAT: `no-intro` (the default) or `redump` (the six disc systems). Admin → DATs and the versions check treat both the same way. |
| `MetadatKinds` | Which metadat files exist for the system (default all four). A kind it doesn't list is neither fetched nor required, and gives empty metadata (no year, developers, publishers or genres). The disc systems list none, since libretro has no Redump metadat. |
| `FileSets` | False by default. True (the disc systems) means: external files keep their **exact** names (no `<ZipBase>.` prefix or ` (N)` suffix; a taken name is `NameTaken`), a release may have other files (`files` in info.json), and, when `RomExtensions` includes `.cue`, ingest groups each cue with its `.bin` tracks into one release. A `.cue`/`.bin` Review item can only be deleted. The registry refuses `FileSets` with `EmbedRomsInZip` true, and a family whose members disagree on it. |
| `BrowserCore` | The [EmulatorJS](https://emulatorjs.org) core that plays the system in the browser, or null (the default) for no **Play** button. `gb` and `gbc` return `gambatte`, `gba` returns `mgba`. A new core also needs vendoring and allowing in the play page ([development](development.md#browser-emulators)). |
| `DefaultBoxArtPng` | Stored when no box art is found. Make your own (the GB/GBC/GBA/N64/NES/SNES/Atari 2600/ColecoVision/Game Gear/Master System/Mega Drive/32X/TurboGrafx-16/Switch/PlayStation/PSP/GameCube/Wii/Xbox 360/Saturn ones are drawn for this project; their SVG sources are in each project's `Resources/boxart/`). |
| `ReadHeader` | Classification (`ClassifiedSystemId`), the invalid-header rule (`IsPlausible`), review suggestions (`Title`) and the stored header (`Fields`). Return null for a file with no recognisable header; an unmatched headerless file then belongs to the ingest folder's handler. A system whose cartridges have no header returns null always, as `Atari2600Handler` does: its unmatched files always go to Review (never `InvalidHeader`), its releases have no Header button, and `info.json` `header` is null. A system with an optional header returns it when found and null otherwise, as `GameGearHandler` and `MasterSystemHandler` do with `TMR SEGA` (one shared `TmrSegaHeader` reader, classified as the calling handler's id) and `MegaDriveHandler` with the `SEGA` header at 0x100: headerless files still go to Review, and only releases with a header show the Header button. A system with only a magic-bytes check returns a header every time, plausible or not, as `ColecoVisionHandler` does with its `magic` field (`AA55`/`55AA`). |
| `NormalizeForHashing` | The bytes that are hashed **and stored**, and the bytes `ReadHeader` sees. Stored bytes, `info.json` hashes and the integrity checker's deep hashes then always agree. Nintendo 64 is the example: `.v64` (byte-swapped) and `.n64` (little-endian) dumps are converted to big-endian here, so they match the DAT's `.z64` entries and are stored as `.z64` (`RomExtensions[0]`). SNES strips a 512-byte copier header here (by file size), so headered `.smc` dumps match the DAT's `.sfc` entries and are stored as `.sfc`. TurboGrafx-16 combines both patterns: no header (`ReadHeader` always null, like Atari 2600) plus a size-only 512-byte copier strip (size 512 bytes past a multiple of 8 KiB), with `MaxRomSizeBytes` set to the DAT's largest entry plus 512 because the size limit is checked before the strip. |

Storage of every shipped system:

| System | `Id` | `EmbedRomsInZip` | `DatSource` |
|---|---|---|---|
| Game Boy | `gb` | true | Libretro |
| Game Boy Color | `gbc` | true | Libretro |
| Game Boy Advance | `gba` | true | Libretro |
| Nintendo 64 | `n64` | true | Libretro |
| Nintendo Entertainment System | `nes` | true | Libretro |
| Super Nintendo Entertainment System | `snes` | true | Libretro |
| Atari 2600 | `a2600` | true | Libretro |
| ColecoVision | `coleco` | true | Libretro |
| Game Gear | `gg` | true | Libretro |
| Master System | `sms` | true | Libretro |
| Mega Drive / Genesis | `md` | true | Libretro |
| 32X | `s32x` | true | Libretro |
| TurboGrafx-16 | `tg16` | true | Libretro |
| Nintendo Switch | `switch` | false | TitleDb |
| PlayStation | `psx` | false (`FileSets`, bin/cue sets) | Libretro (`redump`, no metadat) |
| PlayStation Portable | `psp` | false (`FileSets`) | Libretro (`redump`, no metadat) |
| GameCube | `gc` | false (`FileSets`) | Libretro (`redump`, no metadat) |
| Wii | `wii` | false (`FileSets`) | Libretro (`redump`, no metadat) |
| Xbox 360 | `x360` | false (`FileSets`) | Libretro (`redump`, no metadat) |
| Saturn | `saturn` | false (`FileSets`, bin/cue sets) | Libretro (`redump`, no metadat) |

**Switch, the external + titledb example** (`RnRomManager.Systems.Switch`): `EmbedRomsInZip` is false and `DatSource` is `TitleDb`. `ReadHeader` reads only the first MiB it's given: a PFS0 (`.nsp`, `.nsz`) yields its file count and, from its ticket names, a `titleId`; an XCI (`HEAD` at 0x100 or 0x1100) yields no id, so the file name's `[0100…]` is used. Every offset is checked with widened arithmetic before it's sliced, and anything damaged gives a null id rather than an error. It needs no `NormalizeForHashing`.

**The disc systems** (`RnRomManager.Systems.Sony`, `.NintendoDisc`, `.Microsoft`, and Saturn in `.Sega`) are the `FileSets` examples. PlayStation, PSP, Xbox 360 and Saturn have no header (`ReadHeader` returns null). GameCube and Wii share the `nintendo-disc` family and one `NintendoDiscHeader.Read(rom, callerId)`: the magic word at 0x1C (GameCube) or 0x18 (Wii) classifies the disc, so a Wii disc dropped in the GameCube folder is stored as Wii.

**Updates tab.** There is no handler property for it: any system whose identification flags a release `Update` (titledb does, for title IDs ending in `800` and for `[UPD]` files) gets the Game page's **Updates** tab for those releases, ordered by the `version` read from a `[x.y.z]` tag in the file name. A libretro system's DAT flags never include `Update`, so its games show only Releases.

**Limits of the contract:**

- A release is one file unless the system sets `FileSets`, and then only bin/cue sets (a `.cue` naming `.bin` tracks) are grouped. Other multi-file formats (`.gdi`, `.ccd`, `.mds`, `.chd`, audio tracks as `.wav`/`.ape`) aren't supported. Multi-disc games are one release per disc, grouped by title.
- `Fields` values must be JSON primitives (string, number, bool or null), because the UI renders them generically.

## Step by step

1. Create `src/RnRomManager.Systems.<Name>` (class library, referencing `RnRomManager.Core`) and add it to `RnRomManager.slnx`. Add a matching `tests/RnRomManager.Systems.<Name>.Tests` project. A closely related system may instead live in an existing project when that is simpler: Game Boy Advance shares `RnRomManager.Systems.GameBoy` (and its box-art and resource code) but is its own `gba` family. Nintendo 64 has its own project, `RnRomManager.Systems.Nintendo64`, with a small private loader for its embedded box art; NES and SNES follow the same pattern in `RnRomManager.Systems.Nes`, `RnRomManager.Systems.Snes`, `RnRomManager.Systems.Atari`, `RnRomManager.Systems.Coleco`, `RnRomManager.Systems.Sega` and `RnRomManager.Systems.Nec` (short names rather than the full system names). Master System and Mega Drive share `RnRomManager.Systems.Sega` with Game Gear: Master System reuses Game Gear's `TMR SEGA` reader (`TmrSegaHeader`) rather than copying it, and all of them load their box art through one `EmbeddedResources` helper. 32X shows the same for a header: its ROMs carry the Mega Drive header, so `Sega32xHandler` and `MegaDriveHandler` both call the shared `MegaDriveHeader.Read(rom, systemId)`, which classifies the result as the calling system. Also add the new project's `.csproj` to the restore layer in the `Dockerfile`.
2. Implement `ISystemHandler` (one class per `Id`). Embed the default box-art PNG as a resource, together with its source file.
3. Register it in `src/RnRomManager.Api/AppHost.cs` next to the existing ones:
   ```csharp
   builder.Services.AddSystemHandler<Sg1000Handler>();
   ```
   and add a project reference from `RnRomManager.Api`.
4. Add the system to the test data builders in `tests/RnRomManager.TestData` if you want seeded ROMs and fixture DATs for it (`FixtureDatWriter` writes the main DAT plus its metadat files per libretro system, in the folder its `layouts` entry gives (`FixtureDatLayout.NoIntro` by default, `FixtureDatLayout.Redump` for a disc system), or a titledb compact file through its `titleDb` map). Every test fixture that loads DATs for all systems must include the new one, or ingestion's `DatsMissing` preflight fails.
5. Optionally map its icon in `src/RnRomManager.Web/src/lib/components/icons.ts` (`'system-sg1000': 'bi bi-…'`); without it the UI uses the controller fallback.
6. Test the header reader with synthetic ROMs (never commit real ROMs), plus the registry lookups and the box-art PNG (valid, 512×512). See `tests/RnRomManager.Systems.GameBoy.Tests` for the pattern.
7. Update the docs: this repository's README (supported systems), [features](features.md) if behaviour differs, and [ZIP format](zip-format.md) for the new `header` fields.

After deployment, an Admin runs **Admin → DATs → Refresh DATs** to download the new system's DATs; ingestion refuses to start (`DatsMissing`) until every registered system has them.
