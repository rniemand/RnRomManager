using System.Buffers.Binary;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NintendoDs;

/// <summary>
/// Reads the NCSD header of a 3DS card dump (<c>.3ds</c>/<c>.cci</c>) and the NCCH header of its first partition
/// (3dbrew NCSD/NCCH/CIA pages, checked 2026-11-17). CIA files return null so an unmatched one goes to Review. Never throws.
/// </summary>
public static class Nintendo3DsHeaderReader
{
    public const string Nintendo3DsId = "3ds";

    /// <summary>A NCSD partition offset is in media units; retail dumps use 0x200.</summary>
    public const long MediaUnit = 0x200;

    private const int MinLength = 0x200;
    private const uint CiaHeaderSize = 0x2020;

    /// <param name="rom">At most the first MiB of the dump.</param>
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < MinLength || BinaryPrimitives.ReadUInt32LittleEndian(rom) == CiaHeaderSize)
            return null;

        if (!rom.Slice(0x100, 4).SequenceEqual("NCSD"u8))
            return new RomHeader(Nintendo3DsId, null, false, new Dictionary<string, object?> { ["format"] = null });

        var fields = new Dictionary<string, object?>
        {
            ["format"] = "NCSD",
            ["mediaId"] = BinaryPrimitives.ReadUInt64LittleEndian(rom[0x108..]).ToString("X16"),
            ["productCode"] = null,
            ["makerCode"] = null,
            ["programId"] = null,
            ["encrypted"] = null,
        };

        var ncch = BinaryPrimitives.ReadUInt32LittleEndian(rom[0x120..]) * MediaUnit;
        if (ncch + 0x200 <= rom.Length && rom.Slice((int)ncch + 0x100, 4).SequenceEqual("NCCH"u8))
        {
            var partition = rom[(int)ncch..];
            fields["productCode"] = ProductCode(partition.Slice(0x150, 16));
            fields["makerCode"] = Text(partition.Slice(0x110, 2));
            fields["programId"] = BinaryPrimitives.ReadUInt64LittleEndian(partition[0x118..]).ToString("X16");
            // 3DS H1: NCCH flags[7] (0x18F) bit 2 is NoCrypto.
            fields["encrypted"] = (partition[0x18F] & 0x04) == 0;
        }

        return new RomHeader(Nintendo3DsId, null, true, fields);
    }

    /// <summary>Up to the first NUL, trailing spaces trimmed; null when nothing is left.</summary>
    private static string? ProductCode(ReadOnlySpan<byte> area)
    {
        var end = area.IndexOf((byte)0);
        var code = Text(end < 0 ? area : area[..end]).TrimEnd(' ');
        return code.Length > 0 ? code : null;
    }

    /// <summary>Bytes 0x20–0x7E kept; every other byte becomes <c>?</c>.</summary>
    private static string Text(ReadOnlySpan<byte> area)
    {
        var chars = new char[area.Length];
        for (var i = 0; i < area.Length; i++)
            chars[i] = area[i] is >= 0x20 and <= 0x7E ? (char)area[i] : '?';
        return new string(chars);
    }
}
