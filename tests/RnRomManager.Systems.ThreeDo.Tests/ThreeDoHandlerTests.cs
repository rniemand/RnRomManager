using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.ThreeDo.Tests;

/// <summary>3DO B1: the handler contract, its box art and its Redump catalog.</summary>
public class ThreeDoHandlerTests
{
    private static readonly ISystemHandler Handler = new ThreeDoHandler();

    [Test]
    public void ThreeDo_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("3do", "3DO", "3DO", "3do", "The 3DO Company - 3DO")));
        Assert.That(Handler.DatName, Is.EqualTo(IngestSeeder.ThreeDoDat));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".bin", ".cue", ".iso", ".chd", ".ccd", ".img", ".sub", ".mds", ".mdf", ".toc" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16L, 1024L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, DatSource.Libretro, DatKind.Redump)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That(IngestSeeder.RedumpLayouts[IngestSeeder.ThreeDoDat], Is.EqualTo(FixtureDatLayout.Redump));
    }

    [Test]
    public void BrowserCore_IsOpera() => Assert.That(Handler.BrowserCore, Is.EqualTo("opera"));

    [TestCase(16)]
    [TestCase(4096)]
    [TestCase(64 * 1024)]
    public void ReadHeader_IsNull_ForAnyBytes(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        "\u0001ZZZZZ\u0001"u8.CopyTo(bytes);

        HandlerAsserts.AssertNoHeader(Handler, bytes);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var disc = new SyntheticDiscBuilder { Seed = 31 }.Build();

        HandlerAsserts.AssertNormalizeIsIdentity(Handler, disc);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
    }

    [Test]
    public void DatCache_LoadsARedumpCatalog_AndFindsTheDataTrackBySha1()
    {
        using var dir = new TempDir();
        var track = new SyntheticDiscBuilder { Seed = 41 }.Build();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.ThreeDoDat] = [new FixtureGame("Opal Orbit (USA)", "Opal Orbit (USA) (Track 1).bin", track)],
        }, layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        var store = cache.For("3do")!;
        var hit = store.BySha1(Convert.ToHexStringLower(SHA1.HashData(track)))!;
        Assert.That((store.DatName, hit.GameName, hit.RomName, hit.SizeBytes),
            Is.EqualTo((IngestSeeder.ThreeDoDat, "Opal Orbit (USA)", "Opal Orbit (USA) (Track 1).bin", (long)track.Length)));
        Assert.That(File.Exists(Path.Combine(cache.Directory, DatKind.RelativePath(DatKind.Redump, IngestSeeder.ThreeDoDat))), Is.True);
    }

    [Test]
    public void DatCache_LoadsAnEmptyCatalog()
    {
        using var dir = new TempDir();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>> { [IngestSeeder.ThreeDoDat] = [] },
            layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        Assert.That(cache.For("3do")!.Roms, Is.Empty);
    }

    private static DatCache Cache(TempDir dir) =>
        new(Options.Create(new PathsOptions { Ingest = dir.Combine("ingest"), Roms = dir.Combine("roms"), Cache = dir.Combine("cache") }),
            new SystemRegistry([Handler]), NullLogger<DatCache>.Instance);
}
