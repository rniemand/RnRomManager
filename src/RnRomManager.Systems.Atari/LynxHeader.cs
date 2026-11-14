using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari;

internal static class LynxHeader
{
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 64 || !rom[..4].SequenceEqual("LYNX"u8))
            return null;

        var title = ReadText(rom.Slice(0x0A, 32));
        return new RomHeader("lynx", title, true, new Dictionary<string, object?>
        {
            ["bank0PageSize"] = BinaryPrimitives.ReadUInt16LittleEndian(rom[0x04..]),
            ["bank1PageSize"] = BinaryPrimitives.ReadUInt16LittleEndian(rom[0x06..]),
            ["version"] = BinaryPrimitives.ReadUInt16LittleEndian(rom[0x08..]),
            ["title"] = title,
            ["manufacturer"] = ReadText(rom.Slice(0x2A, 16)),
            ["rotation"] = rom[0x3A] switch { 0 => "none", 1 => "left", 2 => "right", var rotation => rotation },
        });
    }

    internal static string? ReadText(ReadOnlySpan<byte> field)
    {
        var nul = field.IndexOf((byte)0);
        var value = Encoding.Latin1.GetString(nul < 0 ? field : field[..nul]).Trim();
        return value.Length == 0 ? null : value;
    }
}
