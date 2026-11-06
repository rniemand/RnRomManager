using RnRomManager.TestData;

namespace RnRomManager.Systems.Nintendo64.Tests;

public class SyntheticN64RomBuilderTests
{
    [Test]
    public void SameProperties_SameBytes_OtherSeed_OtherBytes()
    {
        var rom = new SyntheticN64RomBuilder { Seed = 5, Title = "SAME" };
        Assert.That(rom.Build(), Is.EqualTo(rom.Build()));
        Assert.That((rom with { Seed = 6 }).Build(), Is.Not.EqualTo(rom.Build()));
        Assert.That(rom.Build(), Has.Length.EqualTo(1024 * 1024));
    }

    [Test]
    public void EveryPropertyReadsBack()
    {
        var rom = new SyntheticN64RomBuilder
        {
            Seed = 7, SizeBytes = 8192, Title = "READ BACK", GameCode = "NRBP", Version = 3,
            Crc1 = 0xDEADBEEF, Crc2 = 0x01020304, BootAddress = 0x80001000,
        };

        var header = Nintendo64HeaderReader.Read(rom.Build())!;

        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["title"] = "READ BACK",
            ["gameCode"] = "NRBP",
            ["mediaType"] = "Cartridge",
            ["destination"] = "Europe",
            ["version"] = 3,
            ["crc1"] = "0xDEADBEEF",
            ["crc2"] = "0x01020304",
            ["bootAddress"] = "0x80001000",
            ["headerValid"] = true,
            ["romSizeBytes"] = 8192,
        }));
    }

    [Test]
    public void ByteOrders_PutThe0x80MarkerWhereTheRuleExpectsIt()
    {
        var rom = new SyntheticN64RomBuilder { Seed = 8, SizeBytes = 4096 };
        Assert.That(rom.Build()[0], Is.EqualTo(0x80));
        Assert.That((rom with { ByteOrder = N64ByteOrder.V64 }).Build()[1], Is.EqualTo(0x80));
        Assert.That((rom with { ByteOrder = N64ByteOrder.N64 }).Build()[3], Is.EqualTo(0x80));
    }
}
