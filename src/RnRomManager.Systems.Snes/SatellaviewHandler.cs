using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Snes;

/// <summary>
/// Satellaview (BS-X memory packs). Matched against libretro's No-Intro <c>Nintendo - Satellaview</c> DAT, whose
/// <c>.bs</c> entries are 128 KiB–1 MiB. There's no header check (<see cref="ReadHeader"/> is always null, like Atari
/// 2600), so an unmatched file always goes to Review. Snes9x plays it with <c>BS-X.bin</c> from <c>Paths:Bios</c>.
/// </summary>
public sealed class SatellaviewHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => SnesHandler.LoadResource("boxart.satellaview.png"));

    public string Id => "satellaview";
    public string DisplayName => "Satellaview";
    public string ShortName => "BS-X";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".bs"];

    /// <summary>The DAT's smallest pack.</summary>
    public long MinRomSizeBytes => 128 * 1024;

    /// <summary>The DAT's largest pack.</summary>
    public long MaxRomSizeBytes => 1024 * 1024;

    public string DatName => "Nintendo - Satellaview";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>Snes9x (operator-supplied), which loads a pack with <c>BS-X.bin</c> when its header passes <c>is_bsx</c>.</summary>
    public string BrowserCore => "snes9x";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
