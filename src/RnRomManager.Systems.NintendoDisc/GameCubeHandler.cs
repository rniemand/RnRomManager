using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NintendoDisc;

/// <summary>
/// Nintendo GameCube: one <c>.iso</c> per disc.
/// Stored outside the ZIP under exact names (disc systems B4, B5); the redump DAT has no metadat. The disc header (B6) tells GameCube and Wii apart.
/// </summary>
public sealed class GameCubeHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.gc.png"));

    public string Id => "gc";
    public string DisplayName => "GameCube";
    public string ShortName => "GC";
    public string Family => "nintendo-disc";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".iso"];

    public long MinRomSizeBytes => 32 * 1024;

    public long MaxRomSizeBytes => 9L * 1024L * 1024 * 1024;

    public string DatName => "Nintendo - GameCube";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => NintendoDiscHeader.Read(rom, Id);
}
