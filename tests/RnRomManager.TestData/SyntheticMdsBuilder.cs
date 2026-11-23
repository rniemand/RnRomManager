using System.Buffers.Binary;

namespace RnRomManager.TestData;

/// <summary>
/// One session-1 block of a synthetic <c>.mds</c>. A null <paramref name="Pregap"/> writes <c>extra_offset</c> 0; otherwise the
/// extra block holds the pregap and a length of 1000 sectors.
/// </summary>
public sealed record MdsBlock(int Point, int Mode = 0xAB, int Sub = 0, int SectorSize = 2352, ulong Offset = 0, uint? Pregap = 150);

/// <summary>File type gaps D7: an Alcohol 120% <c>.mds</c> as libmirage reads it (header at 0, session block at 88, track blocks at 112, then 8-byte extra blocks).</summary>
public static class SyntheticMdsBuilder
{
    /// <summary>A one-track MODE2 disc starting at the <c>.mdf</c>'s first byte.</summary>
    public static byte[] OneTrack() => Build([new MdsBlock(1)]);

    public static byte[] Build(MdsBlock[] blocks, int sessions = 1, int version = 1)
    {
        var bytes = new byte[112 + blocks.Length * 88];
        "MEDIA DESCRIPTOR"u8.CopyTo(bytes);
        bytes[16] = (byte)version;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x14), (ushort)sessions);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x50), 88);
        bytes[88 + 10] = (byte)blocks.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88 + 20), 112);
        for (var i = 0; i < blocks.Length; i++)
        {
            var (at, extra) = (112 + i * 80, 112 + blocks.Length * 80 + i * 8);
            var b = blocks[i];
            bytes[at] = (byte)b.Mode;
            bytes[at + 1] = (byte)b.Sub;
            bytes[at + 4] = (byte)b.Point;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 16), (ushort)b.SectorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 36), (uint)(b.Offset / 2352));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 40), b.Offset);
            if (b.Pregap is { } pregap)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 12), (uint)extra);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extra), pregap);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extra + 4), 1000);
            }
        }

        return bytes;
    }
}
