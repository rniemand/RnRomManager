using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.TestData;

/// <summary>
/// A synthetic Nintendo Switch dump (spec N2): a PFS0 (NSP/NSZ) with the given entry names and sizes, or, when
/// <see cref="XciHeadAt"/> is set, an XCI-style buffer with <c>HEAD</c> at that offset. Padded with seeded bytes to at
/// least <see cref="MinLength"/> (N1's minimum size), so different seeds give different SHA1s.
/// </summary>
public sealed class SyntheticNspBuilder
{
    /// <summary>PFS0 entries in order; each gets <c>Size</c> seeded bytes of data.</summary>
    public IReadOnlyList<(string Name, int Size)> Entries { get; init; } = [];

    /// <summary><c>0x100</c> (a usual XCI) or <c>0x1100</c> (with the key area); null builds a PFS0.</summary>
    public int? XciHeadAt { get; init; }

    public int MinLength { get; init; } = 1024;

    public int Seed { get; init; }

    /// <summary>A ticket name for <paramref name="titleId"/>: the id, 16 more hex digits and <c>.tik</c>.</summary>
    public static string TicketName(string titleId) => titleId + "0000000000000000.tik";

    public byte[] Build()
    {
        var random = new Random(Seed);
        var bytes = new List<byte>();
        if (XciHeadAt is { } head)
        {
            var buffer = new byte[Math.Max(MinLength, head + 0x200)];
            random.NextBytes(buffer);
            "HEAD"u8.CopyTo(buffer.AsSpan(head));
            buffer[0] = 0; // never read as a PFS0
            return buffer;
        }

        var table = new List<byte>();
        var offsets = new List<int>();
        foreach (var (name, _) in Entries)
        {
            offsets.Add(table.Count);
            table.AddRange(Encoding.UTF8.GetBytes(name));
            table.Add(0);
        }
        while (table.Count % 0x20 != 0)
            table.Add(0);

        var header = new byte[0x10 + Entries.Count * 0x18];
        "PFS0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)Entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)table.Count);
        long dataOffset = 0;
        for (var i = 0; i < Entries.Count; i++)
        {
            var entry = header.AsSpan(0x10 + i * 0x18);
            BinaryPrimitives.WriteUInt64LittleEndian(entry, (ulong)dataOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)Entries[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], (uint)offsets[i]);
            dataOffset += Entries[i].Size;
        }
        bytes.AddRange(header);
        bytes.AddRange(table);
        var data = new byte[Math.Max(dataOffset, MinLength - bytes.Count)];
        random.NextBytes(data);
        bytes.AddRange(data);
        return [.. bytes];
    }
}
