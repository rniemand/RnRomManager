using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.GameBoy;

/// <summary>
/// Reads the GB/GBC cartridge header at 0x0100–0x014F (spec §8, Pan Docs). Never throws on bad input:
/// garbage just yields invalid flags; only files too small to hold a header return null.
/// </summary>
public static class GameBoyHeaderReader
{
    public const string GameBoyId = "gb";
    public const string GameBoyColorId = "gbc";
    public const int HeaderEnd = 0x0150;

    /// <summary>The boot ROM's 48-byte logo bitmap at 0x0104 (Pan Docs).</summary>
    public static ReadOnlySpan<byte> NintendoLogo =>
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E, 0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    private const int LogoOffset = 0x0104;
    private const int Mbc1MulticartSize = 1024 * 1024;
    private const int Mbc1MulticartLogoOffset = 0x40104;

    /// <returns>null when <paramref name="rom"/> is shorter than 0x150 bytes.</returns>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < HeaderEnd)
            return null;

        var cgbFlag = rom[0x143];
        var isCgb = (cgbFlag & 0x80) != 0; // the CGB boot ROM checks only bit 7
        var manufacturerCode = isCgb && rom[0x13F..0x143].ToArray().All(b => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9')
            ? Encoding.ASCII.GetString(rom[0x13F..0x143])
            : null;
        var title = ReadTitle(rom.Slice(0x134, manufacturerCode is not null ? 11 : isCgb ? 15 : 16));

        var cartridgeType = rom[0x147];
        int? romSizeBytes = rom[0x148] <= 0x08 ? (32 * 1024) << rom[0x148] : null;
        var logoValid = rom.Slice(LogoOffset, NintendoLogo.Length).SequenceEqual(NintendoLogo);
        var headerChecksumValid = HeaderChecksum(rom) == rom[0x14D];

        var fields = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["manufacturerCode"] = manufacturerCode,
            ["cgbFlag"] = Hex(cgbFlag),
            ["sgbFlag"] = Hex(rom[0x146]),
            ["cartridgeType"] = GameBoyHeaderTables.CartridgeTypes.GetValueOrDefault(cartridgeType) ?? Unknown(cartridgeType),
            ["romSizeBytes"] = romSizeBytes,
            ["ramSizeBytes"] = RamSizeBytes(cartridgeType, rom[0x149]),
            ["destination"] = rom[0x14A] switch { 0x00 => "Japan", 0x01 => "Overseas", var d => Unknown(d) },
            ["licensee"] = Licensee(rom),
            ["version"] = (int)rom[0x14C],
            ["logoValid"] = logoValid,
            ["headerChecksumValid"] = headerChecksumValid,
            ["globalChecksumValid"] = GlobalChecksum(rom) == BinaryPrimitives.ReadUInt16BigEndian(rom[0x14E..]),
            ["sizeMatchesHeader"] = romSizeBytes == rom.Length,
            ["isMbc1Multicart"] = IsMbc1Multicart(rom, cartridgeType),
        };

        return new RomHeader(isCgb ? GameBoyColorId : GameBoyId, title, logoValid || headerChecksumValid, fields);
    }

    /// <summary>ASCII up to the first NUL, non-printable → <c>?</c>, trimmed; null when nothing is left.</summary>
    private static string? ReadTitle(ReadOnlySpan<byte> area)
    {
        var end = area.IndexOf((byte)0);
        var chars = (end < 0 ? area : area[..end]).ToArray().Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '?').ToArray();
        var title = new string(chars).Trim();
        return title.Length > 0 ? title : null;
    }

    private static string Licensee(ReadOnlySpan<byte> rom)
    {
        var old = rom[0x14B];
        if (old != 0x33)
            return GameBoyHeaderTables.OldLicensees.GetValueOrDefault(old) ?? Unknown(old);
        var code = Encoding.ASCII.GetString(rom[0x144..0x146]);
        return GameBoyHeaderTables.NewLicensees.GetValueOrDefault(code)
            ?? $"Unknown (0x{rom[0x144]:X2}{rom[0x145]:X2})";
    }

    private static int? RamSizeBytes(byte cartridgeType, byte code)
    {
        if (cartridgeType is 0x05 or 0x06)
            return 512; // MBC2: 512×4-bit built-in RAM; the header says 0
        return code switch
        {
            0x00 or 0x01 => 0,
            0x02 => 8 * 1024,
            0x03 => 32 * 1024,
            0x04 => 128 * 1024,
            0x05 => 64 * 1024,
            _ => null,
        };
    }

    /// <summary>§8.4: 1 MiB, an MBC1 type, and a second valid logo in bank 0x10.</summary>
    private static bool IsMbc1Multicart(ReadOnlySpan<byte> rom, byte cartridgeType) =>
        rom.Length == Mbc1MulticartSize
        && cartridgeType is 0x01 or 0x02 or 0x03
        && rom.Slice(Mbc1MulticartLogoOffset, NintendoLogo.Length).SequenceEqual(NintendoLogo);

    private static byte HeaderChecksum(ReadOnlySpan<byte> rom)
    {
        byte x = 0;
        for (var i = 0x134; i <= 0x14C; i++)
            x = (byte)(x - rom[i] - 1);
        return x;
    }

    private static ushort GlobalChecksum(ReadOnlySpan<byte> rom)
    {
        uint sum = 0;
        for (var i = 0; i < rom.Length; i++)
        {
            if (i is not (0x14E or 0x14F))
                sum += rom[i];
        }
        return (ushort)sum;
    }

    private static string Hex(byte value) => "0x" + value.ToString("X2", CultureInfo.InvariantCulture);

    private static string Unknown(byte value) => $"Unknown (0x{value:X2})";
}
