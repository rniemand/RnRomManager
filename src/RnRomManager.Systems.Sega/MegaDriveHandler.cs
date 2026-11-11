using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega Mega Drive / Genesis (spec D1–D3). The header at <c>0x100</c> is optional: a file without <c>SEGA</c> in the
/// system-type field is not invalid, so an unmatched file goes to Review. Raw dumps are already big-endian, so hashing
/// uses the file as-is; interleaved <c>.smd</c> dumps aren't supported.
/// </summary>
public sealed class MegaDriveHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.megadrive.png"));

    public string Id => "md";
    public string DisplayName => "Mega Drive / Genesis";
    public string ShortName => "MD";
    public string Family => "megadrive";

    /// <summary><c>[0]</c> (<c>.md</c>) is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".md", ".bin", ".gen"];

    public long MinRomSizeBytes => 8192;
    public long MaxRomSizeBytes => 16 * 1024 * 1024;
    public string DatName => "Sega - Mega Drive - Genesis";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => MegaDriveHeader.Read(rom, Id);
}
