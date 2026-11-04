using System.Security.Cryptography;
using RnRomManager.TestData;

namespace RnRomManager.Systems.GameBoy.Tests;

/// <summary>The builder is the base of every GB test and fixture, so its output is checked against §8.1 directly.</summary>
public class SyntheticRomBuilderTests
{
    private static readonly SyntheticRomBuilder Clean = new() { Seed = 42, Title = "TETRIS", CartridgeType = 0x01, RomSizeCode = 0x01 };

    [Test]
    public void SameSettings_SameBytes_DifferentSeed_DifferentBytes()
    {
        Assert.That(Clean.Build(), Is.EqualTo(Clean.Build()));
        Assert.That((Clean with { Seed = 43 }).Build(), Is.Not.EqualTo(Clean.Build()));
    }

    [Test]
    public void DefaultBuild_IsPinned()
    {
        // Fixture DATs and the committed basic.7z depend on these exact bytes; a change here breaks them.
        Assert.That(Convert.ToHexStringLower(SHA1.HashData(new SyntheticRomBuilder().Build())), Is.EqualTo("adf16a90ebd02c4763682b20f932b6049d0330ae"));
    }

    [Test]
    public void Checksums_AreValidByTheSpecFormulas()
    {
        var rom = Clean.Build();

        Assert.That(rom[0x14D], Is.EqualTo(HeaderChecksum(rom)));
        Assert.That((rom[0x14E] << 8) | rom[0x14F], Is.EqualTo(GlobalChecksum(rom)));
    }

    [Test]
    public void Fields_LandAtTheirOffsets()
    {
        var rom = (Clean with
        {
            Title = "POKEMON",
            ManufacturerCode = "AAUE",
            CgbFlag = 0x80,
            SgbFlag = 0x03,
            CartridgeType = 0x1B,
            RomSizeCode = 0x02,
            RamSizeCode = 0x03,
            Destination = 0x00,
            NewLicensee = "01",
            Version = 0x02,
        }).Build();

        Assert.That(rom, Has.Length.EqualTo(128 * 1024));
        Assert.That(rom.AsSpan(0x104, 48).ToArray(), Is.EqualTo(SyntheticRomBuilder.NintendoLogo.ToArray()));
        Assert.That(System.Text.Encoding.ASCII.GetString(rom, 0x134, 11), Is.EqualTo("POKEMON\0\0\0\0"));
        Assert.That(System.Text.Encoding.ASCII.GetString(rom, 0x13F, 4), Is.EqualTo("AAUE"));
        Assert.That(System.Text.Encoding.ASCII.GetString(rom, 0x144, 2), Is.EqualTo("01"));
        Assert.That(rom[0x143..0x14D], Is.EqualTo(new byte[] { 0x80, (byte)'0', (byte)'1', 0x03, 0x1B, 0x02, 0x03, 0x00, 0x33, 0x02 }));
    }

    [Test]
    public void OldLicensee_IsUsedWithoutANewOne()
    {
        var rom = (Clean with { OldLicensee = 0x08 }).Build();

        Assert.That((rom[0x144], rom[0x145], rom[0x14B]), Is.EqualTo(((byte)0, (byte)0, (byte)0x08)));
    }

    [Test]
    public void CorruptLogo_ChangesOnlyTheLogoAndTheGlobalChecksum()
    {
        var diff = Differences(Clean.Build(), (Clean with { CorruptLogo = true }).Build());

        Assert.That(diff.Where(i => i is < 0x14E or > 0x14F), Is.EqualTo(new[] { 0x104 + 10 }));
    }

    [Test]
    public void CorruptHeaderChecksum_ChangesTheHeaderChecksumAndTheGlobalSumThatCoversIt()
    {
        var rom = (Clean with { CorruptHeaderChecksum = true }).Build();

        Assert.That(Differences(Clean.Build(), rom).Where(i => i is < 0x14E or > 0x14F), Is.EqualTo(new[] { 0x14D }));
        Assert.That(rom[0x14D], Is.Not.EqualTo(HeaderChecksum(rom)));
        Assert.That((rom[0x14E] << 8) | rom[0x14F], Is.EqualTo(GlobalChecksum(rom)), "global sum stays consistent");
    }

    [Test]
    public void CorruptGlobalChecksum_ChangesOnlyTheGlobalChecksum()
    {
        var rom = (Clean with { CorruptGlobalChecksum = true }).Build();

        Assert.That(Differences(Clean.Build(), rom), Is.SubsetOf(new[] { 0x14E, 0x14F }).And.Not.Empty);
        Assert.That((rom[0x14E] << 8) | rom[0x14F], Is.Not.EqualTo(GlobalChecksum(rom)));
        Assert.That(rom[0x14D], Is.EqualTo(HeaderChecksum(rom)));
    }

    [TestCase(16 * 1024)]
    [TestCase(64 * 1024 + 512)]
    public void SizeBytes_OverridesTheLengthButNotTheHeaderSize(int size)
    {
        var rom = (Clean with { SizeBytes = size }).Build();

        Assert.That(rom, Has.Length.EqualTo(size));
        Assert.That(rom[0x148], Is.EqualTo(0x01), "header still says 64 KiB");
        Assert.That(rom[0x14D], Is.EqualTo(HeaderChecksum(rom)));
        Assert.That((rom[0x14E] << 8) | rom[0x14F], Is.EqualTo(GlobalChecksum(rom)));
    }

    [Test]
    public void SmallerThanAHeader_IsJustNoise()
    {
        Assert.That((Clean with { SizeBytes = 0x14F }).Build(), Has.Length.EqualTo(0x14F));
    }

    [Test]
    public void Mbc1Multicart_Is1MiBMbc1WithASecondLogo()
    {
        var rom = SyntheticRomBuilder.Mbc1Multicart(seed: 7).Build();

        Assert.That(rom, Has.Length.EqualTo(1024 * 1024));
        Assert.That((rom[0x147], rom[0x148]), Is.EqualTo(((byte)0x01, (byte)0x05)));
        Assert.That(rom.AsSpan(0x40104, 48).ToArray(), Is.EqualTo(SyntheticRomBuilder.NintendoLogo.ToArray()));
        Assert.That(rom[0x14D], Is.EqualTo(HeaderChecksum(rom)));
    }

    // Independent copies of the §8.1 formulas, so the test doesn't just compare the builder with itself.
    private static byte HeaderChecksum(byte[] rom)
    {
        var x = 0;
        for (var i = 0x134; i <= 0x14C; i++)
            x = (x - rom[i] - 1) & 0xFF;
        return (byte)x;
    }

    private static int GlobalChecksum(byte[] rom) =>
        rom.Select((b, i) => i is 0x14E or 0x14F ? 0 : b).Sum() & 0xFFFF;

    private static int[] Differences(byte[] a, byte[] b) =>
        Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToArray();
}
