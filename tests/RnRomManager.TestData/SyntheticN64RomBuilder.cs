using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.TestData;

/// <summary>The byte order <see cref="SyntheticN64RomBuilder"/> writes (TestData doesn't reference the Systems project).</summary>
public enum N64ByteOrder
{
    /// <summary>Big-endian (<c>.z64</c>).</summary>
    Z64,

    /// <summary>Byte-swapped 16-bit pairs (<c>.v64</c>).</summary>
    V64,

    /// <summary>Little-endian 32-bit words (<c>.n64</c>).</summary>
    N64,
}

/// <summary>
/// Deterministic, non-copyrighted N64 ROMs for tests and seed sets. The body is seeded random bytes; the header is
/// written over it in big-endian, and the whole ROM is converted to <see cref="ByteOrder"/> last, by its own swap code
/// so tests of the handler's conversion don't test it against itself. Same properties → same bytes.
/// </summary>
public sealed record SyntheticN64RomBuilder
{
    public const int HeaderEnd = 0x40;

    public int Seed { get; init; }

    public int SizeBytes { get; init; } = 1024 * 1024;

    /// <summary>Up to 20 ASCII chars, space-padded as on real carts; null writes zero bytes.</summary>
    public string? Title { get; init; }

    /// <summary>4 ASCII chars at 0x3B; null writes zero bytes.</summary>
    public string? GameCode { get; init; }

    public byte Version { get; init; }

    public uint Crc1 { get; init; }

    public uint Crc2 { get; init; }

    public uint BootAddress { get; init; } = 0x80000400;

    /// <summary>Writes <c>0x00</c> instead of <c>0x80</c> at byte 0, so the header is implausible.</summary>
    public bool CorruptMagic { get; init; }

    public N64ByteOrder ByteOrder { get; init; } = N64ByteOrder.Z64;

    public byte[] Build()
    {
        var rom = new byte[SizeBytes];
        // System.Random with a seed uses the legacy, fixed algorithm, so the bytes are stable across runtimes.
        new Random(Seed).NextBytes(rom);
        if (SizeBytes < HeaderEnd)
            return rom; // too small to hold a header: just noise (tests for TooSmall)

        // PI config word: 0x80 first in big-endian (the byte-order marker), the rest as on most carts.
        rom[0] = CorruptMagic ? (byte)0x00 : (byte)0x80;
        rom[1] = 0x37;
        rom[2] = 0x12;
        rom[3] = 0x40;
        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x08), BootAddress);
        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x10), Crc1);
        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x14), Crc2);
        var title = rom.AsSpan(0x20, 20);
        if (Title is null)
            title.Clear();
        else
        {
            title.Fill((byte)' ');
            Encoding.ASCII.GetBytes(Title.Length > 20 ? Title[..20] : Title, title);
        }
        var code = rom.AsSpan(0x3B, 4);
        code.Clear();
        if (GameCode is not null)
            Encoding.ASCII.GetBytes(GameCode.Length > 4 ? GameCode[..4] : GameCode, code);
        rom[0x3F] = Version;

        return ByteOrder switch
        {
            N64ByteOrder.V64 => Reorder(rom, [1, 0]),
            N64ByteOrder.N64 => Reorder(rom, [3, 2, 1, 0]),
            _ => rom,
        };
    }

    /// <summary>Output byte <c>i + k</c> = input byte <c>i + map[k]</c> for every group of <c>map.Length</c> bytes.</summary>
    private static byte[] Reorder(byte[] rom, int[] map)
    {
        var result = new byte[rom.Length];
        for (var i = 0; i + map.Length <= rom.Length; i += map.Length)
            for (var k = 0; k < map.Length; k++)
                result[i + k] = rom[i + map[k]];
        return result;
    }
}
