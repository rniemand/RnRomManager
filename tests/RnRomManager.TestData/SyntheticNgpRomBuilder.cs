using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.TestData;

/// <summary>
/// Deterministic, non-copyrighted Neo Geo Pocket ROMs for tests and seed sets. The body is seeded random bytes; the
/// 64-byte header is written over it. Same properties → same bytes.
/// </summary>
public sealed record SyntheticNgpRomBuilder
{
    public const int HeaderEnd = 0x40;

    public int Seed { get; init; }

    public int Size { get; init; } = 65536;

    /// <summary>Up to 12 ASCII chars, space-padded; null writes 12 spaces.</summary>
    public string? Title { get; init; }

    /// <summary>Mode byte 0x10 (colour) when true, 0x00 when false.</summary>
    public bool Colour { get; init; }

    /// <summary>28 ASCII chars written at 0x00.</summary>
    public string Licence { get; init; } = "COPYRIGHT BY SNK CORPORATION";

    public ushort Catalog { get; init; }

    public byte SubCatalog { get; init; }

    public byte[] Build()
    {
        if (Size < HeaderEnd)
            throw new ArgumentOutOfRangeException(nameof(Size), Size, "A ROM holds at least the 64-byte header.");

        var rom = new byte[Size];
        // System.Random with a seed uses the legacy, fixed algorithm, so the bytes are stable across runtimes.
        new Random(Seed).NextBytes(rom);
        var licence = rom.AsSpan(0, 28);
        licence.Clear();
        Encoding.ASCII.GetBytes(Licence.Length > 28 ? Licence[..28] : Licence, licence);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x1C), 0x00200040);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x20), Catalog);
        rom[0x22] = SubCatalog;
        rom[0x23] = Colour ? (byte)0x10 : (byte)0x00;
        var title = rom.AsSpan(0x24, 12);
        title.Fill((byte)' ');
        if (Title is not null)
            Encoding.ASCII.GetBytes(Title.Length > 12 ? Title[..12] : Title, title);
        rom.AsSpan(0x30, 0x10).Clear();
        return rom;
    }
}
