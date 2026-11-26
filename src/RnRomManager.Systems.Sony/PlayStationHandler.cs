using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sony;

/// <summary>
/// Sony PlayStation. The redump DAT lists each disc's data track as <c>.bin</c>; a cue and its other tracks form the set.
/// Stored outside the ZIP under exact names (disc systems B4, B5); the redump DAT has no metadat, and there is no header.
/// A CloneCD <c>.ccd</c> with its <c>.img</c> and optional <c>.sub</c> is a set too (CloneCD B1), played through a cue built from the <c>.ccd</c>.
/// So are an Alcohol 120% <c>.mds</c> with its <c>.mdf</c> and a cdrdao <c>.toc</c> with its <c>.bin</c> tracks (file type gaps D1), played through a cue built from the <c>.mds</c> or <c>.toc</c>.
/// </summary>
public sealed class PlayStationHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(PlayStationHandler), "boxart.psx.png");

    public string Id => "psx";
    public string DisplayName => "PlayStation";
    public string ShortName => "PSX";
    public string Family => "psx";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as. A <c>.chd</c> is a lone file that never matches the DAT (CHD B1–B2); an <c>.img</c> is a lone raw image like an <c>.iso</c>, and a <c>.pbp</c> (PS1 Classics EBOOT) a lone file like a <c>.chd</c>, as is a <c>.cbn</c> (PCSX ReARMed compressed BIN).</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".bin", ".cue", ".iso", ".chd", ".img", ".pbp", ".ccd", ".sub", ".mds", ".mdf", ".toc", ".cbn"];

    public long MinRomSizeBytes => 16;

    public long MaxRomSizeBytes => 1024L * 1024 * 1024;

    public string DatName => "Sony - PlayStation";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <summary>Play B1: PCSX ReARMed, one disc per launch through <c>play-disc</c>; optional scph5501.bin, else HLE.</summary>
    public string BrowserCore => "pcsx_rearmed";

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
