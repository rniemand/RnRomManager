using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Snes;

/// <summary>
/// Sufami Turbo mini-carts. Matched against libretro's No-Intro <c>Nintendo - Sufami Turbo</c> DAT. The size range and
/// <see cref="ReadHeader"/> follow Snes9x 1.63's <c>is_SufamiTurbo_Cart</c> (512 KiB–1 MiB, <c>BANDAI SFC-ADX</c> at 0 and
/// not <c>SFC-ADX BACKUP</c> at 0x10, which marks the BIOS), so an unmatched file Snes9x wouldn't take as a Sufami Turbo
/// cart fails <c>InvalidHeader</c>. Snes9x plays it with <c>STBIOS.bin</c> from <c>Paths:Bios</c>.
/// </summary>
public sealed class SufamiTurboHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(SufamiTurboHandler), "boxart.sufami.png");

    public string Id => "sufami";
    public string DisplayName => "Sufami Turbo";
    public string ShortName => "ST";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".st"];
    public long MinRomSizeBytes => 512 * 1024;
    public long MaxRomSizeBytes => 1024 * 1024;
    public string DatName => "Nintendo - Sufami Turbo";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>Snes9x (operator-supplied), which needs <c>STBIOS.bin</c>.</summary>
    public string BrowserCore => "snes9x";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom)
    {
        var plausible = rom.Length >= 0x1E && rom[..14].SequenceEqual("BANDAI SFC-ADX"u8)
            && !rom.Slice(0x10, 14).SequenceEqual("SFC-ADX BACKUP"u8);
        return new RomHeader(Id, null, plausible, new Dictionary<string, object?> { ["headerValid"] = plausible });
    }
}
