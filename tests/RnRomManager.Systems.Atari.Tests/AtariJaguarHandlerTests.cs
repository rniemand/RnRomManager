using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Atari.Tests;

public class AtariJaguarHandlerTests
{
    private static readonly ISystemHandler Handler = new AtariJaguarHandler();

    [Test]
    public void Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("jaguar", "Atari Jaguar", "Jaguar", "jaguar", "Atari - Jaguar")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".j64", ".jag", ".rom", ".abs", ".cof" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16384L, 16777216L)));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
        Assert.That(Handler.BrowserCore, Is.EqualTo("virtualjaguar"));
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
    }

    [Test]
    public void ReadHeader_AlwaysReturnsNull()
    {
        var rom = new byte[16384];
        "LYNX"u8.CopyTo(rom);
        HandlerAsserts.AssertNoHeader(Handler, rom);
    }

    [Test]
    public void BoxArt_IsA512By512Png()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
    }
}
