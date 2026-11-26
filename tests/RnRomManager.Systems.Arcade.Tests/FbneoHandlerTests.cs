using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Arcade.Tests;

/// <summary>Arcade (FinalBurn Neo) S1–S2: the handler contract, its stored extensions and its box art.</summary>
public class FbneoHandlerTests
{
    private static readonly ISystemHandler Handler = new FbneoHandler();

    [Test]
    public void Fbneo_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("fbneo", "Arcade (FinalBurn Neo)", "FBNeo", "fbneo", "FBNeo - Arcade Games")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".fbneoz", ".fbneo7z", ".zip", ".7z" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((22L, 1L * 1024 * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource), Is.EqualTo((false, false, DatSource.Fbneo)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
    }

    [Test]
    public void BrowserCore_IsFbneo() => Assert.That(Handler.BrowserCore, Is.EqualTo("fbneo"));

    [TestCase("sf2.zip", ".fbneoz")]
    [TestCase("SF2.ZIP", ".fbneoz")]
    [TestCase("sf2.fbneoz", ".fbneoz")]
    [TestCase("mslug.7z", ".fbneo7z")]
    [TestCase("mslug.fbneo7z", ".fbneo7z")]
    [TestCase("mslug.rar", null)]
    [TestCase("sf2", null)]
    public void SourceExtension_KeepsZipAnd7zApart(string fileName, string? expected) =>
        Assert.That(Handler.SourceExtension(fileName), Is.EqualTo(expected));

    [Test]
    public void ReadHeader_IsNull() => HandlerAsserts.AssertNoHeader(Handler, new byte[4096]);

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
    }
}
