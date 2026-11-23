using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.TestData;

/// <summary>One track block of a synthetic <c>.nrg</c> DAO chunk; offsets are byte offsets into the image.</summary>
public sealed record NrgBlock(ulong Index0, ulong Index1, ulong End, int Mode = 0x07, int SectorSize = 2352);

/// <summary>
/// Browser play format gaps N2: a Nero <c>.nrg</c> as libmirage reads it (big-endian): track data, then chunks, then a
/// v2 (<c>NER5</c>, u64) or v1 (<c>NERO</c>, u32) footer pointing at the first chunk.
/// </summary>
public static class SyntheticNrgBuilder
{
    /// <summary>A DAO image: zero-filled data up to the last track's end, the DAO chunk, <c>END!</c> and the footer.</summary>
    public static byte[] Build(NrgBlock[] blocks, bool v2 = true) =>
        Image((long)blocks[^1].End, v2, Dao(blocks, v2), Chunk("END!", []));

    /// <summary>A <c>DAOX</c> (v2) or <c>DAOI</c> (v1) chunk holding tracks <paramref name="firstTrack"/> onward.</summary>
    public static byte[] Dao(NrgBlock[] blocks, bool v2, int firstTrack = 1)
    {
        var blockBytes = v2 ? 42 : 30;
        var data = new byte[22 + blocks.Length * blockBytes];
        data[20] = (byte)firstTrack;
        data[21] = (byte)(firstTrack + blocks.Length - 1);
        for (var i = 0; i < blocks.Length; i++)
        {
            var block = data.AsSpan(22 + i * blockBytes);
            var b = blocks[i];
            BinaryPrimitives.WriteUInt16BigEndian(block[12..], (ushort)b.SectorSize);
            block[14] = (byte)b.Mode;
            ulong[] offsets = [b.Index0, b.Index1, b.End];
            for (var o = 0; o < 3; o++)
                if (v2)
                    BinaryPrimitives.WriteUInt64BigEndian(block[(18 + o * 8)..], offsets[o]);
                else
                    BinaryPrimitives.WriteUInt32BigEndian(block[(18 + o * 4)..], (uint)offsets[o]);
        }

        return Chunk(v2 ? "DAOX" : "DAOI", data);
    }

    /// <summary>A chunk: 4-byte id, u32 size, the data.</summary>
    public static byte[] Chunk(string id, byte[] data)
    {
        var chunk = new byte[8 + data.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    /// <summary><paramref name="dataLength"/> bytes of track data (byte i = i % 251), the chunks, then the footer.</summary>
    public static byte[] Image(long dataLength, bool v2, params byte[][] chunks)
    {
        var footer = v2 ? 12 : 8;
        var image = new byte[dataLength + chunks.Sum(c => c.Length) + footer];
        for (var i = 0; i < dataLength; i++)
            image[i] = (byte)(i % 251);
        var at = (int)dataLength;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(image, at);
            at += chunk.Length;
        }

        var tail = image.AsSpan(at);
        if (v2)
        {
            "NER5"u8.CopyTo(tail);
            BinaryPrimitives.WriteUInt64BigEndian(tail[4..], (ulong)dataLength);
        }
        else
        {
            "NERO"u8.CopyTo(tail);
            BinaryPrimitives.WriteUInt32BigEndian(tail[4..], (uint)dataLength);
        }

        return image;
    }
}
