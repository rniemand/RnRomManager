using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nintendo64;

/// <summary>
/// Reads the N64 ROM header at 0x00–0x3F of a big-endian dump (n64brew "ROM Header", N4). Never throws on bad input;
/// only files too small to hold a header return null.
/// </summary>
public static class Nintendo64HeaderReader
{
    public const string Nintendo64Id = "n64";
    public const int HeaderEnd = 0x40;

    /// <returns>null when <paramref name="rom"/> is shorter than 0x40 bytes.</returns>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < HeaderEnd)
            return null;

        var title = ReadTitle(rom.Slice(0x20, 20));
        var gameCode = ReadCode(rom.Slice(0x3B, 4));
        var headerValid = rom[0] == 0x80;

        var fields = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["gameCode"] = gameCode,
            ["mediaType"] = gameCode is null ? null : MediaType(gameCode[0]),
            ["destination"] = gameCode is null ? null : Destination(gameCode[3]),
            ["version"] = (int)rom[0x3F],
            ["crc1"] = Hex32(rom[0x10..]),
            ["crc2"] = Hex32(rom[0x14..]),
            ["bootAddress"] = Hex32(rom[0x08..]),
            ["headerValid"] = headerValid,
            ["romSizeBytes"] = rom.Length,
        };

        return new RomHeader(Nintendo64Id, title, headerValid, fields);
    }

    /// <summary>ASCII up to the first NUL, non-printable → <c>?</c>, trimmed; null when nothing is left (the GB rule).</summary>
    private static string? ReadTitle(ReadOnlySpan<byte> area)
    {
        var end = area.IndexOf((byte)0);
        var chars = (end < 0 ? area : area[..end]).ToArray().Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '?').ToArray();
        var title = new string(chars).Trim();
        return title.Length > 0 ? title : null;
    }

    private static string? ReadCode(ReadOnlySpan<byte> area) =>
        area.ToArray().All(b => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9')
            ? Encoding.ASCII.GetString(area)
            : null;

    private static string MediaType(char code) => code switch
    {
        'N' => "Cartridge",
        'C' => "Cartridge (expandable)",
        'D' => "64DD disk",
        'E' => "64DD expansion",
        'Z' => "Aleck64",
        _ => $"Unknown ({code})",
    };

    private static string Destination(char code) => code switch
    {
        'A' => "Asia",
        'B' => "Brazil",
        'C' => "China",
        'D' => "Germany",
        'E' => "USA",
        'F' => "France",
        'H' => "Netherlands",
        'I' => "Italy",
        'J' => "Japan",
        'K' => "Korea",
        'N' => "Canada",
        'P' or 'X' or 'Y' => "Europe",
        'S' => "Spain",
        'U' => "Australia",
        'W' => "Scandinavia",
        _ => $"Unknown ({code})",
    };

    private static string Hex32(ReadOnlySpan<byte> at) =>
        "0x" + BinaryPrimitives.ReadUInt32BigEndian(at).ToString("X8", CultureInfo.InvariantCulture);
}
