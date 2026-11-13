using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari.Tests;

public class AtariLynxHandlerTests
{
    private static readonly ISystemHandler Handler = new AtariLynxHandler();

    [Test]
    public void Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("lynx", "Atari Lynx", "Lynx", "lynx", "Atari - Lynx")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".lnx", ".lyx" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((1024L, 1048576L)));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
    }

    [Test]
    public void HeaderedRom_IsReadWithoutChangingTheBytesForHashing()
    {
        var rom = new byte[1024];
        "LYNX"u8.CopyTo(rom);

        Assert.That(Handler.ReadHeader(rom)?.ClassifiedSystemId, Is.EqualTo("lynx"));
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
