using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.ThreeDo;

/// <summary>
/// 3DO (3DO B1). The redump DAT lists each disc's data track as <c>.bin</c>; a cue and its other tracks form the set, and a
/// standalone <c>.iso</c> is a release of its own. Stored outside the ZIP under exact names, as Sega CD; the redump DAT has no
/// metadat, and there is no header. Its own family keeps a <c>.bin</c> from ever being tried against another disc system's DAT.
/// The id starts with a digit, so the project, namespace and class are spelled <c>ThreeDo</c>.
/// A CloneCD <c>.ccd</c> with its <c>.img</c> and optional <c>.sub</c> is a set too (CloneCD B1), played through a cue built from the <c>.ccd</c>.
/// So are an Alcohol 120% <c>.mds</c> with its <c>.mdf</c> and a cdrdao <c>.toc</c> with its <c>.bin</c> tracks (file type gaps D1), played through a cue built from the <c>.mds</c> or <c>.toc</c>.
/// </summary>
public sealed class ThreeDoHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.3do.png"));

    public string Id => "3do";
    public string DisplayName => "3DO";
    public string ShortName => "3DO";
    public string Family => "3do";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as. A <c>.chd</c> is a lone file that never matches the DAT, as for Sega CD.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".bin", ".cue", ".iso", ".chd", ".ccd", ".img", ".sub", ".mds", ".mdf", ".toc"];

    public long MinRomSizeBytes => 16;

    /// <summary>Per file, as Sega CD.</summary>
    public long MaxRomSizeBytes => 1024L * 1024 * 1024;

    public string DatName => "The 3DO Company - 3DO";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    /// <summary>
    /// 3DO B7: Opera, whose files the operator supplies (<c>Emulator:OperaCorePath</c>); every release is a disc, so it
    /// plays through play-disc.
    /// </summary>
    public string? BrowserCore => "opera";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
