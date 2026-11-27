using System.Text;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Sega.Tests;

/// <summary>SG-1000 / SC-3000 spec S1 and S6.</summary>
public class Sg1000HandlerTests
{
    private static readonly ISystemHandler Handler = new Sg1000Handler();

    [Test]
    public void Sg1000_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("sg1000", "SG-1000 / SC-3000", "SG-1000", "sg1000", "Sega - SG-1000")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".sg", ".sc" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((2048L, 1048576L)));
        Assert.That((Handler.EmbedRomsInZip, Handler.KeepsSourceExtension, Handler.BrowserCore), Is.EqualTo((true, true, (string?)null)));
        Assert.That((Handler.DatSource, Handler.DatFolder), Is.EqualTo((DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
    }

    [TestCase(".sg")]
    [TestCase(".sc")]
    public void KeepsExtension_IsTrue(string extension) => Assert.That(Handler.KeepsExtension(extension), Is.True);

    [Test]
    public void ReadHeader_IsAlwaysNull()
    {
        var random = new byte[16384];
        new Random(3).NextBytes(random);
        var tmr = new byte[32768];
        Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(tmr, 0x7FF0);

        HandlerAsserts.AssertNoHeader(Handler, random);
        HandlerAsserts.AssertNoHeader(Handler, tmr);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var rom = new byte[8192];
        new Random(7).NextBytes(rom);
        HandlerAsserts.AssertNormalizeIsIdentity(Handler, rom);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheMasterSystemOne()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        Assert.That(Handler.DefaultBoxArtPng.ToArray(), Is.Not.EqualTo(new MasterSystemHandler().DefaultBoxArtPng.ToArray()));
    }
}
