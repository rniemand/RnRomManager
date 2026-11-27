using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.WonderSwan.Tests;

/// <summary>Pocket Challenge V2 spec P1 and P2.</summary>
public class PocketChallengeV2HandlerTests
{
    private static readonly ISystemHandler Handler = new PocketChallengeV2Handler();

    private static readonly string[] FooterFields =
        ["publisherId", "colour", "gameId", "version", "romSize", "saveType", "vertical", "mapper", "checksum", "checksumValid"];

    [Test]
    public void PocketChallengeV2_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("pc2", "Pocket Challenge V2", "PC2", "pc2", "Benesse - Pocket Challenge V2")));
        Assert.That(Handler.Family, Is.Not.EqualTo(WonderSwanHandlerBase.FamilyId));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".pc2" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16L, 16L * 1024 * 1024)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That(Handler.EmbedRomsInZip, Is.True);
        Assert.That(Handler.BrowserCore, Is.EqualTo("mednafen_wswan"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ReadHeader_ClassifiesAsPc2_WhateverTheColourByte(bool colour)
    {
        var header = Handler.ReadHeader(new SyntheticWsRomBuilder { Colour = colour }.Build())!;

        Assert.That((header.ClassifiedSystemId, header.IsPlausible), Is.EqualTo(("pc2", true)));
        Assert.That(header.Fields.Keys, Is.EqualTo(FooterFields));
    }

    [Test]
    public void ReadHeader_WithoutTheJump_IsImplausible()
    {
        Assert.That(Handler.ReadHeader(new SyntheticWsRomBuilder { Opcode = 0x00 }.Build())!.IsPlausible, Is.False);
    }

    [Test]
    public void ReadHeader_UnderTheFooterSize_IsNull()
    {
        HandlerAsserts.AssertNoHeader(Handler, new byte[15]);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheWonderSwanOnes()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        var png = Convert.ToHexString(Handler.DefaultBoxArtPng.Span);
        Assert.That(png, Is.Not.EqualTo(Convert.ToHexString(new WonderSwanHandler().DefaultBoxArtPng.Span)));
        Assert.That(png, Is.Not.EqualTo(Convert.ToHexString(new WonderSwanColorHandler().DefaultBoxArtPng.Span)));
    }
}
