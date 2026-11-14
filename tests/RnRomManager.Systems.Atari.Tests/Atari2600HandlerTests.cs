using System.Buffers.Binary;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.Snes;

namespace RnRomManager.Systems.Atari.Tests;

/// <summary>Atari 2600 spec A2 and A5.</summary>
public class Atari2600HandlerTests
{
    private static readonly ISystemHandler Handler = new Atari2600Handler();

    [Test]
    public void BrowserCore_IsStella2014_WithEmbeddedRoms()
    {
        Assert.That(Handler.BrowserCore, Is.EqualTo("stella2014"));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
    }

    [Test]
    public void Atari2600_Contract()
    {
        Assert.That(Handler.ShortName, Is.EqualTo("2600"));
        Assert.That((Handler.Id, Handler.DisplayName, Handler.Family, Handler.DatName),
            Is.EqualTo(("a2600", "Atari 2600", "a2600", "Atari - 2600")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".a26", ".bin" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((2048L, 65536L)));
    }

    [TestCase(0)]
    [TestCase(2048)]
    [TestCase(4096)]
    [TestCase(65536)]
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
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheSnesOne()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new SnesHandler().DefaultBoxArtPng.ToArray()));
    }
}
