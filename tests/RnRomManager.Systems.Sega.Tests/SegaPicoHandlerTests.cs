using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Sega.Tests;

/// <summary>Sega Pico P1, P2 and box art.</summary>
public class SegaPicoHandlerTests
{
    private static readonly ISystemHandler Handler = new SegaPicoHandler();
    private static readonly SyntheticMegaDriveRomBuilder Builder = new() { Seed = 1971, Size = 524288, SystemType = "SEGA PICO" };

    [Test]
    public void SegaPico_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName, Handler.DatFolder),
            Is.EqualTo(("pico", "Sega Pico", "Pico", "pico", "Sega - PICO", "no-intro")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".md", ".bin", ".pco" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((8192L, 4194304L)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That(Handler.KeepsSourceExtension, Is.False);
    }

    [Test]
    public void BrowserCore_IsPicodrive_WithEmbeddedRoms()
    {
        Assert.That(Handler.BrowserCore, Is.EqualTo("picodrive"));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
    }

    [Test]
    public void ReadHeader_ReadsTheMegaDriveFields_ClassifiedPico()
    {
        var header = Handler.ReadHeader(Builder.Build())!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("pico", "TURBO COMET", true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["systemType"] = "SEGA PICO", ["copyright"] = "(C)SEGA 1991.JAN", ["domesticTitle"] = "TURBO KOMETTO",
            ["overseasTitle"] = "TURBO COMET", ["serial"] = "GM 00001234-00", ["checksum"] = "ABCD", ["region"] = "JUE",
        }));
    }

    [Test]
    public void ReadHeader_MegaDriveSystemType_IsStillClassifiedPico() =>
        Assert.That(Handler.ReadHeader((Builder with { SystemType = "SEGA GENESIS" }).Build())!.ClassifiedSystemId, Is.EqualTo("pico"));

    [Test]
    public void ReadHeader_NoSegaOrTooShort_IsNull()
    {
        Assert.That(Handler.ReadHeader((Builder with { SystemType = "PICO" }).Build()), Is.Null);
        Assert.That(Handler.ReadHeader(Builder.Build().AsSpan(0, 0x1FF)), Is.Null);
        HandlerAsserts.AssertNoHeader(Handler, []);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var rom = Builder.Build();
        HandlerAsserts.AssertNormalizeIsIdentity(Handler, rom);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromMegaDrives()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        Assert.That(png.ToArray(), Is.Not.EqualTo(new MegaDriveHandler().DefaultBoxArtPng.ToArray()));
    }
}
