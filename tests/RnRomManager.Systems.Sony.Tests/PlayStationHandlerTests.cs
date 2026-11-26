using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Sony.Tests;

/// <summary>Disc systems B4: the PlayStation contract.</summary>
public class PlayStationHandlerTests
{
    private static readonly ISystemHandler Handler = new PlayStationHandler();

    [Test]
    public void BrowserCore_IsPcsxRearmed_ForExternalDiscSets()
    {
        Assert.That(Handler.BrowserCore, Is.EqualTo("pcsx_rearmed"));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets), Is.EqualTo((false, true)));
    }

    [Test]
    public void PlayStation_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("psx", "PlayStation", "PSX", "psx", "Sony - PlayStation")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".bin", ".cue", ".iso", ".chd", ".img", ".pbp", ".ccd", ".sub", ".mds", ".mdf", ".toc", ".cbn" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16L, 1024L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, DatSource.Libretro, DatKind.Redump)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
    }

    [Test]
    public void ReadHeader_IsNull() => HandlerAsserts.AssertNoHeader(Handler, new byte[4096]);

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromThePspOne()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        foreach (var other in new ISystemHandler[] { new PlayStationPortableHandler() })
            Assert.That(png.ToArray(), Is.Not.EqualTo(other.DefaultBoxArtPng.ToArray()), other.Id);
    }
}
