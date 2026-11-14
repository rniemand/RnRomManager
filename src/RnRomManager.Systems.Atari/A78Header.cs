using System.Buffers.Binary;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari;

/// <summary>The optional 128-byte <c>.a78</c> header: <c>ATARI7800</c> at 0x01. A headerless <c>.bin</c> returns null.</summary>
internal static class A78Header
{
    public static RomHeader? Read(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 128 || !rom.Slice(0x01, 9).SequenceEqual("ATARI7800"u8))
            return null;

        var title = LynxHeader.ReadText(rom.Slice(0x11, 32));
        return new RomHeader("a7800", title, true, new Dictionary<string, object?>
        {
            ["version"] = rom[0x00],
            ["title"] = title,
            ["romSize"] = BinaryPrimitives.ReadUInt32BigEndian(rom[0x31..]),
            ["cartType"] = BinaryPrimitives.ReadUInt16BigEndian(rom[0x35..]),
            ["controller1"] = rom[0x37],
            ["controller2"] = rom[0x38],
            ["tvType"] = (rom[0x39] & 1) == 0 ? "ntsc" : "pal",
        });
    }
}
