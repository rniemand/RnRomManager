using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.TestData;

/// <summary>Where <see cref="SyntheticSnesRomBuilder"/> writes the header (TestData doesn't reference the Systems project).</summary>
public enum SnesMapping
{
    LoROM,
    HiROM,
    ExHiROM,
}

/// <summary>
/// Deterministic, non-copyrighted <c>.sfc</c> images for tests and seed sets: seeded random bytes with an internal
/// header at the mapping's offset, optionally behind a 512-byte copier header. Same properties → same bytes.
/// </summary>
public sealed record SyntheticSnesRomBuilder
{
    public const int CopierHeaderSize = 512;
    public const int TitleLength = 21;

    public int Seed { get; init; }

    /// <summary>A power of two; ExHiROM needs at least 8192.</summary>
    public int SizeKiB { get; init; } = 512;

    public SnesMapping Mapping { get; init; } = SnesMapping.LoROM;

    /// <summary>Padded to 21 with spaces and written as Latin-1, so a test can set any byte <c>0x00–0xFF</c>.</summary>
    public string Title { get; init; } = "SYNTHETIC SNES";

    public bool FastRom { get; init; }

    public byte RomType { get; init; }

    public byte SramSizeByte { get; init; }

    public byte Region { get; init; } = 1;

    public byte Version { get; init; }

    /// <summary>false writes <c>0000</c> for both checksum and complement.</summary>
    public bool ValidChecksum { get; init; } = true;

    /// <summary>Prepends 512 zero bytes.</summary>
    public bool CopierHeader { get; init; }

    public int HeaderOffset => Mapping switch
    {
        SnesMapping.LoROM => 0x7FC0,
        SnesMapping.HiROM => 0xFFC0,
        _ => 0x40FFC0,
    };

    public byte[] Build()
    {
        if (SizeKiB <= 0 || (SizeKiB & (SizeKiB - 1)) != 0)
            throw new ArgumentException($"SizeKiB {SizeKiB} is not a power of two.", nameof(SizeKiB));
        if (SizeKiB * 1024 < HeaderOffset + 0x40)
            throw new ArgumentException($"{SizeKiB} KiB is too small for a {Mapping} header.", nameof(SizeKiB));
        if (Title.Length > TitleLength)
            throw new ArgumentException($"Title is over {TitleLength} characters.", nameof(Title));

        var image = new byte[SizeKiB * 1024];
        // System.Random with a seed uses the legacy, fixed algorithm, so the bytes are stable across runtimes.
        new Random(Seed).NextBytes(image);
        var h = image.AsSpan(HeaderOffset, 0x40);
        Encoding.Latin1.GetBytes(Title.PadRight(TitleLength)).CopyTo(h);
        h[0x15] = (byte)(0x20 | (FastRom ? 0x10 : 0) | Mapping switch { SnesMapping.LoROM => 0x0, SnesMapping.HiROM => 0x1, _ => 0x5 });
        h[0x16] = RomType;
        h[0x17] = (byte)int.Log2(SizeKiB);
        h[0x18] = SramSizeByte;
        h[0x19] = Region;
        h[0x1B] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x1C..], ValidChecksum ? (ushort)0xFFFF : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x1E..], 0);
        if (ValidChecksum)
        {
            var sum = (ushort)image.Sum(b => (long)b);
            BinaryPrimitives.WriteUInt16LittleEndian(h[0x1C..], (ushort)~sum);
            BinaryPrimitives.WriteUInt16LittleEndian(h[0x1E..], sum);
        }
        return CopierHeader ? [.. new byte[CopierHeaderSize], .. image] : image;
    }
}
