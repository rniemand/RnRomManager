using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// The Mega Drive header at <c>0x100</c>, shared by Mega Drive and 32X (32X spec X2): plausible when the system-type
/// field starts with <c>SEGA</c>; the result is classified as <c>systemId</c> whatever the system type says.
/// </summary>
internal static class MegaDriveHeader
{
    public static RomHeader? Read(ReadOnlySpan<byte> rom, string systemId)
    {
        if (rom.Length < 0x200)
            return null;
        var systemType = Text(rom.Slice(0x100, 16));
        if (systemType is null || !systemType.StartsWith("SEGA", StringComparison.Ordinal))
            return null;
        var domestic = Text(rom.Slice(0x120, 48));
        var overseas = Text(rom.Slice(0x150, 48));
        return new RomHeader(systemId, overseas ?? domestic, true, new Dictionary<string, object?>
        {
            ["systemType"] = systemType,
            ["copyright"] = Text(rom.Slice(0x110, 16)),
            ["domesticTitle"] = domestic,
            ["overseasTitle"] = overseas,
            ["serial"] = Text(rom.Slice(0x180, 14)),
            ["checksum"] = $"{rom[0x18E]:X2}{rom[0x18F]:X2}",
            ["region"] = Text(rom.Slice(0x1F0, 3)),
        });
    }

    /// <summary>ASCII with every byte outside <c>0x20–0x7E</c> as a space, runs of spaces collapsed, trimmed; empty → null.</summary>
    private static string? Text(ReadOnlySpan<byte> field)
    {
        var sb = new StringBuilder(field.Length);
        foreach (var b in field)
        {
            var c = b is >= 0x20 and <= 0x7E ? (char)b : ' ';
            if (c != ' ' || (sb.Length > 0 && sb[^1] != ' '))
                sb.Append(c);
        }
        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? null : text;
    }
}
