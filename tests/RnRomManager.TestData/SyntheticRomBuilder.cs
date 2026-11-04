using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.TestData;

/// <summary>
/// Deterministic, non-copyrighted Game Boy ROMs for tests and seed sets (spec §15.2). The body is seeded random
/// bytes; the cartridge header (§8.1) is filled from these properties, and both checksums are computed last, over
/// the final bytes. Same properties → same bytes.
/// </summary>
public sealed record SyntheticRomBuilder
{
    /// <summary>The 48-byte boot logo bitmap at 0x0104 (Pan Docs, "Nintendo Logo"); the boot ROM refuses a cart without it.</summary>
    public static ReadOnlySpan<byte> NintendoLogo =>
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E, 0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    public const int LogoOffset = 0x0104;
    public const int TitleOffset = 0x0134;
    public const int ManufacturerCodeOffset = 0x013F;
    public const int CgbFlagOffset = 0x0143;
    public const int NewLicenseeOffset = 0x0144;
    public const int SgbFlagOffset = 0x0146;
    public const int CartridgeTypeOffset = 0x0147;
    public const int RomSizeOffset = 0x0148;
    public const int RamSizeOffset = 0x0149;
    public const int DestinationOffset = 0x014A;
    public const int OldLicenseeOffset = 0x014B;
    public const int VersionOffset = 0x014C;
    public const int HeaderChecksumOffset = 0x014D;
    public const int GlobalChecksumOffset = 0x014E;
    public const int HeaderEnd = 0x0150;

    /// <summary>Where an MBC1 multicart's second game (bank 0x10) has its logo (spec §8.4).</summary>
    public const int Mbc1MulticartLogoOffset = 0x40104;

    public int Seed { get; init; }

    /// <summary>ASCII, written NUL-padded from 0x0134. Up to 16 chars; the CGB flag and manufacturer code overwrite the tail.</summary>
    public string Title { get; init; } = "SYNTHETIC";

    /// <summary>4 chars at 0x013F, or null for none.</summary>
    public string? ManufacturerCode { get; init; }

    /// <summary>0x80 dual-mode, 0xC0 CGB-only, 0x00 original GB.</summary>
    public byte CgbFlag { get; init; }

    public byte SgbFlag { get; init; }

    public byte CartridgeType { get; init; }

    /// <summary>0x00–0x08 = 32 KiB &lt;&lt; code. Also decides the length unless <see cref="SizeBytes"/> is set.</summary>
    public byte RomSizeCode { get; init; }

    public byte RamSizeCode { get; init; }

    /// <summary>0x00 Japan, 0x01 Overseas.</summary>
    public byte Destination { get; init; } = 0x01;

    /// <summary>Ignored when <see cref="NewLicensee"/> is set (the old byte is then 0x33).</summary>
    public byte OldLicensee { get; init; } = 0x01;

    /// <summary>2 ASCII chars at 0x0144, or null to use <see cref="OldLicensee"/>.</summary>
    public string? NewLicensee { get; init; }

    public byte Version { get; init; }

    /// <summary>Actual file length when it should differ from the header's ROM size (trimmed/over-dumps, tiny files).</summary>
    public int? SizeBytes { get; init; }

    public bool CorruptLogo { get; init; }

    public bool CorruptHeaderChecksum { get; init; }

    public bool CorruptGlobalChecksum { get; init; }

    /// <summary>Also writes the logo at <see cref="Mbc1MulticartLogoOffset"/> (needs ≥ 0x40134 bytes).</summary>
    public bool SecondLogoForMulticart { get; init; }

    /// <summary>A 1 MiB MBC1 cart with a second logo at 0x40104: what the reader detects as MBC1M (spec §8.4).</summary>
    public static SyntheticRomBuilder Mbc1Multicart(int seed = 0, string title = "MULTICART") => new()
    {
        Seed = seed,
        Title = title,
        CartridgeType = 0x01,
        RomSizeCode = 0x05,
        SecondLogoForMulticart = true,
    };

    public byte[] Build()
    {
        var length = SizeBytes ?? (32 * 1024) << RomSizeCode;
        var rom = new byte[length];
        // System.Random with a seed uses the legacy, fixed algorithm, so the bytes are stable across runtimes.
        new Random(Seed).NextBytes(rom);
        if (length < HeaderEnd)
            return rom; // too small to hold a header: just noise (tests for TooSmall)

        rom.AsSpan(0x0100, 4).Clear();
        rom[0x0101] = 0xC3; // nop; jp 0x0150
        rom[0x0102] = 0x50;
        rom[0x0103] = 0x01;
        WriteLogo(rom, LogoOffset);
        if (CorruptLogo)
            rom[LogoOffset + 10] ^= 0xFF;
        if (SecondLogoForMulticart)
            WriteLogo(rom, Mbc1MulticartLogoOffset);

        var title = rom.AsSpan(TitleOffset, 16);
        title.Clear();
        Encoding.ASCII.GetBytes(Title.Length > 16 ? Title[..16] : Title, title);
        if (ManufacturerCode is not null)
            Encoding.ASCII.GetBytes(ManufacturerCode, rom.AsSpan(ManufacturerCodeOffset, 4));
        // Original GB carts may use 0x0143 as the 16th title character; only a CGB cart needs the flag there.
        if (CgbFlag != 0)
            rom[CgbFlagOffset] = CgbFlag;
        if (NewLicensee is not null)
            Encoding.ASCII.GetBytes(NewLicensee, rom.AsSpan(NewLicenseeOffset, 2));
        else
            rom.AsSpan(NewLicenseeOffset, 2).Clear();
        rom[SgbFlagOffset] = SgbFlag;
        rom[CartridgeTypeOffset] = CartridgeType;
        rom[RomSizeOffset] = RomSizeCode;
        rom[RamSizeOffset] = RamSizeCode;
        rom[DestinationOffset] = Destination;
        rom[OldLicenseeOffset] = NewLicensee is null ? OldLicensee : (byte)0x33;
        rom[VersionOffset] = Version;

        rom[HeaderChecksumOffset] = (byte)(HeaderChecksum(rom) ^ (CorruptHeaderChecksum ? 0xFF : 0x00));
        BinaryPrimitives.WriteUInt16BigEndian(rom.AsSpan(GlobalChecksumOffset), (ushort)(GlobalChecksum(rom) + (CorruptGlobalChecksum ? 1 : 0)));
        return rom;
    }

    /// <summary>§8.1: <c>x = x - rom[i] - 1</c> over 0x0134–0x014C.</summary>
    public static byte HeaderChecksum(ReadOnlySpan<byte> rom)
    {
        byte x = 0;
        for (var i = TitleOffset; i <= VersionOffset; i++)
            x = (byte)(x - rom[i] - 1);
        return x;
    }

    /// <summary>§8.1: 16-bit sum of every byte except the two checksum bytes.</summary>
    public static ushort GlobalChecksum(ReadOnlySpan<byte> rom)
    {
        uint sum = 0;
        for (var i = 0; i < rom.Length; i++)
        {
            if (i is not (GlobalChecksumOffset or GlobalChecksumOffset + 1))
                sum += rom[i];
        }
        return (ushort)sum;
    }

    private static void WriteLogo(byte[] rom, int offset)
    {
        if (rom.Length >= offset + NintendoLogo.Length)
            NintendoLogo.CopyTo(rom.AsSpan(offset));
    }
}
