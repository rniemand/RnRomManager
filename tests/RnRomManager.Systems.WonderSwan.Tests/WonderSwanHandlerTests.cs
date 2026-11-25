using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.WonderSwan.Tests;

public class WonderSwanHandlerTests
{
    private static readonly ISystemHandler[] Handlers = [new WonderSwanHandler(), new WonderSwanColorHandler()];

    [Test]
    public void WonderSwan_Contract()
    {
        ISystemHandler ws = new WonderSwanHandler();

        Assert.That((ws.Id, ws.DisplayName, ws.ShortName, ws.Family, ws.DatName),
            Is.EqualTo(("ws", "WonderSwan", "WS", "wonderswan", "Bandai - WonderSwan")));
        Assert.That(ws.RomExtensions, Is.EqualTo(new[] { ".ws" }));
    }

    [Test]
    public void WonderSwanColor_Contract()
    {
        ISystemHandler wsc = new WonderSwanColorHandler();

        Assert.That((wsc.Id, wsc.DisplayName, wsc.ShortName, wsc.Family, wsc.DatName),
            Is.EqualTo(("wsc", "WonderSwan Color", "WSC", "wonderswan", "Bandai - WonderSwan Color")));
        Assert.That(wsc.RomExtensions, Is.EqualTo(new[] { ".wsc" }));
    }

    [TestCaseSource(nameof(Handlers))]
    public void SharedContract(ISystemHandler handler)
    {
        Assert.That((handler.MinRomSizeBytes, handler.MaxRomSizeBytes), Is.EqualTo((16L, 16L * 1024 * 1024)));
        Assert.That(handler.EmbedRomsInZip, Is.True);
        Assert.That(handler.BrowserCore, Is.Null);
        Assert.That((handler.DatSource, handler.DatFolder), Is.EqualTo((DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
        Assert.That(handler.KeepsSourceExtension, Is.False);
        Assert.That(handler.Family, Is.EqualTo(WonderSwanHandlerBase.FamilyId));
    }

    [TestCaseSource(nameof(Handlers))]
    public void ReadHeader_ClassifiesByTheFooterNotTheHandler(ISystemHandler handler)
    {
        Assert.That(handler.ReadHeader(new SyntheticWsRomBuilder { Colour = true }.Build())!.ClassifiedSystemId, Is.EqualTo("wsc"));
        Assert.That(handler.ReadHeader(new SyntheticWsRomBuilder().Build())!.ClassifiedSystemId, Is.EqualTo("ws"));
        Assert.That(handler.ReadHeader(new byte[15]), Is.Null);
    }

    [TestCaseSource(nameof(Handlers))]
    public void NormalizeForHashing_IsTheIdentity(ISystemHandler handler)
    {
        ReadOnlyMemory<byte> rom = new byte[] { 1, 2, 3 };

        Assert.That(handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom.ToArray()));
    }

    [TestCaseSource(nameof(Handlers))]
    public void DefaultBoxArt_IsA512x512Png(ISystemHandler handler)
    {
        var png = handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }

    [Test]
    public void DefaultBoxArt_DiffersPerSystem()
    {
        Assert.That(Convert.ToHexString(Handlers[0].DefaultBoxArtPng.Span), Is.Not.EqualTo(Convert.ToHexString(Handlers[1].DefaultBoxArtPng.Span)));
    }

    [Test]
    public void Builder_UnderFooterSize_Throws()
    {
        Assert.That(() => new SyntheticWsRomBuilder { Size = 15 }.Build(), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
