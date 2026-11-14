using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari;

/// <summary>
/// Atari 5200. Cartridges have no header, so <see cref="ReadHeader"/> is always null and an unmatched file goes to Review.
/// Extensions and sizes follow the libretro <c>Atari - 5200</c> DAT (4–40 KiB <c>.a52</c>); <c>.bin</c> is also accepted
/// and a matched one is stored under the DAT's <c>.a52</c> name.
/// </summary>
public sealed class Atari5200Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.a5200.png"));

    public string Id => "a5200";
    public string DisplayName => "Atari 5200";
    public string ShortName => "5200";
    public string Family => Id;

    /// <summary><c>[0]</c> (<c>.a52</c>) is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".a52", ".bin"];

    public long MinRomSizeBytes => 4 * 1024;

    public long MaxRomSizeBytes => 64 * 1024;

    public string DatName => "Atari - 5200";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public string BrowserCore => "a5200";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
