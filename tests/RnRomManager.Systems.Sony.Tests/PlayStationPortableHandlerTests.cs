using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sony.Tests;

/// <summary>Disc systems B4: the PlayStation Portable contract.</summary>
public class PlayStationPortableHandlerTests
{
    private static readonly ISystemHandler Handler = new PlayStationPortableHandler();

    [Test]
    public void PlayStationPortable_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("psp", "PlayStation Portable", "PSP", "psp", "Sony - PlayStation Portable")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".iso" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16L, 8L * 1024L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, DatSource.Libretro, DatKind.Redump)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
    }

    [Test]
    public void BrowserCore_IsPpsspp() => Assert.That(Handler.BrowserCore, Is.EqualTo("ppsspp"));

    [Test]
    public void ReadHeader_IsNull() => Assert.That(Handler.ReadHeader(new byte[4096]), Is.Null);

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromThePsxOne()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        foreach (var other in new ISystemHandler[] { new PlayStationHandler() })
            Assert.That(png.ToArray(), Is.Not.EqualTo(other.DefaultBoxArtPng.ToArray()), other.Id);
    }
}
