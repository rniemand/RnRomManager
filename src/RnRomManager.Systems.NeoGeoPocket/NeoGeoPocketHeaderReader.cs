using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NeoGeoPocket;

/// <summary>
/// Reads the 64-byte NGP/NGPC cartridge header (Beetle NeoPop's <c>RomHeader</c>, <c>mednafen/ngp/rom.h</c>).
/// Never throws: only files too small to hold a header return null.
/// </summary>
public static class NeoGeoPocketHeaderReader
{
    public const string NeoGeoPocketId = "ngp";
    public const string NeoGeoPocketColorId = "ngpc";
    public const int HeaderEnd = 0x40;

    private const int LicenceLength = 28;
    private const string Copyright = "COPYRIGHT BY SNK CORPORATION";
    private const string Licensed = " LICENSED BY SNK CORPORATION";
    private const byte ColourMode = 0x10;

    /// <returns>null when <paramref name="rom"/> is shorter than 0x40 bytes.</returns>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < HeaderEnd)
            return null;

        // The BIOS checks one of these two strings to boot the cart.
        var licenceBytes = rom[..LicenceLength];
        var licence = licenceBytes.SequenceEqual(Encoding.ASCII.GetBytes(Copyright)) ? Copyright
            : licenceBytes.SequenceEqual(Encoding.ASCII.GetBytes(Licensed)) ? Licensed.Trim()
            : null;
        var mode = rom[0x23];
        var colour = mode == ColourMode;
        var title = ReadTitle(rom[0x24..0x30]);

        var fields = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["licence"] = licence,
            ["catalog"] = (int)BinaryPrimitives.ReadUInt16LittleEndian(rom[0x20..]),
            ["subCatalog"] = (int)rom[0x22],
            ["mode"] = "0x" + mode.ToString("X2", CultureInfo.InvariantCulture),
            ["colour"] = colour,
            ["startPc"] = "0x" + BinaryPrimitives.ReadUInt32LittleEndian(rom[0x1C..]).ToString("X8", CultureInfo.InvariantCulture),
        };

        return new RomHeader(colour ? NeoGeoPocketColorId : NeoGeoPocketId, title, licence is not null, fields);
    }

    /// <summary>ASCII up to the first NUL, non-printable → <c>?</c>, trailing spaces trimmed; null when nothing is left.</summary>
    private static string? ReadTitle(ReadOnlySpan<byte> area)
    {
        var end = area.IndexOf((byte)0);
        var chars = (end < 0 ? area : area[..end]).ToArray().Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '?').ToArray();
        var title = new string(chars).TrimEnd(' ');
        return title.Length > 0 ? title : null;
    }
}
