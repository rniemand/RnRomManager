using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari.Tests;

public class Atari5200HandlerTests
{
    private static readonly ISystemHandler Handler = new Atari5200Handler();

    [Test]
    public void Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("a5200", "Atari 5200", "5200", "a5200", "Atari - 5200")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".a52", ".bin" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((4096L, 65536L)));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
        Assert.That(Handler.FileSets, Is.False);
        Assert.That((Handler.DatSource, Handler.DatFolder), Is.EqualTo((DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(Handler.BrowserCore, Is.Null);
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
    }

    [TestCase(0)]
    [TestCase(4096)]
    [TestCase(40960)]
    public void ReadHeader_IsAlwaysNull(int size)
    {
        var rom = new byte[size];
        new Random(size).NextBytes(rom);
        Assert.That(Handler.ReadHeader(rom), Is.Null);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var rom = new byte[4096];
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
