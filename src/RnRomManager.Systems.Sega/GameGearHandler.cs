using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega Game Gear (spec G2, G3). The <c>TMR SEGA</c> header is optional (many Japanese carts have none), so a missing
/// header is not invalid: an unmatched file goes to Review. <c>.sms</c> covers the SMS-mode games in the libretro
/// <c>Sega - Game Gear</c> DAT (8 KiB–1 MiB).
/// </summary>
public sealed class GameGearHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.gamegear.png"));

    public string Id => "gg";
    public string DisplayName => "Game Gear";
    public string ShortName => "GG";
    public string Family => "gamegear";

    /// <summary><c>[0]</c> (<c>.gg</c>) is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".gg", ".sms"];

    public long MinRomSizeBytes => 8192;

    public long MaxRomSizeBytes => 1024 * 1024;

    public string DatName => "Sega - Game Gear";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public string BrowserCore => "smsplus";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => TmrSegaHeader.Read(rom, Id);
}
