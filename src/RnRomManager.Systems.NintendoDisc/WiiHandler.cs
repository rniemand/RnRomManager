using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NintendoDisc;

/// <summary>
/// Nintendo Wii: one <c>.iso</c> or <c>.wbfs</c> per disc (a <c>.wbfs</c> is matched by game id). Same size limits as GameCube on purpose: ingest checks size against the first handler in the family that lists the extension.
/// Stored outside the ZIP under exact names (disc systems B4, B5); the redump DAT has no metadat. The disc header (B6) tells GameCube and Wii apart.
/// </summary>
public sealed class WiiHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.wii.png"));

    public string Id => "wii";
    public string DisplayName => "Wii";
    public string ShortName => "Wii";
    public string Family => "nintendo-disc";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".iso", ".wbfs"];

    public long MinRomSizeBytes => 32 * 1024;

    public long MaxRomSizeBytes => 9L * 1024L * 1024 * 1024;

    public string DatName => "Nintendo - Wii";

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
