using System.Buffers.Binary;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Sega.Tests;

/// <summary>Mega Drive spec D1, D2 and U2.</summary>
public class MegaDriveHandlerTests
{
    private static readonly ISystemHandler Handler = new MegaDriveHandler();
    private static readonly SyntheticMegaDriveRomBuilder Builder = new() { Seed = 1601 };

    [Test]
    public void MegaDrive_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("md", "Mega Drive / Genesis", "MD", "megadrive", "Sega - Mega Drive - Genesis")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".md", ".bin", ".gen" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((8192L, 16777216L)));
    }

    [Test]
    public void BrowserCore_IsGenesisPlusGx_WithEmbeddedRoms()
    {
        Assert.That(Handler.BrowserCore, Is.EqualTo("genesis_plus_gx"));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
    }

    [Test]
    public void ReadHeader_ReadsEveryField()
    {
        var header = Handler.ReadHeader(Builder.Build())!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("md", "TURBO COMET", true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["systemType"] = "SEGA GENESIS", ["copyright"] = "(C)SEGA 1991.JAN", ["domesticTitle"] = "TURBO KOMETTO",
            ["overseasTitle"] = "TURBO COMET", ["serial"] = "GM 00001234-00", ["checksum"] = "ABCD", ["region"] = "JUE",
        }));
    }

    [TestCase("SEGA MEGA DRIVE")]
    [TestCase(" SEGA GENESIS")]
    [TestCase("\0SEGA GENESIS")]
    public void ReadHeader_SystemTypeVariants_ArePlausible(string systemType) =>
        Assert.That(Handler.ReadHeader((Builder with { SystemType = systemType }).Build())!.IsPlausible, Is.True);

    [Test]
    public void ReadHeader_BlankOverseasTitle_UsesTheDomesticOne()
    {
        var header = Handler.ReadHeader((Builder with { OverseasTitle = "" }).Build())!;
        Assert.That((header.Title, header.Fields["overseasTitle"]), Is.EqualTo(("TURBO KOMETTO", (object?)null)));
    }

    [Test]
    public void ReadHeader_ControlAndHighBytes_BecomeSingleSpaces() =>
        Assert.That(Handler.ReadHeader((Builder with { OverseasTitle = "TURBO\u0001ÿ\tCOMET\0" }).Build())!.Title,
            Is.EqualTo("TURBO COMET"));

    [Test]
    public void ReadHeader_AllBlankTitles_AreNull()
    {
        var header = Handler.ReadHeader((Builder with { OverseasTitle = "", DomesticTitle = "\0\0" }).Build())!;
        Assert.That((header.Title, header.Fields["domesticTitle"]), Is.EqualTo(((string?)null, (object?)null)));
    }

    [Test]
    public void ReadHeader_NoSegaOrTooShort_IsNull()
    {
        Assert.That(Handler.ReadHeader((Builder with { SystemType = "GENESIS" }).Build()), Is.Null);
        Assert.That(Handler.ReadHeader(Builder.Build().AsSpan(0, 0x1FF)), Is.Null);
        Assert.That(Handler.ReadHeader(ReadOnlySpan<byte>.Empty), Is.Null);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var rom = Builder.Build();
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheOtherSegaOnes()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new GameGearHandler().DefaultBoxArtPng.ToArray()));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new MasterSystemHandler().DefaultBoxArtPng.ToArray()));
    }
}
