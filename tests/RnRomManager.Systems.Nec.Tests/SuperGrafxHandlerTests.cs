using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Nec.Tests;

/// <summary>PC Engine SuperGrafx spec G1 and G2.</summary>
public class SuperGrafxHandlerTests
{
    private static readonly ISystemHandler Handler = new SuperGrafxHandler();

    [Test]
    public void SuperGrafx_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("sgx", "PC Engine SuperGrafx", "SGX", "sgx", "NEC - PC Engine SuperGrafx")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".sgx" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((524288L, 1049088L)));
        Assert.That(Handler.BrowserCore, Is.EqualTo("mednafen_pce"));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
    }

    [TestCase(0)]
    [TestCase(524288)]
    [TestCase(524800)]
    public void ReadHeader_IsAlwaysNull(int size)
    {
        var rom = new byte[size];
        new Random(size).NextBytes(rom);
        HandlerAsserts.AssertNoHeader(Handler, rom);
    }

    [Test]
    public void NormalizeForHashing_StripsA512ByteCopierHeader()
    {
        var rom = new byte[524288 + 512];
        new Random(7).NextBytes(rom);
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom[512..]));
    }

    [Test]
    public void NormalizeForHashing_LeavesAnUnheaderedDumpUnchanged()
    {
        var rom = new byte[524288];
        new Random(7).NextBytes(rom);
        HandlerAsserts.AssertNormalizeIsIdentity(Handler, rom);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheTurboGrafx16One()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        Assert.That(Handler.DefaultBoxArtPng.ToArray(), Is.Not.EqualTo(new TurboGrafx16Handler().DefaultBoxArtPng.ToArray()));
    }
}
