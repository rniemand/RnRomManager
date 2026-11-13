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
    private const int WbfsCopyBytes = 0x100;
    private static readonly byte[] WbfsMagic = "WBFS"u8.ToArray();

    /// <summary>
    /// A WBFS container (B2: <c>WBFS</c> at 0x00, sector shift 9–12 at 0x08) is read from its 0x100-byte disc-header
    /// copy at <c>1 &lt;&lt; shift</c> (libwbfs <c>wbfs_disc_info_t.dhead</c>). <c>container</c> is <c>wbfs</c> or <c>iso</c>.
    /// </summary>
    public static RomHeader Read(ReadOnlySpan<byte> rom, string callerId)
    {
        if (!Has(rom, 0, WbfsMagic))
            return Read(rom, callerId, "iso", TitleMaxBytes);
        if (rom.Length <= 8 || rom[8] is < 9 or > 12 || rom.Length < (1 << rom[8]) + WbfsCopyBytes)
            return new RomHeader(callerId, null, false, Fields(null, null, null, null, "wbfs"));
        // The copy is complete, so a NUL-less title filling its 0xE0 bytes is kept whole.
        return Read(rom.Slice(1 << rom[8], WbfsCopyBytes), callerId, "wbfs", WbfsCopyBytes - TitleOffset);
    }

    private static RomHeader Read(ReadOnlySpan<byte> rom, string callerId, string container, int titleMaxBytes)
    {
        var classified = Has(rom, WiiMagicOffset, WiiMagic) ? "wii" : Has(rom, GameCubeMagicOffset, GameCubeMagic) ? "gc" : null;
        var title = Title(rom, titleMaxBytes);
        return new RomHeader(classified ?? callerId, title, classified is not null,
            Fields(GameId(rom), rom.Length > 6 ? rom[6] : null, rom.Length > 7 ? rom[7] : null, title, container));
    }

    private static Dictionary<string, object?> Fields(string? gameId, byte? discNumber, byte? version, string? title, string container) => new()
    {
        ["gameId"] = gameId,
        ["discNumber"] = discNumber,
        ["version"] = version,
        ["title"] = title,
        ["container"] = container,
    };

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

    /// <summary>0x20 up to the first NUL (at most <paramref name="maxBytes"/>) as Latin-1, trimmed; null when cut short or empty.</summary>
    private static string? Title(ReadOnlySpan<byte> rom, int maxBytes)
    {
        if (rom.Length <= TitleOffset)
            return null;
        var field = rom[TitleOffset..Math.Min(rom.Length, TitleOffset + maxBytes)];
        var nul = field.IndexOf((byte)0);
        if (nul < 0 && field.Length < maxBytes)
            return null; // the span ends inside the title
        var title = Encoding.Latin1.GetString(nul < 0 ? field : field[..nul]).Trim();
        return title.Length == 0 ? null : title;
    }
}
