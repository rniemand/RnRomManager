using System.Text;
using RnRomManager.TestData;

namespace RnRomManager.Systems.NeoGeoPocket.Tests;

public class NeoGeoPocketHeaderReaderTests
{
    private static byte[] Header(string licence, byte mode = 0x00)
    {
        var rom = new byte[64];
        Encoding.ASCII.GetBytes(licence, rom);
        rom[0x23] = mode;
        return rom;
    }

    [Test]
    public void TooSmall_IsNull()
    {
        Assert.That(NeoGeoPocketHeaderReader.Read(new byte[63]), Is.Null);
    }

    [Test]
    public void Copyright_IsPlausible()
    {
        var header = NeoGeoPocketHeaderReader.Read(Header("COPYRIGHT BY SNK CORPORATION"))!;

        Assert.That((header.IsPlausible, header.Fields["licence"]), Is.EqualTo((true, "COPYRIGHT BY SNK CORPORATION")));
    }

    [Test]
    public void Licensed_IsPlausible_AndTrimmed()
    {
        var header = NeoGeoPocketHeaderReader.Read(Header(" LICENSED BY SNK CORPORATION"))!;

        Assert.That((header.IsPlausible, header.Fields["licence"]), Is.EqualTo((true, "LICENSED BY SNK CORPORATION")));
    }

    [TestCase("COPYRIGHT BY SNK CORPORATIOX")]
    [TestCase(" LICENSED BY SNK CORPORATIoN")]
    [TestCase("LICENSED BY SNK CORPORATION ")]
    public void OneByteOff_IsImplausible(string licence)
    {
        var header = NeoGeoPocketHeaderReader.Read(Header(licence))!;

        Assert.That((header.IsPlausible, header.Fields["licence"]), Is.EqualTo((false, (object?)null)));
    }

    [TestCase(0x10, "ngpc", true)]
    [TestCase(0x00, "ngp", false)]
    [TestCase(0x11, "ngp", false)]
    public void ModeByte_Classifies(byte mode, string system, bool colour)
    {
        var header = NeoGeoPocketHeaderReader.Read(Header("COPYRIGHT BY SNK CORPORATION", mode))!;

        Assert.That((header.ClassifiedSystemId, header.Fields["colour"]), Is.EqualTo((system, colour)));
    }

    [Test]
    public void ImplausibleColourHeader_StillClassifiesNgpc()
    {
        var header = NeoGeoPocketHeaderReader.Read(Header("NOT A LICENCE STRING AT ALL!", 0x10))!;

        Assert.That((header.IsPlausible, header.ClassifiedSystemId), Is.EqualTo((false, "ngpc")));
    }

    [Test]
    public void BuilderRom_EveryFieldInOrder()
    {
        var rom = new SyntheticNgpRomBuilder { Seed = 3, Title = "PRISMTOWER", Colour = true, Catalog = 0x1234, SubCatalog = 7 }.Build();

        var header = NeoGeoPocketHeaderReader.Read(rom)!;

        Assert.That(header.Title, Is.EqualTo("PRISMTOWER"));
        Assert.That(header.Fields.Keys, Is.EqualTo(new[] { "title", "licence", "catalog", "subCatalog", "mode", "colour", "startPc" }));
        Assert.That(header.Fields.Values, Is.EqualTo(new object?[] { "PRISMTOWER", "COPYRIGHT BY SNK CORPORATION", 0x1234, 7, "0x10", true, "0x00200040" }));
    }

    [Test]
    public void Title_CutAtNul_HighBytesBecomeQuestionMarks()
    {
        var rom = Header("COPYRIGHT BY SNK CORPORATION");
        Encoding.ASCII.GetBytes("AB", rom.AsSpan(0x24));
        rom[0x26] = 0x80;
        rom[0x27] = (byte)'C';
        rom[0x28] = 0x00;
        rom[0x29] = (byte)'Z';

        Assert.That(NeoGeoPocketHeaderReader.Read(rom)!.Fields["title"], Is.EqualTo("AB?C"));
    }

    [Test]
    public void AllSpaceTitle_IsNull()
    {
        var rom = new SyntheticNgpRomBuilder { Seed = 4 }.Build();

        var header = NeoGeoPocketHeaderReader.Read(rom)!;

        Assert.That((header.Title, header.Fields["title"]), Is.EqualTo(((string?)null, (object?)null)));
    }
}
