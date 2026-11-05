using System.Text;

namespace RnRomManager.TestData;

/// <summary>
/// Deterministic, non-copyrighted GBA ROMs for tests and seed sets. The body is seeded random bytes; the logo,
/// the 0x96 fixed value and the header fields are written over it, and the complement check is computed last.
/// Same properties → same bytes.
/// </summary>
public sealed record SyntheticGbaRomBuilder
{
    /// <summary>The 156-byte logo at 0x04 (GBATEK). A copy of the reader's constant, so tests catch an edit to either.</summary>
    public static ReadOnlySpan<byte> NintendoLogo =>
    [
        0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21, 0x3D, 0x84, 0x82, 0x0A, 0x84, 0xE4, 0x09, 0xAD,
        0x11, 0x24, 0x8B, 0x98, 0xC0, 0x81, 0x7F, 0x21, 0xA3, 0x52, 0xBE, 0x19, 0x93, 0x09, 0xCE, 0x20,
        0x10, 0x46, 0x4A, 0x4A, 0xF8, 0x27, 0x31, 0xEC, 0x58, 0xC7, 0xE8, 0x33, 0x82, 0xE3, 0xCE, 0xBF,
        0x85, 0xF4, 0xDF, 0x94, 0xCE, 0x4B, 0x09, 0xC1, 0x94, 0x56, 0x8A, 0xC0, 0x13, 0x72, 0xA7, 0xFC,
        0x9F, 0x84, 0x4D, 0x73, 0xA3, 0xCA, 0x9A, 0x61, 0x58, 0x97, 0xA3, 0x27, 0xFC, 0x03, 0x98, 0x76,
        0x23, 0x1D, 0xC7, 0x61, 0x03, 0x04, 0xAE, 0x56, 0xBF, 0x38, 0x84, 0x00, 0x40, 0xA7, 0x0E, 0xFD,
        0xFF, 0x52, 0xFE, 0x03, 0x6F, 0x95, 0x30, 0xF1, 0x97, 0xFB, 0xC0, 0x85, 0x60, 0xD6, 0x80, 0x25,
        0xA9, 0x63, 0xBE, 0x03, 0x01, 0x4E, 0x38, 0xE2, 0xF9, 0xA2, 0x34, 0xFF, 0xBB, 0x3E, 0x03, 0x44,
        0x78, 0x00, 0x90, 0xCB, 0x88, 0x11, 0x3A, 0x94, 0x65, 0xC0, 0x7C, 0x63, 0x87, 0xF0, 0x3C, 0xAF,
        0xD6, 0x25, 0xE4, 0x8B, 0x38, 0x0A, 0xAC, 0x72, 0x21, 0xD4, 0xF8, 0x07,
    ];

    public const int LogoOffset = 0x04;
    public const int TitleOffset = 0xA0;
    public const int GameCodeOffset = 0xAC;
    public const int MakerCodeOffset = 0xB0;
    public const int FixedValueOffset = 0xB2;
    public const int UnitCodeOffset = 0xB3;
    public const int DeviceTypeOffset = 0xB4;
    public const int VersionOffset = 0xBC;
    public const int HeaderChecksumOffset = 0xBD;
    public const int HeaderEnd = 0xC0;

    public int Seed { get; init; }

    public int SizeBytes { get; init; } = 256 * 1024;

    /// <summary>Up to 12 ASCII chars, NUL-padded; null writes zero bytes.</summary>
    public string? Title { get; init; }

    /// <summary>4 ASCII chars; null writes zero bytes.</summary>
    public string? GameCode { get; init; }

    /// <summary>2 ASCII chars; null writes zero bytes.</summary>
    public string? MakerCode { get; init; } = "01";

    public byte UnitCode { get; init; }

    public byte DeviceType { get; init; }

    public byte Version { get; init; }

    public bool CorruptLogo { get; init; }

    public bool CorruptFixedValue { get; init; }

    public bool CorruptHeaderChecksum { get; init; }

    public byte[] Build()
    {
        var rom = new byte[SizeBytes];
        // System.Random with a seed uses the legacy, fixed algorithm, so the bytes are stable across runtimes.
        new Random(Seed).NextBytes(rom);
        if (SizeBytes < HeaderEnd)
            return rom; // too small to hold a header: just noise (tests for TooSmall)

        NintendoLogo.CopyTo(rom.AsSpan(LogoOffset));
        if (CorruptLogo)
            rom[LogoOffset + 10] ^= 0xFF;
        WriteAscii(rom, TitleOffset, 12, Title);
        WriteAscii(rom, GameCodeOffset, 4, GameCode);
        WriteAscii(rom, MakerCodeOffset, 2, MakerCode);
        rom[FixedValueOffset] = CorruptFixedValue ? (byte)0x00 : (byte)0x96;
        rom[UnitCodeOffset] = UnitCode;
        rom[DeviceTypeOffset] = DeviceType;
        rom.AsSpan(0xB5, 7).Clear(); // reserved
        rom[VersionOffset] = Version;

        rom[HeaderChecksumOffset] = (byte)(HeaderChecksum(rom) + (CorruptHeaderChecksum ? 1 : 0));
        return rom;
    }

    /// <summary>GBATEK: <c>chk = 0; for 0xA0..0xBC: chk -= rom[i]; chk -= 0x19</c>.</summary>
    public static byte HeaderChecksum(ReadOnlySpan<byte> rom)
    {
        byte chk = 0;
        for (var i = TitleOffset; i <= VersionOffset; i++)
            chk -= rom[i];
        return (byte)(chk - 0x19);
    }

    private static void WriteAscii(byte[] rom, int offset, int length, string? value)
    {
        var area = rom.AsSpan(offset, length);
        area.Clear();
        if (value is not null)
            Encoding.ASCII.GetBytes(value.Length > length ? value[..length] : value, area);
    }
}
