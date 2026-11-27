using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>
/// Commodore CDTV (CD32 A1). Stored and matched as PC-FX discs are: libretro's Redump <c>Commodore - CDTV</c> DAT (65 games)
/// lists one <c>.bin</c> per disc, a whole single-track disc or its <c>(Track 01)</c>, the largest 767,163,600 bytes; a cue, TOC,
/// CloneCD or MDS set is one release, a standalone <c>.chd</c>, <c>.iso</c> or <c>.nrg</c> goes to Review. No metadat and no header.
/// Its own family keeps it apart from CD32.
/// </summary>
public sealed class CdtvHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(CdtvHandler), "boxart.cdtv.png");

    public string Id => "cdtv";
    public string DisplayName => "Commodore CDTV";
    public string ShortName => "CDTV";
    public string Family => "cdtv";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as. Saturn's list and order.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".bin", ".cue", ".nrg", ".chd", ".iso", ".ccd", ".img", ".sub", ".mds", ".mdf", ".toc"];

    public long MinRomSizeBytes => 16;

    public long MaxRomSizeBytes => 1024L * 1024 * 1024;

    public string DatName => "Commodore - CDTV";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    /// <summary>CD32 A1: PUAE, vendored; needs the operator's Kickstart ROMs.</summary>
    public string? BrowserCore => "puae";

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
