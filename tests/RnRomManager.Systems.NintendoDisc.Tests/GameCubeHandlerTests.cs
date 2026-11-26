using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.NintendoDisc.Tests;

/// <summary>Disc systems B4: the GameCube contract.</summary>
public class GameCubeHandlerTests
{
    private static readonly ISystemHandler Handler = new GameCubeHandler();

    [Test]
    public void GameCube_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("gc", "GameCube", "GC", "nintendo-disc", "Nintendo - GameCube")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".iso", ".gcm" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((32768L, 9L * 1024L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, DatSource.Libretro, DatKind.Redump)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheWiiOne()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        foreach (var other in new ISystemHandler[] { new WiiHandler() })
            Assert.That(png.ToArray(), Is.Not.EqualTo(other.DefaultBoxArtPng.ToArray()), other.Id);
    }
}
