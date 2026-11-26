using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Atari.Tests;

public class AtariLynxHandlerTests
{
    private static readonly ISystemHandler Handler = new AtariLynxHandler();

    [Test]
    public void Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("lynx", "Atari Lynx", "Lynx", "lynx", "Atari - Lynx")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".lnx", ".lyx", ".bll" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((1024L, 1048576L)));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
        Assert.That(Handler.BrowserCore, Is.EqualTo("handy"));
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
    }

    [Test]
    public void HeaderedRom_IsReadWithoutChangingTheBytesForHashing()
    {
        var rom = new byte[1024];
        "LYNX"u8.CopyTo(rom);

        Assert.That(Handler.ReadHeader(rom)?.ClassifiedSystemId, Is.EqualTo("lynx"));
        HandlerAsserts.AssertNormalizeIsIdentity(Handler, rom);
    }

    [Test]
    public void BoxArt_IsA512By512Png()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
    }
}
