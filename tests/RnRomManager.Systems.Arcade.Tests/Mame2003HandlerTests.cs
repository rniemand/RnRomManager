using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Arcade.Tests;

/// <summary>Arcade S1–S2: the handler contract, its stored extension and its box art.</summary>
public class Mame2003HandlerTests
{
    private static readonly ISystemHandler Handler = new Mame2003Handler();

    [Test]
    public void Mame2003_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("mame2003", "Arcade (MAME 2003-Plus)", "MAME", "mame2003", "MAME")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".mamez", ".zip" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((22L, 1L * 1024 * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource), Is.EqualTo((false, false, DatSource.Mame)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That(Handler.DatFolder, Is.EqualTo(DatKind.NoIntro), "unused: the default");
    }

    [Test]
    public void BrowserCore_IsNullUntilTheOperatorCore() => Assert.That(Handler.BrowserCore, Is.Null);

    [TestCase("sf2.zip", ".mamez")]
    [TestCase("SF2.ZIP", ".mamez")]
    [TestCase("sf2.mamez", ".mamez")]
    [TestCase("sf2.MAMEZ", ".mamez")]
    [TestCase("sf2.7z", null)]
    [TestCase("sf2", null)]
    public void SourceExtension_IsAlwaysMamez(string fileName, string? expected) =>
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
