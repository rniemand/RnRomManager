using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Nec.Tests;

/// <summary>PC-FX X1: the handler contract, its box art and its Redump catalog.</summary>
public class PcfxHandlerTests
{
    private static readonly ISystemHandler Handler = new PcfxHandler();

    [Test]
    public void Pcfx_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("pcfx", "PC-FX", "PC-FX", "pcfx", "NEC - PC-FX")));
        Assert.That(Handler.DatName, Is.EqualTo(IngestSeeder.PcfxDat));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".bin", ".cue", ".chd", ".ccd", ".img", ".sub", ".mds", ".mdf", ".toc" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16L, 1024L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, DatSource.Libretro, DatKind.Redump)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That(IngestSeeder.RedumpLayouts[IngestSeeder.PcfxDat], Is.EqualTo(FixtureDatLayout.Redump));
    }

    [Test]
    public void Pcfx_HasItsOwnFamily_ApartFromTurboGrafxCd() =>
        Assert.That(Handler.Family, Is.Not.EqualTo(new TurboGrafxCdHandler().Family));

    [Test]
    public void BrowserCore_IsBeetlePcfx() =>
        Assert.That(Handler.BrowserCore, Is.EqualTo("mednafen_pcfx"));

    [TestCase(16)]
    [TestCase(4096)]
    [TestCase(64 * 1024)]
    public void ReadHeader_IsNull_ForAnyBytes(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        "PC-FX:Hu_CD-ROM"u8.CopyTo(bytes);

        HandlerAsserts.AssertNoHeader(Handler, bytes);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var disc = new byte[8192 + 512];
        new Random(11).NextBytes(disc);

        HandlerAsserts.AssertNormalizeIsIdentity(Handler, disc);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTurboGrafxCds()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        Assert.That(png.ToArray(), Is.Not.EqualTo(new TurboGrafxCdHandler().DefaultBoxArtPng.ToArray()));
    }

    [Test]
    public void DatCache_LoadsARedumpCatalog_AndFindsTheDataTrackBySha1()
    {
        using var dir = new TempDir();
        var track = new SyntheticDiscBuilder { Seed = 21 }.Build();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.PcfxDat] = [new FixtureGame("Disc Fox (Japan)", "Disc Fox (Japan) (Track 2).bin", track)],
        }, layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        var store = cache.For("pcfx")!;
        var hit = store.BySha1(Convert.ToHexStringLower(SHA1.HashData(track)))!;
        Assert.That((store.DatName, hit.GameName, hit.RomName, hit.SizeBytes),
            Is.EqualTo((IngestSeeder.PcfxDat, "Disc Fox (Japan)", "Disc Fox (Japan) (Track 2).bin", (long)track.Length)));
        Assert.That(File.Exists(Path.Combine(cache.Directory, DatKind.RelativePath(DatKind.Redump, IngestSeeder.PcfxDat))), Is.True);
    }

    [Test]
    public void DatCache_LoadsAnEmptyCatalog()
    {
        using var dir = new TempDir();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>> { [IngestSeeder.PcfxDat] = [] },
            layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        Assert.That(cache.For("pcfx")!.Roms, Is.Empty);
    }

    private static DatCache Cache(TempDir dir) =>
        new(Options.Create(new PathsOptions { Ingest = dir.Combine("ingest"), Roms = dir.Combine("roms"), Cache = dir.Combine("cache") }),
            new SystemRegistry([Handler]), NullLogger<DatCache>.Instance);
}
