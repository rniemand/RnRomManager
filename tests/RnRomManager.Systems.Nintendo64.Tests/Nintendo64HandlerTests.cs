using System.Buffers.Binary;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.GameBoy;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Nintendo64.Tests;

/// <summary>N64 spec N2, N3 (through the handler) and N6.</summary>
public class Nintendo64HandlerTests
{
    private static readonly ISystemHandler Handler = new Nintendo64Handler();

    [Test]
    public void Nintendo64_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.Family, Handler.DatName), Is.EqualTo(("n64", "Nintendo 64", "n64", "Nintendo - Nintendo 64")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".z64", ".v64", ".n64" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((4096L, 64L * 1024 * 1024)));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheGameBoyOnes()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        ISystemHandler[] others = [new GameBoyHandler(), new GameBoyColorHandler(), new GameBoyAdvanceHandler()];
        Assert.That(others.Select(h => h.DefaultBoxArtPng.ToArray()), Has.None.EqualTo(png.ToArray()));
    }

    [Test]
    public void ReadHeader_ClassifiesABuilderRomAsN64()
    {
        var header = Handler.ReadHeader(new SyntheticN64RomBuilder { Seed = 2, Title = "CLASSIFY" }.Build())!;
        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("n64", "CLASSIFY", true)));
    }

    [TestCase(N64ByteOrder.Z64)]
    [TestCase(N64ByteOrder.V64)]
    [TestCase(N64ByteOrder.N64)]
    public void NormalizeForHashing_GivesTheZ64Bytes(N64ByteOrder order)
    {
        var rom = new SyntheticN64RomBuilder { Seed = 4, Title = "NORMALIZE" };
        Assert.That(Handler.NormalizeForHashing((rom with { ByteOrder = order }).Build()).ToArray(), Is.EqualTo(rom.Build()));
    }
}
