using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.Systems.Atari.Tests;

public class A78HeaderTests
{
    private static readonly Atari7800Handler Handler = new();

    [Test]
    public void Fields_AreReadFromThe128ByteHeader_InOrder()
    {
        var rom = Header();
        rom[0x00] = 3;
        Encoding.Latin1.GetBytes("  Café \0ignored").CopyTo(rom, 0x11);
        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x31), 0x8000_0010);
        BinaryPrimitives.WriteUInt16BigEndian(rom.AsSpan(0x35), 0xFF02);
        rom[0x37] = 1;
        rom[0x38] = 200;

        var header = Handler.ReadHeader(rom)!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("a7800", "Café", true)));
        Assert.That(header.Fields.Keys,
            Is.EqualTo(new[] { "version", "title", "romSize", "cartType", "controller1", "controller2", "tvType" }));
        Assert.That(header.Fields["version"], Is.EqualTo(3));
        Assert.That(header.Fields["title"], Is.EqualTo("Café"));
        Assert.That(header.Fields["romSize"], Is.EqualTo(0x8000_0010u));
        Assert.That(header.Fields["cartType"], Is.EqualTo(0xFF02));
        Assert.That(header.Fields["controller1"], Is.EqualTo(1));
        Assert.That(header.Fields["controller2"], Is.EqualTo(200));
        Assert.That(header.Fields["tvType"], Is.EqualTo("ntsc"));
    }

    [TestCase(0, "ntsc")]
    [TestCase(1, "pal")]
    [TestCase(2, "ntsc")]
    [TestCase(3, "pal")]
    public void TvType_IsBit0(byte value, string expected)
    {
        var rom = Header();
        rom[0x39] = value;
        Assert.That(Handler.ReadHeader(rom)!.Fields["tvType"], Is.EqualTo(expected));
    }

    [Test]
    public void BlankTitle_ReturnsNull()
    {
        var rom = Header();
        rom.AsSpan(0x11, 32).Fill((byte)' ');
        var header = Handler.ReadHeader(rom)!;
        Assert.That(header.Title, Is.Null);
        Assert.That(header.Fields["title"], Is.Null);
    }

    [Test]
    public void MissingMagicOrShortFile_ReturnsNull()
    {
        Assert.That(Handler.ReadHeader(Header()[..127]), Is.Null);
        Assert.That(Handler.ReadHeader(new byte[4096]), Is.Null);
        var atZero = new byte[128];
        "ATARI7800"u8.CopyTo(atZero);
        Assert.That(Handler.ReadHeader(atZero), Is.Null);
        Assert.That(Handler.ReadHeader([]), Is.Null);
    }

    private static byte[] Header()
    {
        var rom = new byte[128];
        "ATARI7800"u8.CopyTo(rom.AsSpan(1));
        return rom;
    }
}
