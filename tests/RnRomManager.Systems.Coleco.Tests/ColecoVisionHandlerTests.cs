using System.Buffers.Binary;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.Atari;

namespace RnRomManager.Systems.Coleco.Tests;

/// <summary>ColecoVision spec C2, C3 and C6.</summary>
public class ColecoVisionHandlerTests
{
    private static readonly ISystemHandler Handler = new ColecoVisionHandler();

    [Test]
    public void ColecoVision_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.Family, Handler.DatName),
            Is.EqualTo(("coleco", "ColecoVision", "coleco", "Coleco - ColecoVision")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".col", ".rom", ".bin" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((8192L, 32768L)));
    }

    [TestCase(new byte[] { 0xAA, 0x55, 0x01 }, true, "AA55")]
    [TestCase(new byte[] { 0x55, 0xAA, 0x01 }, true, "55AA")]
    [TestCase(new byte[] { 0x00, 0x00, 0x01 }, false, "0000")]
    [TestCase(new byte[] { 0xAA }, false, "AA")]
    [TestCase(new byte[0], false, "")]
    public void ReadHeader_ChecksTheMagic(byte[] rom, bool plausible, string magic)
    {
        var header = Handler.ReadHeader(rom)!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("coleco", (string?)null, plausible)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?> { ["magic"] = magic }));
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var rom = new byte[8192];
        new Random(7).NextBytes(rom);
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheAtariOne()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new Atari2600Handler().DefaultBoxArtPng.ToArray()));
    }
}
