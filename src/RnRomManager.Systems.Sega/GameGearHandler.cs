using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega Game Gear (spec G2, G3). The <c>TMR SEGA</c> header is optional (many Japanese carts have none), so a missing
/// header is not invalid: an unmatched file goes to Review. <c>.sms</c> covers the SMS-mode games in the libretro
/// <c>Sega - Game Gear</c> DAT (8 KiB–1 MiB).
/// </summary>
public sealed class GameGearHandler : ISystemHandler
{
    private static readonly int[] HeaderOffsets = [0x7FF0, 0x3FF0, 0x1FF0];
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("TMR SEGA");

    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => LoadResource("boxart.gamegear.png"));

    public string Id => "gg";
    public string DisplayName => "Game Gear";
    public string ShortName => "GG";
    public string Family => "gamegear";

    /// <summary><c>[0]</c> (<c>.gg</c>) is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".gg", ".sms"];

    public long MinRomSizeBytes => 8192;

    public long MaxRomSizeBytes => 1024 * 1024;

    public string DatName => "Sega - Game Gear";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom)
    {
        foreach (var offset in HeaderOffsets)
        {
            if (rom.Length < offset + 16 || !rom.Slice(offset, Signature.Length).SequenceEqual(Signature))
                continue;
            var h = rom.Slice(offset, 16);
            var productCode = ($"{h[14] >> 4:X}{h[13]:X2}{h[12]:X2}").TrimStart('0');
            return new RomHeader(Id, null, true, new Dictionary<string, object?>
            {
                ["offset"] = offset.ToString("X4"),
                ["checksum"] = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]).ToString("X4"),
                ["productCode"] = productCode.Length == 0 ? "0" : productCode,
                ["version"] = h[14] & 0x0F,
                ["region"] = Region(h[15] >> 4),
                ["sizeCode"] = h[15] & 0x0F,
            });
        }
        return null;
    }

    private static string Region(int nibble) => nibble switch
    {
        3 => "SMS Japan",
        4 => "SMS Export",
        5 => "GG Japan",
        6 => "GG Export",
        7 => "GG International",
        _ => nibble.ToString("X"),
    };

    private static byte[] LoadResource(string name)
    {
        using var stream = typeof(GameGearHandler).Assembly.GetManifestResourceStream($"RnRomManager.Systems.Sega.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
