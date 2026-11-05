using System.Globalization;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.GameBoy;

/// <summary>
/// Reads the GBA cartridge header at 0x00–0xBF (GBATEK "GBA Cartridge Header"). Never throws on bad input:
/// garbage just yields invalid flags; only files too small to hold a header return null.
/// </summary>
public static class GameBoyAdvanceHeaderReader
{
    public const string GameBoyAdvanceId = "gba";
    public const int HeaderEnd = 0xC0;

    /// <summary>The 156-byte compressed logo bitmap at 0x04 (GBATEK; matches devkitPro gbafix).</summary>
    public static ReadOnlySpan<byte> NintendoLogo =>
    [
        0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21, 0x3D, 0x84, 0x82, 0x0A, 0x84, 0xE4, 0x09, 0xAD,
        0x11, 0x24, 0x8B, 0x98, 0xC0, 0x81, 0x7F, 0x21, 0xA3, 0x52, 0xBE, 0x19, 0x93, 0x09, 0xCE, 0x20,
        0x10, 0x46, 0x4A, 0x4A, 0xF8, 0x27, 0x31, 0xEC, 0x58, 0xC7, 0xE8, 0x33, 0x82, 0xE3, 0xCE, 0xBF,
        0x85, 0xF4, 0xDF, 0x94, 0xCE, 0x4B, 0x09, 0xC1, 0x94, 0x56, 0x8A, 0xC0, 0x13, 0x72, 0xA7, 0xFC,
        0x9F, 0x84, 0x4D, 0x73, 0xA3, 0xCA, 0x9A, 0x61, 0x58, 0x97, 0xA3, 0x27, 0xFC, 0x03, 0x98, 0x76,
        0x23, 0x1D, 0xC7, 0x61, 0x03, 0x04, 0xAE, 0x56, 0xBF, 0x38, 0x84, 0x00, 0x40, 0xA7, 0x0E, 0xFD,
        0xFF, 0x52, 0xFE, 0x03, 0x6F, 0x95, 0x30, 0xF1, 0x97, 0xFB, 0xC0, 0x85, 0x60, 0xD6, 0x80, 0x25,
        0xA9, 0x63, 0xBE, 0x03, 0x01, 0x4E, 0x38, 0xE2, 0xF9, 0xA2, 0x34, 0xFF, 0xBB, 0x3E, 0x03, 0x44,
        0x78, 0x00, 0x90, 0xCB, 0x88, 0x11, 0x3A, 0x94, 0x65, 0xC0, 0x7C, 0x63, 0x87, 0xF0, 0x3C, 0xAF,
        0xD6, 0x25, 0xE4, 0x8B, 0x38, 0x0A, 0xAC, 0x72, 0x21, 0xD4, 0xF8, 0x07,
    ];

    private const int LogoOffset = 0x04;

    /// <returns>null when <paramref name="rom"/> is shorter than 0xC0 bytes.</returns>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < HeaderEnd)
            return null;

        var title = ReadTitle(rom.Slice(0xA0, 12));
        var gameCode = ReadCode(rom.Slice(0xAC, 4));
        var makerCode = ReadCode(rom.Slice(0xB0, 2));
        var logoValid = rom.Slice(LogoOffset, NintendoLogo.Length).SequenceEqual(NintendoLogo);
        var headerChecksumValid = HeaderChecksum(rom) == rom[0xBD];

        var fields = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["gameCode"] = gameCode,
            ["destination"] = gameCode is null ? null : Destination(gameCode[3]),
            ["makerCode"] = makerCode,
            ["maker"] = makerCode is null ? null
                : GameBoyHeaderTables.NewLicensees.GetValueOrDefault(makerCode) ?? $"Unknown (0x{rom[0xB0]:X2}{rom[0xB1]:X2})",
            ["unitCode"] = Hex(rom[0xB3]),
            ["deviceType"] = Hex(rom[0xB4]),
            ["version"] = (int)rom[0xBC],
            ["fixedValueValid"] = rom[0xB2] == 0x96,
            ["logoValid"] = logoValid,
            ["headerChecksumValid"] = headerChecksumValid,
            ["romSizeBytes"] = rom.Length,
        };

        return new RomHeader(GameBoyAdvanceId, title, logoValid || headerChecksumValid, fields);
    }

    /// <summary>GBATEK: <c>chk = 0; for 0xA0..0xBC: chk -= rom[i]; chk -= 0x19</c>.</summary>
    private static byte HeaderChecksum(ReadOnlySpan<byte> rom)
    {
        byte chk = 0;
        for (var i = 0xA0; i <= 0xBC; i++)
            chk -= rom[i];
        return (byte)(chk - 0x19);
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

    private static string Destination(char code) => code switch
    {
        'J' => "Japan",
        'E' => "USA",
        'P' or 'X' or 'Y' => "Europe",
        'D' => "Germany",
        'F' => "France",
        'I' => "Italy",
        'S' => "Spain",
        'H' => "Netherlands",
        'K' => "Korea",
        _ => $"Unknown ({code})",
    };

    private static string Hex(byte value) => "0x" + value.ToString("X2", CultureInfo.InvariantCulture);
}
