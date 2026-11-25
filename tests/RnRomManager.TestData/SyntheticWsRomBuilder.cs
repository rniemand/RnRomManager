using System.Buffers.Binary;

namespace RnRomManager.TestData;

/// <summary>
/// Deterministic, non-copyrighted WonderSwan ROMs for tests and seed sets. The body is seeded random bytes; the
/// 16-byte footer is written over the last 16. Same properties → same bytes.
/// </summary>
public sealed record SyntheticWsRomBuilder
{
    public const int FooterSize = 16;

    public int Seed { get; init; }

    public int Size { get; init; } = 65536;

    /// <summary>Footer byte 0x7 is 0x01 (colour) when true, 0x00 when false.</summary>
    public bool Colour { get; init; }

    public byte PublisherId { get; init; } = 0x01;

    public byte GameId { get; init; } = 0x01;

    /// <summary>Footer byte 0x0; 0xEA (<c>JMP FAR</c>) makes the footer plausible.</summary>
    public byte Opcode { get; init; } = 0xEA;

    public bool Vertical { get; init; }

    /// <summary>Writes the checksum plus one.</summary>
    public bool BadChecksum { get; init; }

    public byte[] Build()
    {
        if (Size < FooterSize)
            throw new ArgumentOutOfRangeException(nameof(Size), Size, "A ROM holds at least the 16-byte footer.");

        var rom = new byte[Size];
        // System.Random with a seed uses the legacy, fixed algorithm, so the bytes are stable across runtimes.
        new Random(Seed).NextBytes(rom);
        var f = rom.AsSpan(Size - FooterSize);
        f[0x0] = Opcode;
        f[0x1] = 0x00;
        f[0x2] = 0x00;
        f[0x3] = 0x00;
        f[0x4] = 0x40;
        f[0x5] = 0x00;
        f[0x6] = PublisherId;
        f[0x7] = Colour ? (byte)0x01 : (byte)0x00;
        f[0x8] = GameId;
        f[0x9] = 0x00;
        f[0xA] = 0x02;
        f[0xB] = 0x00;
        f[0xC] = Vertical ? (byte)0x05 : (byte)0x04;
        f[0xD] = 0x00;
        var sum = 0u;
        foreach (var b in rom.AsSpan(0, Size - 2))
            sum += b;
        BinaryPrimitives.WriteUInt16LittleEndian(f[0xE..], (ushort)(sum + (BadChecksum ? 1u : 0u)));
        return rom;
    }
}
