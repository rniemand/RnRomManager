using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.NeoGeoPocket.Tests;

public class NeoGeoPocketHandlerTests
{
    private static readonly ISystemHandler[] Handlers = [new NeoGeoPocketHandler(), new NeoGeoPocketColorHandler()];

    [Test]
    public void NeoGeoPocket_Contract()
    {
        ISystemHandler ngp = new NeoGeoPocketHandler();

        Assert.That((ngp.Id, ngp.DisplayName, ngp.ShortName, ngp.Family, ngp.DatName),
            Is.EqualTo(("ngp", "Neo Geo Pocket", "NGP", "neogeopocket", "SNK - Neo Geo Pocket")));
        Assert.That(ngp.RomExtensions, Is.EqualTo(new[] { ".ngp" }));
    }

    [Test]
    public void NeoGeoPocketColor_Contract()
    {
        ISystemHandler ngpc = new NeoGeoPocketColorHandler();

        Assert.That((ngpc.Id, ngpc.DisplayName, ngpc.ShortName, ngpc.Family, ngpc.DatName),
            Is.EqualTo(("ngpc", "Neo Geo Pocket Color", "NGPC", "neogeopocket", "SNK - Neo Geo Pocket Color")));
        Assert.That(ngpc.RomExtensions, Is.EqualTo(new[] { ".ngc", ".ngpc", ".npc" }));
    }

    [TestCaseSource(nameof(Handlers))]
    public void SharedContract(ISystemHandler handler)
    {
        Assert.That((handler.MinRomSizeBytes, handler.MaxRomSizeBytes), Is.EqualTo((0x40L, 4L * 1024 * 1024)));
        Assert.That(handler.EmbedRomsInZip, Is.True);
        Assert.That(handler.BrowserCore, Is.Null);
        Assert.That((handler.DatSource, handler.DatFolder), Is.EqualTo((DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
        Assert.That(handler.Family, Is.EqualTo(NeoGeoPocketHandlerBase.FamilyId));
    }

    [TestCaseSource(nameof(Handlers))]
    public void ReadHeader_ClassifiesByTheHeaderNotTheHandler(ISystemHandler handler)
    {
        Assert.That(handler.ReadHeader(new SyntheticNgpRomBuilder { Colour = true }.Build())!.ClassifiedSystemId, Is.EqualTo("ngpc"));
        Assert.That(handler.ReadHeader(new SyntheticNgpRomBuilder().Build())!.ClassifiedSystemId, Is.EqualTo("ngp"));
        Assert.That(handler.ReadHeader(new byte[63]), Is.Null);
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
    public void Builder_UnderHeaderSize_Throws()
    {
        Assert.That(() => new SyntheticNgpRomBuilder { Size = 63 }.Build(), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
