using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>Commodore Amiga: the handler contract and its box art.</summary>
public class AmigaHandlerTests
{
    private static readonly ISystemHandler Handler = new AmigaHandler();

    [Test]
    public void Amiga_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("amiga", "Commodore Amiga", "Amiga", "amiga", "Commodore - Amiga")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".adf" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((256L * 1024, 4L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource, Handler.DatFolder), Is.EqualTo((true, DatSource.Libretro, DatKind.Tosec)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That((Handler.FileSets, Handler.LegacyEmbeddedRoms, Handler.KeepsSourceExtension), Is.EqualTo((false, false, false)));
        Assert.That(Handler.BrowserCore, Is.EqualTo("puae"));
    }

    [Test]
    public void ReadHeader_IsAlwaysNull()
    {
        byte[] disk = [(byte)'D', (byte)'O', (byte)'S', 0, .. new byte[901_116]]; // a DD AmigaDOS boot block
        Assert.That(Handler.ReadHeader(disk), Is.Null);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }
}
