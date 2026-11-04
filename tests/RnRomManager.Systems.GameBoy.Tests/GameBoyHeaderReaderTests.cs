using RnRomManager.TestData;

namespace RnRomManager.Systems.GameBoy.Tests;

/// <summary>Spec §15.1 "GB header extractor" row, using <see cref="SyntheticRomBuilder"/> ROMs.</summary>
public class GameBoyHeaderReaderTests
{
    private static readonly SyntheticRomBuilder Rom = new() { Seed = 1, Title = "TETRIS", CartridgeType = 0x01, RomSizeCode = 0x01, Version = 1 };

    private static IReadOnlyDictionary<string, object?> Fields(SyntheticRomBuilder rom) => GameBoyHeaderReader.Read(rom.Build())!.Fields;

    [Test]
    public void EveryField_OfAnOriginalGameBoyCart()
    {
        var header = GameBoyHeaderReader.Read(Rom.Build())!;

        Assert.That(header.ClassifiedSystemId, Is.EqualTo("gb"));
        Assert.That(header.Title, Is.EqualTo("TETRIS"));
        Assert.That(header.IsPlausible, Is.True);
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["title"] = "TETRIS",
            ["manufacturerCode"] = null,
            ["cgbFlag"] = "0x00",
            ["sgbFlag"] = "0x00",
            ["cartridgeType"] = "MBC1",
            ["romSizeBytes"] = 64 * 1024,
            ["ramSizeBytes"] = 0,
            ["destination"] = "Overseas",
            ["licensee"] = "Nintendo",
            ["version"] = 1,
            ["logoValid"] = true,
            ["headerChecksumValid"] = true,
            ["globalChecksumValid"] = true,
            ["sizeMatchesHeader"] = true,
            ["isMbc1Multicart"] = false,
        }));
        Assert.That(header.Fields.Keys, Is.EqualTo(new[]
        {
            "title", "manufacturerCode", "cgbFlag", "sgbFlag", "cartridgeType", "romSizeBytes", "ramSizeBytes", "destination",
            "licensee", "version", "logoValid", "headerChecksumValid", "globalChecksumValid", "sizeMatchesHeader", "isMbc1Multicart",
        }), "§8.2 order");
    }

    [TestCase((byte)0x80, "gbc")]
    [TestCase((byte)0xC0, "gbc")]
    [TestCase((byte)0x84, "gbc")]
    [TestCase((byte)0x40, "gb")]
    [TestCase((byte)0x00, "gb")]
    public void Classification_IsBit7OfTheCgbFlag(byte flag, string expected)
    {
        var header = GameBoyHeaderReader.Read((Rom with { CgbFlag = flag }).Build())!;

        Assert.That(header.ClassifiedSystemId, Is.EqualTo(expected));
        Assert.That(header.Fields["cgbFlag"], Is.EqualTo($"0x{flag:X2}"));
    }

    [Test]
    public void SgbFlag_IsReported()
    {
        Assert.That(Fields(Rom with { SgbFlag = 0x03 })["sgbFlag"], Is.EqualTo("0x03"));
    }

    [Test]
    public void TitleLength_DependsOnCgbAndManufacturerCode()
    {
        const string sixteen = "ABCDEFGHIJKLMNOP";

        Assert.That(Fields(Rom with { Title = sixteen })["title"], Is.EqualTo(sixteen), "original GB: 16 chars");
        // A '-' at 0x13F means those 4 bytes can't be a manufacturer code (§8.1 needs A–Z0–9).
        Assert.That(Fields(Rom with { Title = "ABCDEFGHIJK-MNOP", CgbFlag = 0x80 })["title"], Is.EqualTo("ABCDEFGHIJK-MNO"), "CGB: 15");
        Assert.That(Fields(Rom with { Title = sixteen, CgbFlag = 0x80 })["manufacturerCode"], Is.EqualTo("LMNO"),
            "4 uppercase title chars at 0x13F read as a code: the §8.1 rule, ambiguous on real carts too");
        var withCode = Fields(Rom with { Title = "POKEMON YEL", ManufacturerCode = "BYTE", CgbFlag = 0x80 });
        Assert.That((withCode["title"], withCode["manufacturerCode"]), Is.EqualTo(("POKEMON YEL", (object?)"BYTE")), "code: 11");
    }

    [Test]
    public void ManufacturerCode_NeedsCgbAndUppercaseAlphanumerics()
    {
        Assert.That(Fields(Rom with { ManufacturerCode = "BYTE" })["manufacturerCode"], Is.Null, "not CGB");
        Assert.That(Fields(Rom with { ManufacturerCode = "by e", CgbFlag = 0x80 })["manufacturerCode"], Is.Null);
    }

    [Test]
    public void Title_NonPrintableBecomesQuestionMark_AndIsTrimmed()
    {
        var rom = Rom.Build();
        "  A\u0001B  "u8.CopyTo(rom.AsSpan(0x134));
        rom[0x13B] = 0;

        Assert.That(GameBoyHeaderReader.Read(rom)!.Title, Is.EqualTo("A?B"));
    }

    [Test]
    public void EmptyTitle_IsNull()
    {
        Assert.That(GameBoyHeaderReader.Read((Rom with { Title = "" }).Build())!.Title, Is.Null);
    }

    [Test]
    public void NewLicensee_IsUsedWhenOldIs0x33()
    {
        Assert.That(Fields(Rom with { NewLicensee = "08" })["licensee"], Is.EqualTo("Capcom"));
        Assert.That(Fields(Rom with { NewLicensee = "ZZ" })["licensee"], Is.EqualTo("Unknown (0x5A5A)"));
    }

    [Test]
    public void UnknownTableValues_AreReportedWithTheirHex()
    {
        var fields = Fields(Rom with { CartridgeType = 0x04, OldLicensee = 0x02, Destination = 0x07 });

        Assert.That(fields["cartridgeType"], Is.EqualTo("Unknown (0x04)"));
        Assert.That(fields["licensee"], Is.EqualTo("Unknown (0x02)"));
        Assert.That(fields["destination"], Is.EqualTo("Unknown (0x07)"));
    }

    [TestCase((byte)0x00, 0)]
    [TestCase((byte)0x01, 0)]
    [TestCase((byte)0x02, 8 * 1024)]
    [TestCase((byte)0x03, 32 * 1024)]
    [TestCase((byte)0x04, 128 * 1024)]
    [TestCase((byte)0x05, 64 * 1024)]
    [TestCase((byte)0x06, null)]
    public void RamSize(byte code, int? expected)
    {
        Assert.That(Fields(Rom with { RamSizeCode = code })["ramSizeBytes"], Is.EqualTo(expected));
    }

    [TestCase((byte)0x05)]
    [TestCase((byte)0x06)]
    public void Mbc2_Has512BytesOfBuiltInRam(byte cartridgeType)
    {
        Assert.That(Fields(Rom with { CartridgeType = cartridgeType })["ramSizeBytes"], Is.EqualTo(512));
    }

    [Test]
    public void BadChecksums_AreFlagged_LogoAloneKeepsItPlausible()
    {
        var header = GameBoyHeaderReader.Read((Rom with { CorruptHeaderChecksum = true, CorruptGlobalChecksum = true }).Build())!;

        Assert.That(header.Fields["headerChecksumValid"], Is.False);
        Assert.That(header.Fields["globalChecksumValid"], Is.False);
        Assert.That(header.IsPlausible, Is.True);
    }

    [Test]
    public void BadLogo_ValidChecksumKeepsItPlausible_BothBadIsNot()
    {
        var badLogo = GameBoyHeaderReader.Read((Rom with { CorruptLogo = true }).Build())!;
        var bothBad = GameBoyHeaderReader.Read((Rom with { CorruptLogo = true, CorruptHeaderChecksum = true }).Build())!;

        Assert.That((badLogo.Fields["logoValid"], badLogo.IsPlausible), Is.EqualTo(((object?)false, true)));
        Assert.That(bothBad.IsPlausible, Is.False);
    }

    [TestCase(32 * 1024)]
    [TestCase(64 * 1024 + 1)]
    public void SizeMismatch_IsFlagged(int size)
    {
        var fields = Fields(Rom with { SizeBytes = size });

        Assert.That(fields["sizeMatchesHeader"], Is.False);
        Assert.That(fields["romSizeBytes"], Is.EqualTo(64 * 1024));
        Assert.That(fields["globalChecksumValid"], Is.True);
    }

    [Test]
    public void RomSizeCodeOutside0To8_IsNullAndNeverMatches()
    {
        var fields = Fields(Rom with { RomSizeCode = 0x52, SizeBytes = 64 * 1024 });

        Assert.That((fields["romSizeBytes"], fields["sizeMatchesHeader"]), Is.EqualTo(((object?)null, (object?)false)));
    }

    [Test]
    public void Mbc1Multicart_IsDetected()
    {
        Assert.That(Fields(SyntheticRomBuilder.Mbc1Multicart())["isMbc1Multicart"], Is.True);
    }

    [Test]
    public void Mbc1Multicart_NeedsAllThreeConditions()
    {
        var multicart = SyntheticRomBuilder.Mbc1Multicart();

        Assert.That(Fields(multicart with { CartridgeType = 0x19 })["isMbc1Multicart"], Is.False, "not MBC1");
        Assert.That(Fields(multicart with { SecondLogoForMulticart = false })["isMbc1Multicart"], Is.False, "no second logo");
        Assert.That(Fields(multicart with { RomSizeCode = 0x06 })["isMbc1Multicart"], Is.False, "2 MiB");
    }

    [TestCase(0)]
    [TestCase(0x14F)]
    public void SmallerThanAHeader_IsNull(int size)
    {
        Assert.That(GameBoyHeaderReader.Read(new byte[size]), Is.Null);
    }

    [Test]
    public void ExactlyAHeader_IsRead()
    {
        Assert.That(GameBoyHeaderReader.Read((Rom with { SizeBytes = 0x150 }).Build()), Is.Not.Null);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void RandomGarbage_NeverThrows_AndIsPlausibleOnlyByAChecksumFluke(int seed)
    {
        var garbage = new byte[64 * 1024];
        new Random(seed).NextBytes(garbage);

        var header = GameBoyHeaderReader.Read(garbage);

        Assert.That(header, Is.Not.Null);
        Assert.That(header!.Fields["logoValid"], Is.False);
        // A random byte matches the header checksum 1 time in 256; that alone is what IsPlausible would rest on.
        Assert.That(header.IsPlausible, Is.EqualTo(header.Fields["headerChecksumValid"]));
        Assert.That(header.Fields, Has.Count.EqualTo(15));
    }

    [Test]
    public void Tables_MatchTheSpecCartridgeList()
    {
        // §8.3 lists 28 types; Pan Docs' licensee lists have 146 old (without 0x33) and 64 new codes.
        Assert.That(GameBoyHeaderTables.CartridgeTypes, Has.Count.EqualTo(28));
        Assert.That(GameBoyHeaderTables.CartridgeTypes[0xFF], Is.EqualTo("HuC1+RAM+BATTERY"));
        Assert.That(GameBoyHeaderTables.OldLicensees, Has.Count.EqualTo(146).And.Not.ContainKey((byte)0x33));
        Assert.That(GameBoyHeaderTables.NewLicensees, Has.Count.EqualTo(64));
    }
}
