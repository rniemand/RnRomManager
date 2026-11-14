using System.Text;

namespace RnRomManager.TestData;

/// <summary>
/// Deterministic, non-copyrighted DS cartridge images for tests and seed sets. The 0x200-byte header area holds the
/// title, game code, maker code and unit code, then the header CRC (CRC-16, initial 0xFFFF, reflected 0xA001, over
/// 0x000–0x15D, stored little-endian at 0x15E). The bytes after it are seeded random bytes, or 0xFF. Same properties →
/// same bytes. <see cref="WriteTo"/> streams, so a 512 MiB image never sits in memory.
/// </summary>
public sealed record SyntheticNdsRomBuilder
{
    public const int GameCodeOffset = 0x0C;
    public const int MakerCodeOffset = 0x10;
    public const int UnitCodeOffset = 0x12;
    public const int CapacityOffset = 0x14;
    public const int RomVersionOffset = 0x1E;
    public const int HeaderCrcOffset = 0x15E;
    public const int HeaderEnd = 0x160;

    /// <summary>The header area; the body starts here.</summary>
    public const int HeaderAreaBytes = 0x200;

    private const int ChunkBytes = 1024 * 1024;

    public int Seed { get; init; }

    public long SizeBytes { get; init; } = 256 * 1024;

    /// <summary>Up to 12 ASCII chars, NUL-padded; null writes zero bytes.</summary>
    public string? Title { get; init; } = "SYNTHETIC";

    /// <summary>4 ASCII chars; null writes zero bytes.</summary>
    public string? GameCode { get; init; } = "ASYE";

    /// <summary>2 ASCII chars; null writes zero bytes.</summary>
    public string? MakerCode { get; init; } = "01";

    public byte UnitCode { get; init; }

    /// <summary>Device capacity at 0x14: the image size is <c>128 KiB &lt;&lt; Capacity</c>.</summary>
    public byte Capacity { get; init; }

    public byte RomVersion { get; init; }

    /// <summary>Writes a header CRC that is off by one.</summary>
    public bool BadHeaderCrc { get; init; }

    /// <summary>Fills everything after the header area with 0xFF instead of seeded bytes (compresses to almost nothing).</summary>
    public bool FillWithFF { get; init; }

    public byte[] Build()
    {
        if (SizeBytes > Array.MaxLength)
            throw new InvalidOperationException("Use WriteTo for images this large.");
        using var buffer = new MemoryStream((int)SizeBytes);
        WriteTo(buffer);
        return buffer.ToArray();
    }

    public void WriteTo(Stream destination)
    {
        if (SizeBytes < HeaderEnd)
        {
            // Too small to hold a header: just noise (tests for TooSmall).
            var noise = new byte[SizeBytes];
            new Random(Seed).NextBytes(noise);
            destination.Write(noise);
            return;
        }

        var header = Header();
        destination.Write(header, 0, (int)Math.Min(SizeBytes, header.Length));
        // System.Random with a seed uses the legacy, fixed algorithm, so the bytes are stable across runtimes.
        var random = new Random(Seed);
        var chunk = new byte[ChunkBytes];
        if (FillWithFF)
            chunk.AsSpan().Fill(0xFF);
        for (var left = SizeBytes - header.Length; left > 0; left -= ChunkBytes)
        {
            if (!FillWithFF)
                random.NextBytes(chunk);
            destination.Write(chunk, 0, (int)Math.Min(left, ChunkBytes));
        }
    }

    /// <summary>The 0x200-byte header area with its CRC.</summary>
    public byte[] Header()
    {
        var header = new byte[HeaderAreaBytes];
        WriteAscii(header, 0x00, 12, Title);
        WriteAscii(header, GameCodeOffset, 4, GameCode);
        WriteAscii(header, MakerCodeOffset, 2, MakerCode);
        header[UnitCodeOffset] = UnitCode;
        header[CapacityOffset] = Capacity;
        header[RomVersionOffset] = RomVersion;
        var crc = (ushort)(HeaderCrc(header.AsSpan(0, HeaderCrcOffset)) + (BadHeaderCrc ? 1 : 0));
        header[HeaderCrcOffset] = (byte)crc;
        header[HeaderCrcOffset + 1] = (byte)(crc >> 8);
        return header;
    }

    /// <summary>CRC-16, initial 0xFFFF, reflected polynomial 0xA001. A copy of the reader's, so tests catch an edit to either.</summary>
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

    private static void WriteAscii(byte[] rom, int offset, int length, string? value)
    {
        var area = rom.AsSpan(offset, length);
        area.Clear();
        if (value is not null)
            Encoding.ASCII.GetBytes(value.Length > length ? value[..length] : value, area);
    }
}
