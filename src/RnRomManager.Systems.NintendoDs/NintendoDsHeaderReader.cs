using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NintendoDs;

/// <summary>
/// Reads the DS cartridge header (ndstool <c>header.h</c>). Never throws on bad input: garbage just yields an invalid
/// header CRC; only spans too short to hold the CRC return null.
/// </summary>
public static class NintendoDsHeaderReader
{
    public const string NintendoDsId = "nds";

    /// <summary>The header CRC covers 0x000–0x15D and is stored little-endian at 0x15E.</summary>
    public const int HeaderCrcOffset = 0x15E;

    public const int HeaderEnd = 0x160;

    /// <returns>null when <paramref name="rom"/> is shorter than 0x160 bytes.</returns>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < HeaderEnd)
            return null;

        var title = ReadTitle(rom[..12]);
        var headerChecksumValid = HeaderCrc(rom[..HeaderCrcOffset]) == (rom[HeaderCrcOffset] | rom[HeaderCrcOffset + 1] << 8);
        var fields = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["gameCode"] = Text(rom.Slice(0x0C, 4)),
            ["makerCode"] = Text(rom.Slice(0x10, 2)),
            ["unitCode"] = (int)rom[0x12],
            ["romVersion"] = (int)rom[0x1E],
            ["headerChecksumValid"] = headerChecksumValid,
        };
        return new RomHeader(NintendoDsId, title, headerChecksumValid, fields);
    }

    /// <summary>CRC-16, initial value 0xFFFF, reflected polynomial 0xA001 (ndstool <c>CalcCrc</c>).</summary>
    public static ushort HeaderCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }

    /// <summary>Up to the first NUL, trailing spaces trimmed; null when nothing is left.</summary>
    private static string? ReadTitle(ReadOnlySpan<byte> area)
    {
        var end = area.IndexOf((byte)0);
        var title = Text(end < 0 ? area : area[..end]).TrimEnd(' ');
        return title.Length > 0 ? title : null;
    }

    /// <summary>Bytes 0x20–0x7E kept; every other byte becomes <c>?</c>.</summary>
    private static string Text(ReadOnlySpan<byte> area)
    {
        var chars = new char[area.Length];
        for (var i = 0; i < area.Length; i++)
            chars[i] = area[i] is >= 0x20 and <= 0x7E ? (char)area[i] : '?';
        return new string(chars);
    }
}
