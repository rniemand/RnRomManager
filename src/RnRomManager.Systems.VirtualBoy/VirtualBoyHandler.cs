using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.VirtualBoy;

/// <summary>
/// Nintendo Virtual Boy. Its footer header has no magic bytes, so <see cref="ReadHeader"/> is always null and an unmatched
/// file goes to Review. Sizes follow the libretro <c>Nintendo - Virtual Boy</c> DAT (256 KiB–4 MiB <c>.vb</c>), with the
/// minimum left lower for homebrew and prototypes.
/// </summary>
public sealed class VirtualBoyHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.vb.png"));

    public string Id => "vb";
    public string DisplayName => "Virtual Boy";
    public string ShortName => "VB";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".vb"];
    public long MinRomSizeBytes => 64 * 1024;
    public long MaxRomSizeBytes => 4 * 1024 * 1024;
    public string DatName => "Nintendo - Virtual Boy";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public string BrowserCore => "beetle_vb";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
