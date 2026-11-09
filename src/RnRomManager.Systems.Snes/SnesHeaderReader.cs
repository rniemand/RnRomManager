using System.Buffers.Binary;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Snes;

/// <summary>
/// Reads the internal cartridge header of a headerless SNES image (SNES spec S4): LoROM at <c>0x7FC0</c>, HiROM at
/// <c>0xFFC0</c>, ExHiROM at <c>0x40FFC0</c>. Never throws; returns null only when no candidate fits in the ROM.
/// </summary>
public static class SnesHeaderReader
{
    public const string SnesId = "snes";
    private const int HeaderLength = 0x40;
    private const int TitleLength = 21;

    private static readonly (string Mapping, int Offset)[] Default = [("LoROM", 0x7FC0), ("HiROM", 0xFFC0), ("ExHiROM", 0x40FFC0)];

    // ExHiROM dumps can hold bank data that looks like a header at 0xFFC0, so large ROMs try it first.
    private static readonly (string Mapping, int Offset)[] Large = [("ExHiROM", 0x40FFC0), ("LoROM", 0x7FC0), ("HiROM", 0xFFC0)];

    private static readonly string?[] Regions =
    [
        "Japan", "USA", "Europe", "Sweden", "Finland", "Denmark", "France", "Netherlands", "Spain", "Germany", "Italy",
        "China", "Indonesia", "Korea", null, "Canada", "Brazil", "Australia",
    ];

    /// <returns>null when the ROM is too short for any candidate (under 32 KiB).</returns>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        var anyFits = false;
        (string Mapping, int Offset)? byChecksum = null, byTitle = null;
        foreach (var candidate in rom.Length > 4 * 1024 * 1024 ? Large : Default)
        {
            if (rom.Length < candidate.Offset + HeaderLength)
                continue;
            anyFits = true;
            var h = rom.Slice(candidate.Offset, HeaderLength);
            if (byChecksum is null && ChecksumValid(h))
                byChecksum = candidate;
            if (byTitle is null && TitleOk(h[..TitleLength]) && MapModeFits(h[0x15], candidate.Mapping))
                byTitle = candidate;
        }
        if (!anyFits)
            return null;

        var chosen = byChecksum ?? byTitle;
        var fields = new Dictionary<string, object?>
        {
            ["headerValid"] = chosen is not null,
            ["title"] = null,
            ["mapping"] = null,
            ["fastRom"] = null,
            ["romType"] = null,
            ["romSizeKiB"] = null,
            ["sramSizeKiB"] = null,
            ["region"] = null,
            ["version"] = null,
            ["checksum"] = null,
            ["checksumValid"] = null,
            ["romSizeBytes"] = rom.Length,
        };
        string? title = null;
        if (chosen is { } c)
        {
            var h = rom.Slice(c.Offset, HeaderLength);
            title = ReadTitle(h[..TitleLength]);
            fields["title"] = title;
            fields["mapping"] = c.Mapping;
            fields["fastRom"] = (h[0x15] & 0x10) != 0;
            fields["romType"] = (int)h[0x16];
            fields["romSizeKiB"] = h[0x17] is >= 1 and <= 13 ? 1 << h[0x17] : null;
            fields["sramSizeKiB"] = h[0x18] switch { 0 => 0, <= 10 => 1 << h[0x18], _ => null };
            fields["region"] = h[0x19] < Regions.Length ? Regions[h[0x19]] : null;
            fields["version"] = (int)h[0x1B];
            fields["checksum"] = BinaryPrimitives.ReadUInt16LittleEndian(h[0x1E..]).ToString("X4");
            fields["checksumValid"] = ChecksumValid(h);
        }

        return new RomHeader(SnesId, title, chosen is not null, fields);
    }

    /// <summary>Checksum + complement = <c>0xFFFF</c>.</summary>
    private static bool ChecksumValid(ReadOnlySpan<byte> h) =>
        BinaryPrimitives.ReadUInt16LittleEndian(h[0x1E..]) + BinaryPrimitives.ReadUInt16LittleEndian(h[0x1C..]) == 0xFFFF;

    private static bool TitleOk(ReadOnlySpan<byte> title) =>
        !title.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E) && title.ContainsAnyExcept((byte)' ');

    private static bool MapModeFits(byte mapMode, string mapping) =>
        (mapMode & 0x20) != 0 && (mapping, mapMode & 0x0F) is ("LoROM", 0x0 or 0x2 or 0x3) or ("HiROM", 0x1 or 0xA) or ("ExHiROM", 0x5);

    /// <summary>ASCII up to the first NUL, non-printable → <c>?</c>, trimmed; null when nothing is left (the GB rule).</summary>
    private static string? ReadTitle(ReadOnlySpan<byte> area)
    {
        var end = area.IndexOf((byte)0);
        var chars = (end < 0 ? area : area[..end]).ToArray().Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '?').ToArray();
        var title = new string(chars).Trim();
        return title.Length > 0 ? title : null;
    }
}
