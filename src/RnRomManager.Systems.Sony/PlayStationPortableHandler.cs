using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sony;

/// <summary>
/// Sony PlayStation Portable: one <c>.iso</c> per UMD, or a compressed <c>.cso</c> or <c>.chd</c> (chdman <c>createdvd</c>), or an <c>EBOOT</c>-style <c>.pbp</c>, or a homebrew <c>.elf</c>/<c>.prx</c> executable.
/// Stored outside the ZIP under exact names (disc systems B4, B5); the redump DAT has no metadat, and there is no header.
/// </summary>
public sealed class PlayStationPortableHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(PlayStationPortableHandler), "boxart.psp.png");

    public string Id => "psp";
    public string DisplayName => "PlayStation Portable";
    public string ShortName => "PSP";
    public string Family => "psp";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as. A <c>.cso</c>, <c>.pbp</c>, <c>.chd</c>, <c>.elf</c> or <c>.prx</c> never matches the DAT (Redump lists the <c>.iso</c>).</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".iso", ".cso", ".pbp", ".chd", ".elf", ".prx"];

    public long MinRomSizeBytes => 16;

    public long MaxRomSizeBytes => 8L * 1024L * 1024 * 1024;

    public string DatName => "Sony - PlayStation Portable";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    /// <summary>PSP B1: PPSSPP, in the isolated PSP tab; the external <c>.iso</c>, <c>.cso</c>, <c>.pbp</c>, <c>.chd</c>, <c>.elf</c> or <c>.prx</c> streams through play-disc.</summary>
    public string BrowserCore => "ppsspp";

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
