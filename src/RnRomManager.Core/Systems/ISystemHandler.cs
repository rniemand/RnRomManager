using RnRomManager.Core.Dats;

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

    /// <summary>
    /// B5: true for disc systems whose release can be a file set (a data track, its <c>.cue</c> and the other tracks).
    /// External files then keep their exact names (B10), ingest groups cue and CloneCD sets (B7) and Review restricts set
    /// files (B15). Requires <see cref="EmbedRomsInZip"/> false; handlers sharing a <see cref="Family"/> must agree.
    /// </summary>
    bool FileSets => false;

    /// <summary>
    /// S2: true for an external system whose older games may still hold every ROM inside the ZIP (all
    /// <c>externalFileName</c> null) until the storage migration job moves them out. Requires
    /// <see cref="EmbedRomsInZip"/> false and <see cref="FileSets"/> false. See <c>RomStorage.IsEmbedded</c>.
    /// </summary>
    bool LegacyEmbeddedRoms => false;

    /// <summary>
    /// ROM Zip Config B1: true when an Admin may switch <see cref="EmbedRomsInZip"/> (stored as
    /// <c>storage.embedRoms.&lt;id&gt;</c>, see <c>IRomStorageSettings</c>). Requires <see cref="LegacyEmbeddedRoms"/>
    /// and a <see cref="Family"/> of its own.
    /// </summary>
    bool RomStorageConfigurable => false;

    /// <summary>B1: <see cref="EmbedRomsInZip"/> with no stored setting; the registry checks its storage rules on this value.</summary>
    bool DefaultEmbedRomsInZip => EmbedRomsInZip;

    /// <summary>true: the extension names the file's format, so Review keeps the source file's extension.</summary>
    bool KeepsSourceExtension => false;

    /// <summary>Whether Review keeps a source file's <paramref name="extension"/>; <see cref="KeepsSourceExtension"/> unless a handler decides per extension.</summary>
    bool KeepsExtension(string extension) => KeepsSourceExtension;

    /// <summary>Spec T1: libretro's DAT files, or blawar/titledb.</summary>
    DatSource DatSource => DatSource.Libretro;

    /// <summary>B1: the main DAT's folder under <c>Sources:DatBaseUrl</c> and <c>cache/dats/</c>: <c>no-intro</c>, <c>redump</c> or <c>tosec</c>.</summary>
    string DatFolder => DatKind.NoIntro;

    /// <summary>B2: the metadat files that exist for this system; a kind not listed gives empty metadata.</summary>
    IReadOnlyList<string> MetadatKinds => DatKind.Metadat;

    /// <summary>Browser emulators B1: the EmulatorJS core that plays this system in the browser, or null when none does.</summary>
    string? BrowserCore => null;

    /// <summary>512×512 PNG stored as <c>boxart-01.png</c> with <c>isDefault: true</c> (§6.1).</summary>
    ReadOnlyMemory<byte> DefaultBoxArtPng { get; }

    /// <returns>null = unreadable / no header (unmatched → the ingest folder's handler, §7.2 step 4).</returns>
    RomHeader? ReadHeader(ReadOnlySpan<byte> rom);

    /// <summary>Bytes that are hashed and stored (§7.2 step 2), e.g. a SNES copier-header strip.</summary>
    ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => rom;
}
