using System.Text;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Nintendo64.Tests;

/// <summary>N64 spec N4, using <see cref="SyntheticN64RomBuilder"/> ROMs.</summary>
public class Nintendo64HeaderReaderTests
{
    private static readonly SyntheticN64RomBuilder Rom = new()
    {
        Seed = 1, SizeBytes = 4096, Title = "ORBIT KART 64", GameCode = "NOKE", Version = 1,
        Crc1 = 0x1234ABCD, Crc2 = 0x0000BEEF, BootAddress = 0x80000400,
    };

    private static IReadOnlyDictionary<string, object?> Fields(SyntheticN64RomBuilder rom) => Nintendo64HeaderReader.Read(rom.Build())!.Fields;

    [Test]
    public void EveryField_OfAValidCart()
    {
        var header = Nintendo64HeaderReader.Read(Rom.Build())!;

        Assert.That(header.ClassifiedSystemId, Is.EqualTo("n64"));
        Assert.That(header.Title, Is.EqualTo("ORBIT KART 64"));
        Assert.That(header.IsPlausible, Is.True);
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["title"] = "ORBIT KART 64",
            ["gameCode"] = "NOKE",
            ["mediaType"] = "Cartridge",
            ["destination"] = "USA",
            ["version"] = 1,
            ["crc1"] = "0x1234ABCD",
            ["crc2"] = "0x0000BEEF",
            ["bootAddress"] = "0x80000400",
            ["headerValid"] = true,
            ["romSizeBytes"] = 4096,
        }));
        Assert.That(header.Fields.Keys, Is.EqualTo(new[]
        {
            "title", "gameCode", "mediaType", "destination", "version", "crc1", "crc2", "bootAddress", "headerValid", "romSizeBytes",
        }), "N4 order");
    }

    [Test]
    public void UnderTheHeaderSize_IsNull_AtItIsRead()
    {
        Assert.That(Nintendo64HeaderReader.Read(new byte[0x3F]), Is.Null);
        Assert.That(Nintendo64HeaderReader.Read(new byte[0x40]), Is.Not.Null);
    }

    [Test]
    public void Title_StopsAtNul_MarksNonPrintable_AndIsNullWhenEmpty()
    {
        var rom = Rom.Build();
        Encoding.ASCII.GetBytes("AB\u0001C\0IGNORED   ", rom.AsSpan(0x20, 20));
        Assert.That(Nintendo64HeaderReader.Read(rom)!.Title, Is.EqualTo("AB?C"));

        rom.AsSpan(0x20, 20).Fill((byte)' ');
        Assert.That(Nintendo64HeaderReader.Read(rom)!.Title, Is.Null);
        Assert.That(Fields(Rom with { Title = null })["title"], Is.Null);
    }

    [TestCase("nOKE")]
    [TestCase("NO-E")]
    [TestCase(null)]
    public void InvalidGameCode_NullsTheCodeMediaTypeAndDestination(string? code)
    {
        var fields = Fields(Rom with { GameCode = code });
        Assert.That((fields["gameCode"], fields["mediaType"], fields["destination"]), Is.EqualTo(((object?)null, (object?)null, (object?)null)));
    }

    [TestCase("NOKE", "Cartridge", "USA")]
    [TestCase("COKJ", "Cartridge (expandable)", "Japan")]
    [TestCase("DOKP", "64DD disk", "Europe")]
    [TestCase("EOKX", "64DD expansion", "Europe")]
    [TestCase("ZOKU", "Aleck64", "Australia")]
    [TestCase("QOK9", "Unknown (Q)", "Unknown (9)")]
    [TestCase("NOKW", "Cartridge", "Scandinavia")]
    [TestCase("NOKN", "Cartridge", "Canada")]
    public void MediaTypeAndDestination(string code, string mediaType, string destination)
    {
        var fields = Fields(Rom with { GameCode = code });
        Assert.That((fields["mediaType"], fields["destination"]), Is.EqualTo(((object?)mediaType, (object?)destination)));
    }

    [Test]
    public void CorruptMagic_IsImplausible()
    {
        var header = Nintendo64HeaderReader.Read((Rom with { CorruptMagic = true }).Build())!;
        Assert.That((header.IsPlausible, header.Fields["headerValid"]), Is.EqualTo((false, (object?)false)));
    }

    [Test]
    public void JunkStartingWith0x80_IsPlausible_TheAcceptedWeakCheck()
    {
        var junk = new byte[0x40];
        junk[0] = 0x80;
        Assert.That(Nintendo64HeaderReader.Read(junk)!.IsPlausible, Is.True);
    }

    [Test]
    public void RandomGarbage_NeverThrows()
    {
        var random = new Random(64);
        for (var i = 0; i < 200; i++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);
            Assert.DoesNotThrow(() => Nintendo64HeaderReader.Read(bytes));
        }
    }
}
