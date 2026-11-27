using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nes;

/// <summary>
/// Reads the 16-byte iNES / NES 2.0 header of a headered <c>.nes</c> dump (nesdev "iNES" and "NES 2.0", N4), or a UNIF
/// file's chunks. Never throws; only files shorter than the header return null. iNES has no title, so suggestions use the
/// file name; UNIF's title is its <c>NAME</c> chunk.
/// </summary>
public static class NesHeaderReader
{
    public const string NesId = "nes";
    public const int HeaderSize = 16;

    /// <summary>UNIF: the <c>UNIF</c> magic, a revision and padding to 32 bytes, then the chunks.</summary>
    private const int UnifHeaderSize = 32;

    /// <returns>null when <paramref name="rom"/> is shorter than 16 bytes.</returns>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < HeaderSize)
            return null;
        if (rom.Length >= UnifHeaderSize && rom[..4].SequenceEqual("UNIF"u8))
            return ReadUnif(rom);

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

    /// <summary>Walks the chunks (4-byte id, little-endian uint32 length, data) for the first <c>NAME</c> and <c>MAPR</c>.</summary>
    private static RomHeader ReadUnif(ReadOnlySpan<byte> rom)
    {
        string? name = null, board = null;
        var offset = UnifHeaderSize;
        while (rom.Length - offset >= 8)
        {
            var id = rom.Slice(offset, 4);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset + 4, 4));
            if (length > rom.Length - offset - 8)
                break;
            var data = rom.Slice(offset + 8, (int)length);
            if (name is null && id.SequenceEqual("NAME"u8))
                name = ChunkString(data);
            else if (board is null && id.SequenceEqual("MAPR"u8))
                board = ChunkString(data);
            offset += 8 + (int)length;
        }

        var fields = new Dictionary<string, object?>
        {
            ["headerValid"] = true,
            ["format"] = "UNIF",
            ["board"] = board,
            ["romSizeBytes"] = rom.Length,
        };
        return new RomHeader(NesId, name, true, fields);
    }

    /// <summary>UTF-8 up to the first NUL, trimmed; empty is null.</summary>
    private static string? ChunkString(ReadOnlySpan<byte> data)
    {
        var nul = data.IndexOf((byte)0);
        var text = Encoding.UTF8.GetString(nul < 0 ? data : data[..nul]).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>NES 2.0 size: the MSB nibble above the LSB byte, in units; nibble 0xF is the exponent form, not decoded.</summary>
    private static int? Nes20Size(int msb, byte lsb, int unit) => msb == 0x0F ? null : ((msb << 8) | lsb) * unit;
}
