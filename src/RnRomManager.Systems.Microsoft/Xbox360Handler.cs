using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Microsoft;

/// <summary>
/// Microsoft Xbox 360: one <c>.iso</c> per disc.
/// Stored outside the ZIP under exact names (disc systems B4, B5); the redump DAT has no metadat, and there is no header.
/// </summary>
public sealed class Xbox360Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.x360.png"));

    public string Id => "x360";
    public string DisplayName => "Xbox 360";
    public string ShortName => "X360";
    public string Family => "x360";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".iso"];

    public long MinRomSizeBytes => 2048;

    public long MaxRomSizeBytes => 9L * 1024L * 1024 * 1024;

    public string DatName => "Microsoft - Xbox 360";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
