using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Dos.Tests;

/// <summary>MS-DOS S1–S2: the handler contract, its stored extension and its box art.</summary>
public class DosHandlerTests
{
    private static readonly ISystemHandler Handler = new DosHandler();

    [Test]
    public void Dos_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("dos", "MS-DOS", "DOS", "dos", "DOS")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".dosz", ".zip" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((64L, 4L * 1024 * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource), Is.EqualTo((false, false, DatSource.None)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
    }

    [Test]
    public void BrowserCore_IsNotSetUntilTheCoreIsVendored() => Assert.That(Handler.BrowserCore, Is.Null);

    [TestCase("Doom (1993).zip", ".dosz")]
    [TestCase("DOOM.ZIP", ".dosz")]
    [TestCase("Doom.dosz", ".dosz")]
    [TestCase("Doom.DOSZ", ".dosz")]
    [TestCase("DOOM.EXE", null)]
    [TestCase("Doom", null)]
    public void SourceExtension_IsAlwaysDosz(string fileName, string? expected) =>
        Assert.That(Handler.SourceExtension(fileName), Is.EqualTo(expected));

    [Test]
    public void ReadHeader_IsNull() => Assert.That(Handler.ReadHeader(new byte[4096]), Is.Null);

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }
}
