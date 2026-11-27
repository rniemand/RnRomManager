using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>CD32 A1: the Commodore CDTV handler contract, its box art and its Redump catalog.</summary>
public class CdtvHandlerTests
{
    private static readonly ISystemHandler Handler = new CdtvHandler();

    [Test]
    public void Cdtv_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("cdtv", "Commodore CDTV", "CDTV", "cdtv", "Commodore - CDTV")));
        Assert.That(Handler.DatName, Is.EqualTo(IngestSeeder.CdtvDat));
        Assert.That(Handler.RomExtensions,
            Is.EqualTo(new[] { ".bin", ".cue", ".nrg", ".chd", ".iso", ".ccd", ".img", ".sub", ".mds", ".mdf", ".toc" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16L, 1024L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, DatSource.Libretro, DatKind.Redump)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That(IngestSeeder.RedumpLayouts[IngestSeeder.CdtvDat], Is.EqualTo(FixtureDatLayout.Redump));
    }

    [Test]
    public void Cdtv_HasItsOwnFamily_ApartFromCd32AndAmiga() =>
        Assert.That(Handler.Family, Is.Not.AnyOf(new Cd32Handler().Family, new AmigaHandler().Family));

    [Test]
    public void BrowserCore_IsNull_UntilPlayIsEnabled() =>
        Assert.That(Handler.BrowserCore, Is.Null);

    [TestCase(16)]
    [TestCase(4096)]
    [TestCase(64 * 1024)]
    public void ReadHeader_IsNull_ForAnyBytes(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        "CDTV"u8.CopyTo(bytes);

        HandlerAsserts.AssertNoHeader(Handler, bytes);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var disc = new byte[8192 + 512];
        new Random(33).NextBytes(disc);

        HandlerAsserts.AssertNormalizeIsIdentity(Handler, disc);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromAmigasAndCd32s()
    {
        var png = Handler.DefaultBoxArtPng.ToArray();

        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        Assert.That(png, Is.Not.EqualTo(new AmigaHandler().DefaultBoxArtPng.ToArray()));
        Assert.That(png, Is.Not.EqualTo(new Cd32Handler().DefaultBoxArtPng.ToArray()));
    }

    [Test]
    public void DatCache_LoadsARedumpCatalog_AndFindsTheDataTrackBySha1()
    {
        using var dir = new TempDir();
        var track = new SyntheticDiscBuilder { Seed = 34 }.Build();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.CdtvDat] = [new FixtureGame("Disc Owl (Europe)", "Disc Owl (Europe) (Track 01).bin", track)],
        }, layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        var store = cache.For("cdtv")!;
        var hit = store.BySha1(Convert.ToHexStringLower(SHA1.HashData(track)))!;
        Assert.That((store.DatName, hit.GameName, hit.RomName, hit.SizeBytes),
            Is.EqualTo((IngestSeeder.CdtvDat, "Disc Owl (Europe)", "Disc Owl (Europe) (Track 01).bin", (long)track.Length)));
        Assert.That(File.Exists(Path.Combine(cache.Directory, DatKind.RelativePath(DatKind.Redump, IngestSeeder.CdtvDat))), Is.True);
    }

    [Test]
    public void DatCache_LoadsAnEmptyCatalog()
    {
        using var dir = new TempDir();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>> { [IngestSeeder.CdtvDat] = [] },
            layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        Assert.That(cache.For("cdtv")!.Roms, Is.Empty);
    }

    private static DatCache Cache(TempDir dir) =>
        new(Options.Create(new PathsOptions { Ingest = dir.Combine("ingest"), Roms = dir.Combine("roms"), Cache = dir.Combine("cache") }),
            new SystemRegistry([Handler]), NullLogger<DatCache>.Instance);
}
