using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>Spec B2: the four optional C64 container headers, recognised by their magic at offset 0.</summary>
internal static class C64Header
{
    public static RomHeader? Read(ReadOnlySpan<byte> rom, string systemId)
    {
        if (rom.Length >= 64 && rom.StartsWith("C64 CARTRIDGE   "u8))
        {
            var name = rom.Slice(0x20, 32);
            var end = name.IndexOf((byte)0);
            var title = Encoding.Latin1.GetString(end < 0 ? name : name[..end]).Trim();
            var crtTitle = title.Length == 0 ? null : title;
            return new RomHeader(systemId, crtTitle, true, new Dictionary<string, object?>
            {
                ["format"] = "crt",
                ["version"] = $"{rom[0x14]}.{rom[0x15]}",
                ["hardwareType"] = (int)BinaryPrimitives.ReadUInt16BigEndian(rom[0x16..]),
                ["exrom"] = (int)rom[0x18],
                ["game"] = (int)rom[0x19],
                ["title"] = crtTitle,
            });
        }
        if (rom.Length >= 20 && rom.StartsWith("C64-TAPE-RAW"u8))
            return Plain(systemId, "tap", rom[0x0C]);
        if (rom.Length >= 12 && rom.StartsWith("GCR-1541"u8))
            return new RomHeader(systemId, null, true, new Dictionary<string, object?>
            {
                ["format"] = "g64", ["version"] = (int)rom[0x08], ["tracks"] = (int)rom[0x09],
            });
        if (rom.Length >= 14 && rom.StartsWith("MNIB-1541-RAW"u8))
            return Plain(systemId, "nib", rom[0x0D]);
        return null;
    }

    private static RomHeader Plain(string systemId, string format, byte version) =>
        new(systemId, null, true, new Dictionary<string, object?> { ["format"] = format, ["version"] = (int)version });
}
