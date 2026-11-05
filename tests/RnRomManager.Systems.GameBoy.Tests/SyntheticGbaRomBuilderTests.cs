using RnRomManager.TestData;

namespace RnRomManager.Systems.GameBoy.Tests;

/// <summary>The builder is the base of every GBA test and fixture, so its output is checked against GBATEK directly.</summary>
public class SyntheticGbaRomBuilderTests
{
    private static readonly SyntheticGbaRomBuilder Clean = new() { Seed = 42, Title = "STARCOURIER", GameCode = "ASCE" };

    [Test]
    public void SameSettings_SameBytes_DifferentSeed_DifferentBytes()
    {
        Assert.That(Clean.Build(), Is.EqualTo(Clean.Build()));
        Assert.That((Clean with { Seed = 43 }).Build(), Is.Not.EqualTo(Clean.Build()));
    }

    [Test]
    public void Defaults()
    {
        var rom = new SyntheticGbaRomBuilder().Build();

        Assert.That(rom, Has.Length.EqualTo(256 * 1024));
        Assert.That(rom.AsSpan(0xA0, 16).ToArray(), Is.All.EqualTo(0), "no title, no game code");
        Assert.That(rom[0xB0..0xB2], Is.EqualTo("01"u8.ToArray()));
        Assert.That(rom[0xB2], Is.EqualTo(0x96));
    }

    [Test]
    public void Fields_LandAtTheirOffsets_AndReadBack()
    {
        var builder = Clean with { MakerCode = "08", UnitCode = 0x01, DeviceType = 0x80, Version = 3, SizeBytes = 512 * 1024 };
        var rom = builder.Build();
        var fields = GameBoyAdvanceHeaderReader.Read(rom)!.Fields;

        Assert.That(rom.AsSpan(0x04, 156).SequenceEqual(SyntheticGbaRomBuilder.NintendoLogo), Is.True);
        Assert.That(fields["title"], Is.EqualTo("STARCOURIER"));
        Assert.That(fields["gameCode"], Is.EqualTo("ASCE"));
        Assert.That(fields["makerCode"], Is.EqualTo("08"));
        Assert.That(fields["unitCode"], Is.EqualTo("0x01"));
        Assert.That(fields["deviceType"], Is.EqualTo("0x80"));
        Assert.That(fields["version"], Is.EqualTo(3));
        Assert.That(fields["fixedValueValid"], Is.True);
        Assert.That(fields["logoValid"], Is.True);
        Assert.That(fields["headerChecksumValid"], Is.True);
        Assert.That(fields["romSizeBytes"], Is.EqualTo(512 * 1024));
    }

    [Test]
    public void HeaderChecksum_MatchesTheGbatekFormula()
    {
        var rom = Clean.Build();
        var sum = 0;
        for (var i = 0xA0; i < 0xBD; i++)
            sum += rom[i];

        Assert.That((byte)(-(0x19 + sum)), Is.EqualTo(rom[0xBD]), "gbafix: -(0x19 + Σ)");
        Assert.That((Clean with { CorruptHeaderChecksum = true }).Build()[0xBD], Is.EqualTo((byte)(rom[0xBD] + 1)));
    }

    [Test]
    public void TooSmall_IsJustNoise() =>
        Assert.That((Clean with { SizeBytes = 100 }).Build(), Has.Length.EqualTo(100));
}
