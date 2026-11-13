using System.Text;

namespace RnRomManager.TestData;

/// <summary>Which GameCube/Wii magic word a synthetic disc carries (disc systems B6).</summary>
public enum NintendoDiscMagic
{
    None,
    GameCube,
    Wii,
}

/// <summary>
/// Deterministic disc image bytes for the six disc systems (a <c>.iso</c> or a <c>.bin</c> track). With a
/// <see cref="NintendoDiscMagic"/>, the GameCube/Wii header is written over the start: game id at 0x00, disc number and
/// version at 0x06/0x07, the magic, and the title at 0x20 ending in a NUL. <see cref="Wbfs"/> wraps it in a WBFS container.
/// </summary>
public sealed record SyntheticDiscBuilder
{
    public int Seed { get; init; }
    public int Size { get; init; } = 64 * 1024;
    public NintendoDiscMagic Magic { get; init; }
    public string GameId { get; init; } = "TEST01";
    public string Title { get; init; } = "TEST DISC";
    public byte DiscNumber { get; init; }
    public byte Version { get; init; }

    public byte[] Build()
    {
        var disc = new byte[Size];
        new Random(Seed).NextBytes(disc);
        if (Magic == NintendoDiscMagic.None)
            return disc;
        disc.AsSpan(0, 0x400).Clear();
        Encoding.ASCII.GetBytes(GameId).CopyTo(disc, 0);
        disc[6] = DiscNumber;
        disc[7] = Version;
        byte[] magic = Magic == NintendoDiscMagic.Wii ? [0x5D, 0x1C, 0x9E, 0xA3] : [0xC2, 0x33, 0x9F, 0x3D];
        magic.CopyTo(disc, Magic == NintendoDiscMagic.Wii ? 0x18 : 0x1C);
        Encoding.Latin1.GetBytes(Title).CopyTo(disc, 0x20);
        return disc;
    }

    /// <summary>
    /// A WBFS file of <see cref="Size"/> bytes: <c>WBFS</c> at 0x00, <paramref name="shift"/> (<c>hd_sec_sz_s</c>) at 0x08,
    /// slot 0 used (0x0C = 1), and the built disc's first 0x100 bytes at <c>1 &lt;&lt; shift</c>; the rest is seeded filler.
    /// </summary>
    public byte[] Wbfs(int shift = 9)
    {
        var offset = 1 << shift;
        var wbfs = new byte[Size];
        new Random(Seed + 1).NextBytes(wbfs);
        wbfs.AsSpan(0, offset).Clear();
        "WBFS"u8.CopyTo(wbfs);
        wbfs[8] = (byte)shift;
        wbfs[0x0C] = 1;
        Build().AsSpan(0, 0x100).CopyTo(wbfs.AsSpan(offset));
        return wbfs;
    }

    /// <summary>A cue sheet naming <c>&lt;stem&gt; (Track 1).bin</c> (data) and <c>&lt;stem&gt; (Track 2).bin</c> (audio).</summary>
    public static byte[] Cue(string stem) => Encoding.UTF8.GetBytes(
        $"FILE \"{stem} (Track 1).bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
        $"FILE \"{stem} (Track 2).bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n");
}
