using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NintendoDisc;

/// <summary>
/// The GameCube/Wii disc header (disc systems B6), shared by both handlers. The magic word decides the system, so a Wii
/// disc dropped in the GameCube folder is classified as <c>wii</c>; with neither magic the result is not plausible and
/// is classified as the caller. A span too short for a field gives null for that field; it never throws.
/// </summary>
public static class NintendoDiscHeader
{
    private static readonly byte[] GameCubeMagic = [0xC2, 0x33, 0x9F, 0x3D];
    private static readonly byte[] WiiMagic = [0x5D, 0x1C, 0x9E, 0xA3];
    private const int GameCubeMagicOffset = 0x1C;
    private const int WiiMagicOffset = 0x18;
    private const int TitleOffset = 0x20;
    private const int TitleMaxBytes = 0x3E0;

    public static RomHeader Read(ReadOnlySpan<byte> rom, string callerId)
    {
        var classified = Has(rom, WiiMagicOffset, WiiMagic) ? "wii" : Has(rom, GameCubeMagicOffset, GameCubeMagic) ? "gc" : null;
        var title = Title(rom);
        return new RomHeader(classified ?? callerId, title, classified is not null, new Dictionary<string, object?>
        {
            ["gameId"] = GameId(rom),
            ["discNumber"] = rom.Length > 6 ? rom[6] : null,
            ["version"] = rom.Length > 7 ? rom[7] : null,
            ["title"] = title,
        });
    }

    private static bool Has(ReadOnlySpan<byte> rom, int offset, byte[] magic) =>
        rom.Length >= offset + magic.Length && rom.Slice(offset, magic.Length).SequenceEqual(magic);

    /// <summary>The 6 ASCII bytes at 0x00, or null when one isn't printable.</summary>
    private static string? GameId(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 6)
            return null;
        foreach (var b in rom[..6])
            if (b is < 0x20 or > 0x7E)
                return null;
        return Encoding.ASCII.GetString(rom[..6]);
    }

    /// <summary>0x20 up to the first NUL (at most 0x3E0 bytes) as Latin-1, trimmed; null when cut short or empty.</summary>
    private static string? Title(ReadOnlySpan<byte> rom)
    {
        if (rom.Length <= TitleOffset)
            return null;
        var field = rom[TitleOffset..Math.Min(rom.Length, TitleOffset + TitleMaxBytes)];
        var nul = field.IndexOf((byte)0);
        if (nul < 0 && field.Length < TitleMaxBytes)
            return null; // the span ends inside the title
        var title = Encoding.Latin1.GetString(nul < 0 ? field : field[..nul]).Trim();
        return title.Length == 0 ? null : title;
    }
}
