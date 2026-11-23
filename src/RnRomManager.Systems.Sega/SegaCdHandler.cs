using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega CD (Mega-CD). The redump DAT lists each disc's data track as <c>.bin</c>; a cue and its other tracks form the set,
/// and a standalone <c>.iso</c> is a release of its own. Stored outside the ZIP under exact names, as Saturn and PlayStation;
/// the redump DAT has no metadat, and there is no header (Sega CD B1). Its own family keeps a <c>.bin</c> from ever being tried
/// against the Mega Drive DAT, and the other way round.
/// A CloneCD <c>.ccd</c> with its <c>.img</c> and optional <c>.sub</c> is a set too (CloneCD B1), played through a cue built from the <c>.ccd</c>.
/// So are an Alcohol 120% <c>.mds</c> with its <c>.mdf</c> and a cdrdao <c>.toc</c> with its <c>.bin</c> tracks (file type gaps D1), played through a cue built from the <c>.mds</c> or <c>.toc</c>.
/// </summary>
public sealed class SegaCdHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.segacd.png"));

    public string Id => "segacd";
    public string DisplayName => "Sega CD";
    public string ShortName => "SCD";
    public string Family => "segacd";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as. A <c>.chd</c> is a lone file that never matches the DAT (CHD B1–B2).</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".bin", ".cue", ".iso", ".chd", ".ccd", ".img", ".sub", ".mds", ".mdf", ".toc"];

    public long MinRomSizeBytes => 16;

    public long MaxRomSizeBytes => 1024L * 1024 * 1024;

    public string DatName => "Sega - Mega-CD - Sega CD";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    /// <summary>
    /// Genesis Plus GX, shared with Mega Drive: the play URL's <c>disc</c> flag sends a Sega CD release through play-disc and
    /// the regional BIOS files (Sega CD B5–B7).
    /// </summary>
    public string? BrowserCore => "genesis_plus_gx";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
