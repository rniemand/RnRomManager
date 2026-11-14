using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega CD (Mega-CD). The redump DAT lists each disc's data track as <c>.bin</c>; a cue and its other tracks form the set,
/// and a standalone <c>.iso</c> is a release of its own. Stored outside the ZIP under exact names, as Saturn and PlayStation;
/// the redump DAT has no metadat, and there is no header (Sega CD B1). Its own family keeps a <c>.bin</c> from ever being tried
/// against the Mega Drive DAT, and the other way round.
/// </summary>
public sealed class SegaCdHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.segacd.png"));

    public string Id => "segacd";
    public string DisplayName => "Sega CD";
    public string ShortName => "SCD";
    public string Family => "segacd";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".bin", ".cue", ".iso"];

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

    /// <summary>No browser play yet (Sega CD B5 enables Genesis Plus GX).</summary>
    public string? BrowserCore => null;

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
