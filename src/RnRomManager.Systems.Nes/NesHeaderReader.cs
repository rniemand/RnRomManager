using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nes;

/// <summary>
/// Reads the 16-byte iNES / NES 2.0 header of a headered <c>.nes</c> dump (nesdev "iNES" and "NES 2.0", N4). Never
/// throws; only files shorter than the header return null. iNES has no title, so suggestions use the file name.
/// </summary>
public static class NesHeaderReader
{
    public const string NesId = "nes";
    public const int HeaderSize = 16;

    /// <returns>null when <paramref name="rom"/> is shorter than 16 bytes.</returns>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < HeaderSize)
            return null;

        var headerValid = rom[0] == 0x4E && rom[1] == 0x45 && rom[2] == 0x53 && rom[3] == 0x1A;
        var fields = new Dictionary<string, object?>
        {
            ["headerValid"] = headerValid,
            ["format"] = null,
            ["mapper"] = null,
            ["prgRomSizeBytes"] = null,
            ["chrRomSizeBytes"] = null,
            ["mirroring"] = null,
            ["battery"] = null,
            ["trainer"] = null,
            ["tvSystem"] = null,
            ["romSizeBytes"] = rom.Length,
        };
        if (headerValid)
        {
            byte flags6 = rom[6], flags7 = rom[7];
            var nes20 = (flags7 & 0x0C) == 0x08;
            var mapper = (flags6 >> 4) | (flags7 & 0xF0);
            if (nes20)
                mapper |= (rom[8] & 0x0F) << 8;
            fields["format"] = nes20 ? "NES 2.0" : "iNES";
            fields["mapper"] = mapper;
            fields["prgRomSizeBytes"] = nes20 ? Nes20Size(rom[9] & 0x0F, rom[4], 16384) : rom[4] * 16384;
            fields["chrRomSizeBytes"] = nes20 ? Nes20Size(rom[9] >> 4, rom[5], 8192) : rom[5] * 8192;
            fields["mirroring"] = (flags6 & 0x08) != 0 ? "Four-screen" : (flags6 & 0x01) != 0 ? "Vertical" : "Horizontal";
            fields["battery"] = (flags6 & 0x02) != 0;
            fields["trainer"] = (flags6 & 0x04) != 0;
            fields["tvSystem"] = nes20
                ? (rom[12] & 0x03) switch { 0 => "NTSC", 1 => "PAL", 2 => "Multi-region", _ => "Dendy" }
                : (rom[9] & 0x01) != 0 ? "PAL" : "NTSC";
        }

        return new RomHeader(NesId, null, headerValid, fields);
    }

    /// <summary>NES 2.0 size: the MSB nibble above the LSB byte, in units; nibble 0xF is the exponent form, not decoded.</summary>
    private static int? Nes20Size(int msb, byte lsb, int unit) => msb == 0x0F ? null : ((msb << 8) | lsb) * unit;
}
