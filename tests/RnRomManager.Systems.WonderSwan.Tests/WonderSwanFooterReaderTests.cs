using RnRomManager.TestData;

namespace RnRomManager.Systems.WonderSwan.Tests;

public class WonderSwanFooterReaderTests
{
    private static byte[] Footer(byte opcode = 0xEA, byte colour = 0x00)
    {
        var rom = new byte[16];
        rom[0x0] = opcode;
        rom[0x7] = colour;
        return rom;
    }

    [Test]
    public void TooSmall_IsNull()
    {
        Assert.That(WonderSwanFooterReader.Read(new byte[15]), Is.Null);
    }

    [Test]
    public void SixteenBytes_WithTheJump_IsPlausibleWs()
    {
        var header = WonderSwanFooterReader.Read(Footer())!;

        Assert.That((header.IsPlausible, header.ClassifiedSystemId), Is.EqualTo((true, "ws")));
    }

    [Test]
    public void OtherOpcode_IsImplausible()
    {
        var rom = new SyntheticWsRomBuilder { Seed = 1, Opcode = 0xEB }.Build();

        Assert.That(WonderSwanFooterReader.Read(rom)!.IsPlausible, Is.False);
    }

    [TestCase(0x01, "wsc", true)]
    [TestCase(0x00, "ws", false)]
    [TestCase(0x02, "ws", false)]
    public void ColourByte_Classifies(byte colour, string system, bool isColour)
    {
        var header = WonderSwanFooterReader.Read(Footer(colour: colour))!;

        Assert.That((header.ClassifiedSystemId, header.Fields["colour"]), Is.EqualTo((system, isColour)));
    }

    [Test]
    public void ImplausibleColourFooter_StillClassifiesWsc()
    {
        var header = WonderSwanFooterReader.Read(Footer(opcode: 0x00, colour: 0x01))!;

        Assert.That((header.IsPlausible, header.ClassifiedSystemId), Is.EqualTo((false, "wsc")));
    }

    [Test]
    public void Title_IsNull()
    {
        Assert.That(WonderSwanFooterReader.Read(new SyntheticWsRomBuilder { Seed = 2 }.Build())!.Title, Is.Null);
    }

    [Test]
    public void BuilderRom_EveryFieldInOrder()
    {
        var rom = new SyntheticWsRomBuilder { Seed = 3 }.Build();
        var checksum = "0x" + (rom[^1] << 8 | rom[^2]).ToString("X4");

        var header = WonderSwanFooterReader.Read(rom)!;

        Assert.That(header.Fields.Keys, Is.EqualTo(new[]
        {
            "publisherId", "colour", "gameId", "version", "romSize", "saveType", "vertical", "mapper", "checksum", "checksumValid",
        }));
        Assert.That(header.Fields.Values, Is.EqualTo(new object?[] { "0x01", false, 1, 0, "0x02", "0x00", false, "0x00", checksum, true }));
    }

    [Test]
    public void Vertical_IsBitZero()
    {
        var header = WonderSwanFooterReader.Read(new SyntheticWsRomBuilder { Seed = 4, Vertical = true }.Build())!;

        Assert.That(header.Fields["vertical"], Is.True);
    }

    [Test]
    public void BadChecksum_IsShownButStillPlausible()
    {
        var header = WonderSwanFooterReader.Read(new SyntheticWsRomBuilder { Seed = 5, BadChecksum = true }.Build())!;

        Assert.That((header.Fields["checksumValid"], header.IsPlausible), Is.EqualTo((false, true)));
    }

    [Test]
    public void SixteenByteRom_ChecksumCoversItsFirstFourteenBytes()
    {
        var rom = Footer();
        rom[0x7] = 0x01;
        rom[0x8] = 0x02;
        // 0xEA + 0x01 + 0x02 = 0xED; the checksum bytes themselves aren't summed.
        rom[0xE] = 0xED;
        rom[0xF] = 0x00;

        var header = WonderSwanFooterReader.Read(rom)!;

        Assert.That((header.Fields["checksum"], header.Fields["checksumValid"]), Is.EqualTo(("0x00ED", true)));
    }
}
