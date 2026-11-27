using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>CD32 A1: the Amiga CD32 handler contract, its box art and its Redump catalog.</summary>
public class Cd32HandlerTests
{
    private static readonly ISystemHandler Handler = new Cd32Handler();

    [Test]
    public void Cd32_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("cd32", "Commodore Amiga CD32", "CD32", "cd32", "Commodore - CD32")));
        Assert.That(Handler.DatName, Is.EqualTo(IngestSeeder.Cd32Dat));
        Assert.That(Handler.RomExtensions,
            Is.EqualTo(new[] { ".bin", ".cue", ".nrg", ".chd", ".iso", ".ccd", ".img", ".sub", ".mds", ".mdf", ".toc" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16L, 1024L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, DatSource.Libretro, DatKind.Redump)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That(IngestSeeder.RedumpLayouts[IngestSeeder.Cd32Dat], Is.EqualTo(FixtureDatLayout.Redump));
    }

    [Test]
    public void Cd32_HasItsOwnFamily_ApartFromCdtvAndAmiga() =>
        Assert.That(Handler.Family, Is.Not.AnyOf(new CdtvHandler().Family, new AmigaHandler().Family));

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
        "CD32"u8.CopyTo(bytes);

        HandlerAsserts.AssertNoHeader(Handler, bytes);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var disc = new byte[8192 + 512];
        new Random(32).NextBytes(disc);

        HandlerAsserts.AssertNormalizeIsIdentity(Handler, disc);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromAmigasAndCdtvs()
    {
        var png = Handler.DefaultBoxArtPng.ToArray();

        HandlerAsserts.AssertBoxArtPng(Handler, 512);
        Assert.That(png, Is.Not.EqualTo(new AmigaHandler().DefaultBoxArtPng.ToArray()));
        Assert.That(png, Is.Not.EqualTo(new CdtvHandler().DefaultBoxArtPng.ToArray()));
    }

    [Test]
    public void DatCache_LoadsARedumpCatalog_AndFindsTheDataTrackBySha1()
    {
        using var dir = new TempDir();
        var track = new SyntheticDiscBuilder { Seed = 32 }.Build();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.Cd32Dat] = [new FixtureGame("Disc Chick (Europe)", "Disc Chick (Europe) (Track 01).bin", track)],
        }, layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        var store = cache.For("cd32")!;
        var hit = store.BySha1(Convert.ToHexStringLower(SHA1.HashData(track)))!;
        Assert.That((store.DatName, hit.GameName, hit.RomName, hit.SizeBytes),
            Is.EqualTo((IngestSeeder.Cd32Dat, "Disc Chick (Europe)", "Disc Chick (Europe) (Track 01).bin", (long)track.Length)));
        Assert.That(File.Exists(Path.Combine(cache.Directory, DatKind.RelativePath(DatKind.Redump, IngestSeeder.Cd32Dat))), Is.True);
    }

    [Test]
    public void DatCache_LoadsAnEmptyCatalog()
    {
        using var dir = new TempDir();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>> { [IngestSeeder.Cd32Dat] = [] },
            layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        Assert.That(cache.For("cd32")!.Roms, Is.Empty);
    }

    private static DatCache Cache(TempDir dir) =>
        new(Options.Create(new PathsOptions { Ingest = dir.Combine("ingest"), Roms = dir.Combine("roms"), Cache = dir.Combine("cache") }),
            new SystemRegistry([Handler]), NullLogger<DatCache>.Instance);
}
