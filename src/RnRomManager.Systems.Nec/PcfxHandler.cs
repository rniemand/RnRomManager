using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nec;

/// <summary>
/// PC-FX (PC-FX X1). Stored and matched as TurboGrafx-CD discs are: the Redump DAT lists one track per disc (the data track,
/// or a whole-disc <c>.bin</c> for the PC-FXGA titles); a cue, TOC, CloneCD or MDS set is one release, a standalone <c>.chd</c>
/// a release of its own. No metadat and no header. Its own family keeps it apart from TurboGrafx-CD.
/// </summary>
public sealed class PcfxHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => TurboGrafx16Handler.LoadResource("boxart.pcfx.png"));

    public string Id => "pcfx";
    public string DisplayName => "PC-FX";
    public string ShortName => "PC-FX";
    public string Family => "pcfx";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as. A <c>.chd</c> never matches the DAT.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".bin", ".cue", ".chd", ".ccd", ".img", ".sub", ".mds", ".mdf", ".toc"];

    public long MinRomSizeBytes => 16;

    public long MaxRomSizeBytes => 1024L * 1024 * 1024;

    public string DatName => "NEC - PC-FX";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public string? BrowserCore => null;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
