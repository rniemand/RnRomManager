using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Dos.Tests;

/// <summary>MS-DOS S1–S2: the handler contract, its stored extension and its box art.</summary>
public class DosHandlerTests
{
    private static readonly ISystemHandler Handler = new DosHandler();

    [Test]
    public void Dos_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("dos", "MS-DOS", "DOS", "dos", "DOS")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".dosz", ".zip" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((64L, 4L * 1024 * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource), Is.EqualTo((false, false, DatSource.None)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
    }

    [Test]
    public void BrowserCore_IsDosboxPure() => Assert.That(Handler.BrowserCore, Is.EqualTo("dosbox_pure"));

    [TestCase("Doom (1993).zip", ".dosz")]
    [TestCase("DOOM.ZIP", ".dosz")]
    [TestCase("Doom.dosz", ".dosz")]
    [TestCase("Doom.DOSZ", ".dosz")]
    [TestCase("DOOM.EXE", null)]
    [TestCase("Doom", null)]
    public void SourceExtension_IsAlwaysDosz(string fileName, string? expected) =>
        Assert.That(Handler.SourceExtension(fileName), Is.EqualTo(expected));

    [Test]
    public void ReadHeader_IsNull() => HandlerAsserts.AssertNoHeader(Handler, new byte[4096]);

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
    }
}
