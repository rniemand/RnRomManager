using System.Buffers.Binary;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Snes.Tests;

public class SyntheticSnesRomBuilderTests
{
    [Test]
    public void SameProperties_SameBytes_OtherSeed_OtherBytes()
    {
        var rom = new SyntheticSnesRomBuilder { Seed = 5 };
        Assert.That(rom.Build(), Is.EqualTo(rom.Build()));
        Assert.That((rom with { Seed = 6 }).Build(), Is.Not.EqualTo(rom.Build()));
        Assert.That(rom.Build(), Has.Length.EqualTo(512 * 1024));
    }

    [Test]
    public void EveryPropertyReadsBack()
    {
        var rom = new SyntheticSnesRomBuilder
        {
            Seed = 7, SizeKiB = 1024, Mapping = SnesMapping.HiROM, Title = "READ BACK", FastRom = true, RomType = 2,
            SramSizeByte = 3, Region = 2, Version = 1,
        }.Build();

        var header = SnesHeaderReader.Read(rom)!;

        Assert.That((header.Title, header.IsPlausible), Is.EqualTo(("READ BACK", true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["headerValid"] = true,
            ["title"] = "READ BACK",
            ["mapping"] = "HiROM",
            ["fastRom"] = true,
            ["romType"] = 2,
            ["romSizeKiB"] = 1024,
            ["sramSizeKiB"] = 8,
            ["region"] = "Europe",
            ["version"] = 1,
            ["checksum"] = BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(0xFFDE)).ToString("X4"),
            ["checksumValid"] = true,
            ["romSizeBytes"] = rom.Length,
        }));
    }

    [Test]
    public void Checksum_MatchesAnIndependentSum()
    {
        var rom = new SyntheticSnesRomBuilder { Seed = 9 }.Build();

        var sum = 0;
        foreach (var b in rom)
            sum = (sum + b) & 0xFFFF;

        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(0x7FDE)), Is.EqualTo(sum));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(0x7FDC)), Is.EqualTo(sum ^ 0xFFFF));
    }

    [Test]
    public void InvalidChecksum_WritesZeros()
    {
        var rom = new SyntheticSnesRomBuilder { ValidChecksum = false }.Build();
        Assert.That(rom.AsSpan(0x7FDC, 4).ToArray(), Is.EqualTo(new byte[4]));
    }

    [Test]
    public void CopierHeader_Prepends512Zeros()
    {
        var plain = new SyntheticSnesRomBuilder { Seed = 3 };
        var headered = (plain with { CopierHeader = true }).Build();

        Assert.That(headered[..512], Is.EqualTo(new byte[512]));
        Assert.That(headered[512..], Is.EqualTo(plain.Build()));
    }

    [Test]
    public void Title_IsLatin1()
    {
        var rom = new SyntheticSnesRomBuilder { Title = "\u0001±" }.Build();
        Assert.That(rom.AsSpan(0x7FC0, 3).ToArray(), Is.EqualTo(new byte[] { 0x01, 0xB1, 0x20 }));
    }

    [TestCase(3, SnesMapping.LoROM, TestName = "SizeKiB not a power of two")]
    [TestCase(16, SnesMapping.LoROM, TestName = "LoROM under 32 KiB")]
    [TestCase(32, SnesMapping.HiROM, TestName = "HiROM under 64 KiB")]
    [TestCase(4096, SnesMapping.ExHiROM, TestName = "ExHiROM under 8 MiB")]
    public void InvalidSize_Throws(int sizeKiB, SnesMapping mapping) =>
        Assert.Throws<ArgumentException>(() => new SyntheticSnesRomBuilder { SizeKiB = sizeKiB, Mapping = mapping }.Build());

    [Test]
    public void TitleOver21Chars_Throws() =>
        Assert.Throws<ArgumentException>(() => new SyntheticSnesRomBuilder { Title = new string('A', 22) }.Build());
}
