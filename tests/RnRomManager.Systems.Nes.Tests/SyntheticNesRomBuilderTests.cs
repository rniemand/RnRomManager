using RnRomManager.TestData;

namespace RnRomManager.Systems.Nes.Tests;

public class SyntheticNesRomBuilderTests
{
    [Test]
    public void SameProperties_SameBytes_OtherSeed_OtherBytes()
    {
        var rom = new SyntheticNesRomBuilder { Seed = 5 };
        Assert.That(rom.Build(), Is.EqualTo(rom.Build()));
        Assert.That((rom with { Seed = 6 }).Build(), Is.Not.EqualTo(rom.Build()));
        Assert.That(rom.Build(), Has.Length.EqualTo(16 + 2 * 16384 + 8192));
    }

    [Test]
    public void INes_EveryPropertyReadsBack()
    {
        var rom = new SyntheticNesRomBuilder
        {
            Seed = 7, PrgBanks = 4, ChrBanks = 2, Mapper = 0x42, Mirroring = NesMirroring.Vertical, Battery = true, Trainer = true, Pal = true,
        }.Build();

        Assert.That(rom, Has.Length.EqualTo(16 + 512 + 4 * 16384 + 2 * 8192));
        Assert.That(NesHeaderReader.Read(rom)!.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["headerValid"] = true,
            ["format"] = "iNES",
            ["mapper"] = 0x42,
            ["prgRomSizeBytes"] = 4 * 16384,
            ["chrRomSizeBytes"] = 2 * 8192,
            ["mirroring"] = "Vertical",
            ["battery"] = true,
            ["trainer"] = true,
            ["tvSystem"] = "PAL",
            ["romSizeBytes"] = rom.Length,
        }));
    }

    [Test]
    public void Nes20_MapperAboveFfAndFourScreen_ReadBack()
    {
        var rom = new SyntheticNesRomBuilder { Seed = 8, Nes20 = true, Mapper = 0x123, Mirroring = NesMirroring.FourScreen }.Build();

        var fields = NesHeaderReader.Read(rom)!.Fields;

        Assert.That((fields["format"], fields["mapper"], fields["mirroring"], fields["tvSystem"]),
            Is.EqualTo(("NES 2.0", 0x123, "Four-screen", "NTSC")));
    }

    [Test]
    public void CorruptMagic_IsInvalid()
    {
        var header = NesHeaderReader.Read(new SyntheticNesRomBuilder { CorruptMagic = true }.Build())!;
        Assert.That((header.IsPlausible, header.Fields["headerValid"]), Is.EqualTo((false, false)));
    }
}
