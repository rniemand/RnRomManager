using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega Master System (spec M1–M3). Same optional <c>TMR SEGA</c> header as Game Gear; a missing header is not invalid,
/// so an unmatched file goes to Review. Sizes come from the libretro <c>Sega - Master System - Mark III</c> DAT; <c>.bin</c>, <c>.rom</c> and TecToy's <c>.bms</c> are aliases.
/// </summary>
public sealed class MasterSystemHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(MasterSystemHandler), "boxart.mastersystem.png");

    public string Id => "sms";
    public string DisplayName => "Master System";
    public string ShortName => "SMS";
    public string Family => "mastersystem";
    public IReadOnlyList<string> RomExtensions { get; } = [".sms", ".bin", ".rom", ".bms"];
    public long MinRomSizeBytes => 2048;
    public long MaxRomSizeBytes => 4 * 1024 * 1024;
    public string DatName => "Sega - Master System - Mark III";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public string BrowserCore => "smsplus";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => TmrSegaHeader.Read(rom, Id);
}
