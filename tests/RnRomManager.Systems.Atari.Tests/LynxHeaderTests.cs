using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.Systems.Atari.Tests;

public class LynxHeaderTests
{
    private static readonly AtariLynxHandler Handler = new();

    [Test]
    public void Fields_AreReadFromThe64ByteHeader()
    {
        var rom = Header();
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x04), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x06), 1024);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x08), 1);
        Encoding.Latin1.GetBytes("  Café \0ignored").CopyTo(rom, 0x0A);
        Encoding.Latin1.GetBytes("ABCDEFGHIJKLMNOP").CopyTo(rom, 0x2A);

        var header = Handler.ReadHeader(rom)!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("lynx", "Café", true)));
        Assert.That(header.Fields["bank0PageSize"], Is.EqualTo(512));
        Assert.That(header.Fields["bank1PageSize"], Is.EqualTo(1024));
        Assert.That(header.Fields["version"], Is.EqualTo(1));
        Assert.That(header.Fields["title"], Is.EqualTo("Café"));
        Assert.That(header.Fields["manufacturer"], Is.EqualTo("ABCDEFGHIJKLMNOP"));
    }

    [TestCase(0, "none")]
    [TestCase(1, "left")]
    [TestCase(2, "right")]
    [TestCase(255, 255)]
    public void Rotation_UsesNamesForKnownValues(byte value, object expected)
    {
        var rom = Header();
        rom[0x3A] = value;
        Assert.That(Handler.ReadHeader(rom)!.Fields["rotation"], Is.EqualTo(expected));
    }

    [Test]
    public void MissingOrShortHeader_ReturnsNull()
    {
        Assert.That(Handler.ReadHeader(Header()[..63]), Is.Null);
        Assert.That(Handler.ReadHeader(new byte[1024]), Is.Null);
        Assert.That(Handler.ReadHeader([]), Is.Null);
    }

    [Test]
    public void BlankText_ReturnsNull()
    {
        var rom = Header();
        rom.AsSpan(0x0A, 32).Fill((byte)' ');
        var header = Handler.ReadHeader(rom)!;
        Assert.That(header.Title, Is.Null);
        Assert.That(header.Fields["title"], Is.Null);
        Assert.That(header.Fields["manufacturer"], Is.Null);
    }

    private static byte[] Header()
    {
        var rom = new byte[64];
        "LYNX"u8.CopyTo(rom);
        return rom;
    }
}
