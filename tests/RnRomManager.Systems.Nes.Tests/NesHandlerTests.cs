using System.Buffers.Binary;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.GameBoy;
using RnRomManager.Systems.Nintendo64;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Nes.Tests;

/// <summary>NES spec N2, N4 (through the handler) and N7.</summary>
public class NesHandlerTests
{
    private static readonly ISystemHandler Handler = new NesHandler();

    [Test]
    public void Nes_Contract()
    {
        Assert.That(Handler.ShortName, Is.EqualTo("NES"));
        Assert.That((Handler.Id, Handler.DisplayName, Handler.Family, Handler.DatName),
            Is.EqualTo(("nes", "Nintendo Entertainment System", "nes", "Nintendo - Nintendo Entertainment System")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".nes" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((4112L, 16L + 512 + 64L * 1024 * 1024)));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheOthers()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        ISystemHandler[] others = [new GameBoyHandler(), new GameBoyColorHandler(), new GameBoyAdvanceHandler(), new Nintendo64Handler()];
        Assert.That(others.Select(h => h.DefaultBoxArtPng.ToArray()), Has.None.EqualTo(png.ToArray()));
    }

    [Test]
    public void ReadHeader_ClassifiesABuilderRomAsNes()
    {
        var header = Handler.ReadHeader(new SyntheticNesRomBuilder { Seed = 2, Mapper = 4 }.Build())!;
        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible, header.Fields["mapper"]), Is.EqualTo(("nes", (string?)null, true, 4)));
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheBytesUnchanged()
    {
        var rom = new SyntheticNesRomBuilder { Seed = 3 }.Build();
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom));
    }
}
