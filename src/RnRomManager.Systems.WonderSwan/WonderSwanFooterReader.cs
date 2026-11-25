using System.Buffers.Binary;
using System.Globalization;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.WonderSwan;

/// <summary>
/// Reads the 16-byte WonderSwan cartridge footer at the end of the ROM (Beetle WonderSwan's <c>libretro.c</c> reads
/// bytes 6–15 as its <c>header</c>). Never throws: only files shorter than the footer return null.
/// </summary>
public static class WonderSwanFooterReader
{
    public const string WonderSwanId = "ws";
    public const string WonderSwanColorId = "wsc";
    public const int FooterSize = 16;

    /// <summary>The reset vector's <c>JMP FAR</c>: every cart needs it to boot.</summary>
    private const byte JmpFar = 0xEA;

    /// <returns>null when <paramref name="rom"/> is shorter than 16 bytes.</returns>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < FooterSize)
            return null;

        var f = rom[^FooterSize..];
        var colour = f[0x7] == 0x01;
        var checksum = BinaryPrimitives.ReadUInt16LittleEndian(f[0xE..]);

        var fields = new Dictionary<string, object?>
        {
            ["publisherId"] = Hex(f[0x6]),
            ["colour"] = colour,
            ["gameId"] = (int)f[0x8],
            ["version"] = (int)f[0x9],
            ["romSize"] = Hex(f[0xA]),
            ["saveType"] = Hex(f[0xB]),
            ["vertical"] = (f[0xC] & 0x01) != 0,
            ["mapper"] = Hex(f[0xD]),
            ["checksum"] = "0x" + checksum.ToString("X4", CultureInfo.InvariantCulture),
            ["checksumValid"] = Sum(rom[..^2]) == checksum,
        };

        return new RomHeader(colour ? WonderSwanColorId : WonderSwanId, null, f[0x0] == JmpFar, fields);
    }

    private static string Hex(byte value) => "0x" + value.ToString("X2", CultureInfo.InvariantCulture);

    private static ushort Sum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0u;
        foreach (var b in bytes)
            sum += b;
        return (ushort)sum;
    }
}
