using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Sega.Tests;

/// <summary>Disc systems B4: the Saturn contract.</summary>
public class SaturnHandlerTests
{
    private static readonly ISystemHandler Handler = new SaturnHandler();

    [Test]
    public void Saturn_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("saturn", "Saturn", "Saturn", "saturn", "Sega - Saturn")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".bin", ".cue", ".nrg", ".chd", ".iso", ".ccd", ".img", ".sub", ".mds", ".mdf", ".toc" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16L, 1024L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, DatSource.Libretro, DatKind.Redump)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
    }

    [Test]
    public void BrowserCore_IsYabause_WithExternalDiscs()
    {
        Assert.That(Handler.BrowserCore, Is.EqualTo("yabause"));
        Assert.That(Handler.EmbedRomsInZip, Is.False);
    }

    [Test]
    public void ReadHeader_IsNull() => HandlerAsserts.AssertNoHeader(Handler, new byte[4096]);

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheOtherSegaOnes()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        foreach (var other in new ISystemHandler[] { new GameGearHandler(), new MasterSystemHandler(), new MegaDriveHandler(), new Sega32xHandler() })
            Assert.That(png.ToArray(), Is.Not.EqualTo(other.DefaultBoxArtPng.ToArray()), other.Id);
    }
}
