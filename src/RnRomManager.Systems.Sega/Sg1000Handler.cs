using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega SG-1000 / SC-3000 (spec S1). Cartridges have no header, so <see cref="ReadHeader"/> is always null and an unmatched
/// file goes to Review, keeping its <c>.sg</c> or <c>.sc</c> extension. Matched against the libretro <c>Sega - SG-1000</c> DAT
/// (8–48 KiB <c>.sg</c>); the 2 KiB–1 MiB range leaves room for homebrew and multicarts.
/// </summary>
public sealed class Sg1000Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(Sg1000Handler), "boxart.sg1000.png");

    public string Id => "sg1000";
    public string DisplayName => "SG-1000 / SC-3000";
    public string ShortName => "SG-1000";
    public string Family => "sg1000";
    public IReadOnlyList<string> RomExtensions { get; } = [".sg", ".sc"];
    public long MinRomSizeBytes => 2048;
    public long MaxRomSizeBytes => 1024 * 1024;
    public string DatName => "Sega - SG-1000";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <inheritdoc />
    public bool KeepsSourceExtension => true;

    /// <summary>SMS Plus GX, bundled; the play page names a <c>.sc</c> release <c>.sg</c> so it runs in SG-1000 mode (S7).</summary>
    public string BrowserCore => "smsplus";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
