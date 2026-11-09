using System.Buffers.Binary;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.Nes;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Snes.Tests;

/// <summary>SNES spec S2, S3 and S4 (through the handler) and S7.</summary>
public class SnesHandlerTests
{
    private static readonly ISystemHandler Handler = new SnesHandler();

    [Test]
    public void Snes_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.Family, Handler.DatName),
            Is.EqualTo(("snes", "Super Nintendo Entertainment System", "snes", "Nintendo - Super Nintendo Entertainment System")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".sfc", ".smc" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((8192L, 8388608L + 512)));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheNesOne()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new NesHandler().DefaultBoxArtPng.ToArray()));
    }

    [Test]
    public void ReadHeader_ClassifiesABuilderRomAsSnes()
    {
        var header = Handler.ReadHeader(new SyntheticSnesRomBuilder { Seed = 2, Title = "CLASSIFIED" }.Build())!;
        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible, header.Fields["mapping"]),
            Is.EqualTo(("snes", "CLASSIFIED", true, "LoROM")));
    }

    [Test]
    public void NormalizeForHashing_StripsTheCopierHeader()
    {
        var plain = new SyntheticSnesRomBuilder { Seed = 3 };
        var headered = (plain with { CopierHeader = true }).Build();

        Assert.That(Handler.NormalizeForHashing(headered).ToArray(), Is.EqualTo(plain.Build()));
        Assert.That(Handler.NormalizeForHashing(plain.Build()).ToArray(), Is.EqualTo(plain.Build()));
    }
}
