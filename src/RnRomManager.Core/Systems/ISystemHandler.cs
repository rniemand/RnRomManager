namespace RnRomManager.Core.Systems;

/// <summary>
/// One supported system (spec §22). Core enumerates handlers for folders, DATs, extensions and limits;
/// nothing outside a <c>RnRomManager.Systems.X</c> project is system-specific.
/// </summary>
public interface ISystemHandler
{
    /// <summary>"gb": folder name, info.json <c>system</c>, DB value.</summary>
    string Id { get; }

    /// <summary>"Game Boy".</summary>
    string DisplayName { get; }

    /// <summary>"GB": compact label for game pickers; defaults to <see cref="DisplayName"/>.</summary>
    string ShortName => DisplayName;

    /// <summary>"gameboy": handlers sharing classification and DATs (§7.2 step 4).</summary>
    string Family { get; }

    /// <summary>Lower-case, with the dot; <c>[0]</c> is used when an unmatched file is renamed (§8.4).</summary>
    IReadOnlyList<string> RomExtensions { get; }

    long MinRomSizeBytes { get; }

    long MaxRomSizeBytes { get; }

    /// <summary>"Nintendo - Game Boy": DAT file names and the thumbnail folder.</summary>
    string DatName { get; }

    /// <summary>
    /// true: ROMs are entries of the game ZIP. false: each ROM is a file next to the ZIP, named by its
    /// <c>externalFileName</c> in info.json. No default, so every handler states it; handlers sharing a
    /// <see cref="Family"/> must agree.
    /// </summary>
    bool EmbedRomsInZip { get; }

    /// <summary>512×512 PNG stored as <c>boxart-01.png</c> with <c>isDefault: true</c> (§6.1).</summary>
    ReadOnlyMemory<byte> DefaultBoxArtPng { get; }

    /// <returns>null = unreadable / no header (unmatched → the ingest folder's handler, §7.2 step 4).</returns>
    RomHeader? ReadHeader(ReadOnlySpan<byte> rom);

    /// <summary>Bytes that are hashed and stored (§7.2 step 2), e.g. a SNES copier-header strip.</summary>
    ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => rom;
}
