namespace RnRomManager.Core.Dats;

/// <summary>Arcade D2: one <c>&lt;game&gt;</c> (or <c>&lt;machine&gt;</c>) of a MAME <c>-listxml</c> DAT.</summary>
public sealed record MameSet(
    string Name,
    string Description,
    string? Year,
    string? Manufacturer,
    string? CloneOf,
    string? RomOf,
    bool IsBios,
    IReadOnlyList<MameRom> Roms);
