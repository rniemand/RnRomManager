namespace RnRomManager.TestData;

/// <summary>Nametable mirroring <see cref="SyntheticNesRomBuilder"/> writes (TestData doesn't reference the Systems project).</summary>
public enum NesMirroring
{
    Horizontal,
    Vertical,
    FourScreen,
}

/// <summary>
/// Deterministic, non-copyrighted headered <c>.nes</c> dumps for tests and seed sets: an iNES or NES 2.0 header,
/// an optional 512-byte trainer, then seeded random PRG and CHR banks. Same properties → same bytes.
/// </summary>
public sealed record SyntheticNesRomBuilder
{
    public const int HeaderSize = 16;
    public const int PrgBankSize = 16384;
    public const int ChrBankSize = 8192;
    public const int TrainerSize = 512;

    public int Seed { get; init; }

    /// <summary>16 KiB PRG ROM banks (byte 4).</summary>
    public int PrgBanks { get; init; } = 2;

    /// <summary>8 KiB CHR ROM banks (byte 5).</summary>
    public int ChrBanks { get; init; } = 1;

    /// <summary>Up to 255 for iNES; up to 4095 with <see cref="Nes20"/>.</summary>
    public int Mapper { get; init; }

    /// <summary>Writes a NES 2.0 header (byte 7 bits 2–3 = 10b) instead of iNES.</summary>
    public bool Nes20 { get; init; }

    public NesMirroring Mirroring { get; init; } = NesMirroring.Horizontal;

    public bool Battery { get; init; }

    /// <summary>Adds the flag and 512 trainer bytes between the header and PRG.</summary>
    public bool Trainer { get; init; }

    /// <summary>PAL instead of NTSC (iNES byte 9 bit 0, NES 2.0 byte 12 = 1).</summary>
    public bool Pal { get; init; }

    /// <summary>Writes zeros instead of <c>NES\x1A</c>, so the header is invalid.</summary>
    public bool CorruptMagic { get; init; }

    public int SizeBytes => HeaderSize + (Trainer ? TrainerSize : 0) + PrgBanks * PrgBankSize + ChrBanks * ChrBankSize;

    public byte[] Build()
    {
        var rom = new byte[SizeBytes];
        // System.Random with a seed uses the legacy, fixed algorithm, so the bytes are stable across runtimes.
        new Random(Seed).NextBytes(rom);
        var header = rom.AsSpan(0, HeaderSize);
        header.Clear();
        if (!CorruptMagic)
            "NES\x1A"u8.CopyTo(header);
        header[4] = (byte)PrgBanks;
        header[5] = (byte)ChrBanks;
        header[6] = (byte)(((Mapper & 0x0F) << 4)
            | (Mirroring == NesMirroring.FourScreen ? 0x08 : 0)
            | (Trainer ? 0x04 : 0)
            | (Battery ? 0x02 : 0)
            | (Mirroring == NesMirroring.Vertical ? 0x01 : 0));
        header[7] = (byte)((Mapper & 0xF0) | (Nes20 ? 0x08 : 0));
        if (Nes20)
        {
            header[8] = (byte)((Mapper >> 8) & 0x0F);
            header[9] = (byte)(((ChrBanks >> 8) << 4) | (PrgBanks >> 8));
            header[12] = (byte)(Pal ? 1 : 0);
        }
        else
            header[9] = (byte)(Pal ? 1 : 0);
        return rom;
    }
}
