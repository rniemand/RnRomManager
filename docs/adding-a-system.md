# Adding a system

Everything system-specific lives behind one interface, `ISystemHandler` (`src/RnRomManager.Core/Systems/ISystemHandler.cs`). Core, the database, the API and the UI enumerate the registered handlers for folders, DATs, file extensions, size limits and filters; nothing outside a `RnRomManager.Systems.X` project knows about Game Boy. Adding SNES means a new handler project, one registration line, tests and docs, with no changes to the core.

## The contract

```csharp
public interface ISystemHandler
{
    string Id { get; }                            // "gb": folder name, info.json `system`, DB value
    string DisplayName { get; }                   // "Game Boy"
    string Family { get; }                        // "gameboy": handlers sharing classification and DATs
    IReadOnlyList<string> RomExtensions { get; }  // [".gb"], lower-case with the dot; [0] is used for renames
    long MinRomSizeBytes { get; }                 // smaller files fail as TooSmall
    long MaxRomSizeBytes { get; }                 // larger files fail as TooLarge
    string DatName { get; }                       // "Nintendo - Game Boy": DAT file names + thumbnail folder
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
| `Id` | `roms/<Id>/` and `ingest/<Id>/` (created at startup with their letter and `_*` folders), `info.json` `system`, the database `system_id` (a `VARCHAR`, so no migration), `/api/systems`, the UI's system filter and the `system-<Id>` icon (unmapped ids fall back to a controller icon). |
| `Family` | Which handlers' headers and DATs are tried for a file. GB and GBC share the `gameboy` family, so a file in either ingest folder is checked against both DATs, and the header decides where it's stored. GBA and N64 are their own `gba` and `n64` families: a `.gba` file in a GB folder fails as `UnsupportedType`. |
| `RomExtensions` | Which ingest files are picked up (case-insensitive). An unmatched file whose extension doesn't fit its classified system is renamed to `RomExtensions[0]`. It also filters the system's No-Intro DAT: entries whose file extension isn't in the list (case-insensitive) are left out of hash matching, review DAT search, suggestions and base titles, since no accepted file could match them. NES lists only `.nes`, so the DAT's headerless `.unh` copies are ignored; N64 drops its few `.bin`/`.cmd`/`.sm` entries. |
| `MinRomSizeBytes` / `MaxRomSizeBytes` | Validation before hashing. The whole ROM is read into memory for `ReadHeader`, bounded by the maximum. |
| `DatName` | The five DAT files fetched per system: `no-intro/<DatName>.dat` plus the `releaseyear`, `developer`, `publisher` and `genre` metadat files, all relative to `Sources:DatBaseUrl` and cached in `cache/dats/<kind>/`. Also the libretro-thumbnails folder name. |
| `DefaultBoxArtPng` | Stored when no box art is found. Make your own (the GB/GBC/GBA/N64/NES ones are drawn for this project; their SVG sources are in each project's `Resources/boxart/`). |
| `ReadHeader` | Classification (`ClassifiedSystemId`), the invalid-header rule (`IsPlausible`), review suggestions (`Title`) and the stored header (`Fields`). Return null for a file with no recognisable header; an unmatched headerless file then belongs to the ingest folder's handler. |
| `NormalizeForHashing` | The bytes that are hashed **and stored**, and the bytes `ReadHeader` sees. Stored bytes, `info.json` hashes and the integrity checker's deep hashes then always agree. Nintendo 64 is the example: `.v64` (byte-swapped) and `.n64` (little-endian) dumps are converted to big-endian here, so they match the DAT's `.z64` entries and are stored as `.z64` (`RomExtensions[0]`). A SNES handler would strip a copier header the same way. |

**Limits of the contract:**

- One file per release (cartridge systems). Multi-file or CD-based systems need a contract change.
- `Fields` values must be JSON primitives (string, number, bool or null), because the UI renders them generically.

## Step by step

1. Create `src/RnRomManager.Systems.<Name>` (class library, referencing `RnRomManager.Core`) and add it to `RnRomManager.slnx`. Add a matching `tests/RnRomManager.Systems.<Name>.Tests` project. A closely related system may instead live in an existing project when that is simpler: Game Boy Advance shares `RnRomManager.Systems.GameBoy` (and its box-art and resource code) but is its own `gba` family. Nintendo 64 has its own project, `RnRomManager.Systems.Nintendo64`, with a small private loader for its embedded box art; NES follows the same pattern in `RnRomManager.Systems.Nes` (a short name rather than the full system name). Also add the new project's `.csproj` to the restore layer in the `Dockerfile`.
2. Implement `ISystemHandler` (one class per `Id`). Embed the default box-art PNG as a resource, together with its source file.
3. Register it in `src/RnRomManager.Api/AppHost.cs` next to the existing ones:
   ```csharp
   builder.Services.AddSystemHandler<SuperNintendoHandler>();
   ```
   and add a project reference from `RnRomManager.Api`.
4. Add the system to the test data builders in `tests/RnRomManager.TestData` if you want seeded ROMs and fixture DATs for it (`FixtureDatWriter` writes the five files per system).
5. Optionally map its icon in `src/RnRomManager.Web/src/lib/components/icons.ts` (`'system-snes': 'bi bi-…'`); without it the UI uses the controller fallback.
6. Test the header reader with synthetic ROMs (never commit real ROMs), plus the registry lookups and the box-art PNG (valid, 512×512). See `tests/RnRomManager.Systems.GameBoy.Tests` for the pattern.
7. Update the docs: this repository's README (supported systems), [features](features.md) if behaviour differs, and [ZIP format](zip-format.md) for the new `header` fields.

After deployment, an Admin runs **Admin → DATs → Refresh DATs** to download the new system's DATs; ingestion refuses to start (`DatsMissing`) until every registered system has them.
