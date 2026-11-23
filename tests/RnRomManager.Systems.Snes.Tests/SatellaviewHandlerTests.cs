using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Snes.Tests;

/// <summary>Satellaview: the handler contract (spec B1), no header check (B2) and the box art (B5).</summary>
public class SatellaviewHandlerTests
{
    private static readonly ISystemHandler Handler = new SatellaviewHandler();

    [Test]
    public void Satellaview_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("satellaview", "Satellaview", "BS-X", "satellaview", "Nintendo - Satellaview")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".bs" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((131_072L, 1_048_576L)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource, Handler.DatFolder), Is.EqualTo((true, DatSource.Libretro, DatKind.NoIntro)));
        Assert.That((Handler.FileSets, Handler.KeepsSourceExtension), Is.EqualTo((false, false)));
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
        Assert.That(Handler.BrowserCore, Is.EqualTo("snes9x"));
    }

    [Test]
    public void ReadHeader_IsAlwaysNull()
    {
        var pack = new byte[128 * 1024];
        new Random(3).NextBytes(pack);

        Assert.That(Handler.ReadHeader(pack), Is.Null);
        Assert.That(Handler.ReadHeader([]), Is.Null);
    }

    [Test]
    public void NormalizeForHashing_KeepsTheBytes()
    {
        byte[] pack = [1, 2, 3];
        Assert.That(Handler.NormalizeForHashing(pack).ToArray(), Is.EqualTo(pack));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_OfItsOwn()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new SnesHandler().DefaultBoxArtPng.ToArray()));
    }
}
