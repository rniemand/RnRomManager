using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Systems.GameBoy.Tests;

/// <summary>GBA spec B4/B5, using <see cref="SyntheticGbaRomBuilder"/> ROMs.</summary>
public class GameBoyAdvanceHeaderReaderTests
{
    private static readonly SyntheticGbaRomBuilder Rom = new() { Seed = 1, Title = "STARCOURIER", GameCode = "ASCE", MakerCode = "01", Version = 2 };

    private static IReadOnlyDictionary<string, object?> Fields(SyntheticGbaRomBuilder rom) => GameBoyAdvanceHeaderReader.Read(rom.Build())!.Fields;

    [Test]
    public void EveryField_OfAValidCart()
    {
        var header = GameBoyAdvanceHeaderReader.Read(Rom.Build())!;

        Assert.That(header.ClassifiedSystemId, Is.EqualTo("gba"));
        Assert.That(header.Title, Is.EqualTo("STARCOURIER"));
        Assert.That(header.IsPlausible, Is.True);
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["title"] = "STARCOURIER",
            ["gameCode"] = "ASCE",
            ["destination"] = "USA",
            ["makerCode"] = "01",
            ["maker"] = "Nintendo Research & Development 1",
            ["unitCode"] = "0x00",
            ["deviceType"] = "0x00",
            ["version"] = 2,
            ["fixedValueValid"] = true,
            ["logoValid"] = true,
            ["headerChecksumValid"] = true,
            ["romSizeBytes"] = 256 * 1024,
        }));
        Assert.That(header.Fields.Keys, Is.EqualTo(new[]
        {
            "title", "gameCode", "destination", "makerCode", "maker", "unitCode", "deviceType", "version",
            "fixedValueValid", "logoValid", "headerChecksumValid", "romSizeBytes",
        }), "B4 order");
    }

    [Test]
    public void TooSmall_ReturnsNull_ExactlyTheHeader_Reads()
    {
        Assert.That(GameBoyAdvanceHeaderReader.Read(new byte[0xBF]), Is.Null);
        Assert.That(GameBoyAdvanceHeaderReader.Read((Rom with { SizeBytes = 0xC0 }).Build()), Is.Not.Null);
    }

    [Test]
    public void Title_StopsAtNul_MasksNonPrintable_TrimsAndEmptyIsNull()
    {
        var rom = Rom.Build();
        "  A\u0001B  "u8.CopyTo(rom.AsSpan(0xA0));
        rom[0xA7] = 0;
        rom[0xA8] = (byte)'Z'; // after the NUL: ignored
        Assert.That(GameBoyAdvanceHeaderReader.Read(rom)!.Title, Is.EqualTo("A?B"));

        Assert.That(Fields(Rom with { Title = "FULLTWELVECH" })["title"], Is.EqualTo("FULLTWELVECH"));
        Assert.That(GameBoyAdvanceHeaderReader.Read((Rom with { Title = null }).Build())!.Title, Is.Null);
        Assert.That(Fields(Rom with { Title = "   " })["title"], Is.Null);
    }

    [TestCase("AXVE", "AXVE")]
    [TestCase("B24J", "B24J")]
    [TestCase("axve", null)]
    [TestCase("AX-E", null)]
    [TestCase(null, null)]
    public void GameCode_IsUpperAlphanumericOrNull(string? code, string? expected) =>
        Assert.That(Fields(Rom with { GameCode = code })["gameCode"], Is.EqualTo(expected));

    [TestCase("AXVJ", "Japan")]
    [TestCase("AXVE", "USA")]
    [TestCase("AXVP", "Europe")]
    [TestCase("AXVX", "Europe")]
    [TestCase("AXVD", "Germany")]
    [TestCase("AXVK", "Korea")]
    [TestCase("AXVZ", "Unknown (Z)")]
    [TestCase(null, null)]
    public void Destination_IsTheFourthGameCodeChar(string? code, string? expected) =>
        Assert.That(Fields(Rom with { GameCode = code })["destination"], Is.EqualTo(expected));

    [TestCase("01", "01", "Nintendo Research & Development 1")]
    [TestCase("ZZ", "ZZ", "Unknown (0x5A5A)")]
    [TestCase("z1", null, null)]
    [TestCase(null, null, null)]
    public void Maker_UsesTheNewLicenseeTable(string? code, string? expectedCode, string? expectedMaker)
    {
        var fields = Fields(Rom with { MakerCode = code });

        Assert.That(fields["makerCode"], Is.EqualTo(expectedCode));
        Assert.That(fields["maker"], Is.EqualTo(expectedMaker));
    }

    [Test]
    public void UnitCodeAndDeviceType_AreHex()
    {
        var fields = Fields(Rom with { UnitCode = 0x0A, DeviceType = 0x80 });

        Assert.That(fields["unitCode"], Is.EqualTo("0x0A"));
        Assert.That(fields["deviceType"], Is.EqualTo("0x80"));
    }

    [Test]
    public void FixedValue_IsChecked() =>
        Assert.That(Fields(Rom with { CorruptFixedValue = true })["fixedValueValid"], Is.False);

    [TestCase(false, false, true)]
    [TestCase(true, false, true)]
    [TestCase(false, true, true)]
    [TestCase(true, true, false)]
    public void IsPlausible_IsLogoOrChecksum(bool corruptLogo, bool corruptChecksum, bool expected)
    {
        var header = GameBoyAdvanceHeaderReader.Read((Rom with { CorruptLogo = corruptLogo, CorruptHeaderChecksum = corruptChecksum }).Build())!;

        Assert.That(header.Fields["logoValid"], Is.EqualTo(!corruptLogo));
        Assert.That(header.Fields["headerChecksumValid"], Is.EqualTo(!corruptChecksum));
        Assert.That(header.IsPlausible, Is.EqualTo(expected));
    }

    [Test]
    public void RomSizeBytes_IsTheFileLength() =>
        Assert.That(Fields(Rom with { SizeBytes = 1234567 })["romSizeBytes"], Is.EqualTo(1234567));

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void RandomGarbage_NeverThrows(int seed)
    {
        var rom = new byte[0x200 + seed];
        new Random(seed).NextBytes(rom);

        Assert.DoesNotThrow(() => GameBoyAdvanceHeaderReader.Read(rom));
    }

    [Test]
    public void Logo_IsPinned()
    {
        // B5: guards against an accidental edit; the bytes were cross-checked once against GBATEK/gbafix.
        Assert.That(GameBoyAdvanceHeaderReader.NintendoLogo.Length, Is.EqualTo(156));
        Assert.That(Crc32.ToHex(GameBoyAdvanceHeaderReader.NintendoLogo), Is.EqualTo("d0beb55e"));
        Assert.That(GameBoyAdvanceHeaderReader.NintendoLogo.SequenceEqual(SyntheticGbaRomBuilder.NintendoLogo), Is.True);
    }
}
