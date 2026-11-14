using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari.Tests;

public class Atari7800HandlerTests
{
    private static readonly ISystemHandler Handler = new Atari7800Handler();

    [Test]
    public void Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("a7800", "Atari 7800", "7800", "a7800", "Atari - 7800")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".a78", ".bin" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((4096L, 1048576L)));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
        Assert.That(Handler.FileSets, Is.False);
        Assert.That((Handler.DatSource, Handler.DatFolder), Is.EqualTo((DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(Handler.BrowserCore, Is.Null);
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
    }

    [Test]
    public void HeaderedRom_IsReadWithoutChangingTheBytesForHashing()
    {
        var rom = new byte[16 * 1024 + 128];
        "ATARI7800"u8.CopyTo(rom.AsSpan(1));

        Assert.That(Handler.ReadHeader(rom)?.ClassifiedSystemId, Is.EqualTo("a7800"));
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
