using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// The optional <c>TMR SEGA</c> header shared by Master System and Game Gear carts (Game Gear spec G3). The caller's
/// folder decides the system, so the result is classified as <paramref name="systemId"/> whatever the region nibble.
/// </summary>
internal static class TmrSegaHeader
{
    private static readonly int[] HeaderOffsets = [0x7FF0, 0x3FF0, 0x1FF0];
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("TMR SEGA");

    public static RomHeader? Read(ReadOnlySpan<byte> rom, string systemId)
    {
        foreach (var offset in HeaderOffsets)
        {
            if (rom.Length < offset + 16 || !rom.Slice(offset, Signature.Length).SequenceEqual(Signature))
                continue;
            var h = rom.Slice(offset, 16);
            var productCode = ($"{h[14] >> 4:X}{h[13]:X2}{h[12]:X2}").TrimStart('0');
            return new RomHeader(systemId, null, true, new Dictionary<string, object?>
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
}
