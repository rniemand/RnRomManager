namespace RnRomManager.Core.Dats;

/// <summary>Arcade D2: one ROM of a MAME set. <see cref="Crc32"/> is lower-case hex; null only for a nodump ROM.</summary>
public sealed record MameRom(
    string Name,
    long Size,
    string? Crc32,
    string? Merge,
    string? Bios,
    bool NoDump);
