using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.VirtualBoy.Tests;

public class VirtualBoyHandlerTests
{
    private static readonly ISystemHandler Handler = new VirtualBoyHandler();

    [Test]
    public void Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("vb", "Virtual Boy", "VB", "vb", "Nintendo - Virtual Boy")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".vb" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((65536L, 4194304L)));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
        Assert.That(Handler.FileSets, Is.False);
        Assert.That((Handler.DatSource, Handler.DatFolder), Is.EqualTo((DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(Handler.BrowserCore, Is.Null);
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
    }

    [TestCase(0)]
    [TestCase(65536)]
    [TestCase(262144)]
    public void ReadHeader_IsAlwaysNull(int size)
    {
        var rom = new byte[size];
        new Random(size).NextBytes(rom);
        Assert.That(Handler.ReadHeader(rom), Is.Null);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var rom = new byte[65536];
        new Random(7).NextBytes(rom);
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom));
    }

    [Test]
    public void BoxArt_IsA512By512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;
        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }
}
