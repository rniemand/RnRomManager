using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Nec.Tests;

/// <summary>TurboGrafx-CD B1: the handler contract, its box art and its Redump catalog.</summary>
public class TurboGrafxCdHandlerTests
{
    private static readonly ISystemHandler Handler = new TurboGrafxCdHandler();

    [Test]
    public void TurboGrafxCd_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("tgcd", "TurboGrafx-CD / PC Engine CD", "TGCD", "tgcd", "NEC - PC Engine CD - TurboGrafx-CD")));
        Assert.That(Handler.DatName, Is.EqualTo(IngestSeeder.TurboGrafxCdDat));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".bin", ".cue", ".chd" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16L, 1024L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, DatSource.Libretro, DatKind.Redump)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That(IngestSeeder.RedumpLayouts[IngestSeeder.TurboGrafxCdDat], Is.EqualTo(FixtureDatLayout.Redump));
    }

    [Test]
    public void TurboGrafxCd_HasItsOwnFamily_ApartFromTurboGrafx16() =>
        Assert.That(Handler.Family, Is.Not.EqualTo(new TurboGrafx16Handler().Family));

    [Test]
    public void BrowserCore_IsBeetlePce_SharedWithTurboGrafx16() =>
        Assert.That(Handler.BrowserCore, Is.EqualTo("mednafen_pce").And.EqualTo(new TurboGrafx16Handler().BrowserCore));

    [TestCase(16)]
    [TestCase(4096)]
    [TestCase(64 * 1024)]
    public void ReadHeader_IsNull_ForAnyBytes(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        "PC Engine CD-ROM"u8.CopyTo(bytes);

        Assert.That(Handler.ReadHeader(bytes), Is.Null);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        // 512 bytes past a multiple of 8 KiB: the size TurboGrafx-16 strips a copier header at, which a disc never has.
        var disc = new byte[8192 + 512];
        new Random(11).NextBytes(disc);

        Assert.That(Handler.NormalizeForHashing(disc).ToArray(), Is.EqualTo(disc));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTurboGrafx16s()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new TurboGrafx16Handler().DefaultBoxArtPng.ToArray()));
    }

    [Test]
    public void DatCache_LoadsARedumpCatalog_AndFindsTheDataTrackBySha1()
    {
        using var dir = new TempDir();
        var track = new SyntheticDiscBuilder { Seed = 21 }.Build();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.TurboGrafxCdDat] = [new FixtureGame("Disc Ninja (Japan)", "Disc Ninja (Japan) (Track 02).bin", track)],
        }, layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        var store = cache.For("tgcd")!;
        var hit = store.BySha1(Convert.ToHexStringLower(SHA1.HashData(track)))!;
        Assert.That((store.DatName, hit.GameName, hit.RomName, hit.SizeBytes),
            Is.EqualTo((IngestSeeder.TurboGrafxCdDat, "Disc Ninja (Japan)", "Disc Ninja (Japan) (Track 02).bin", (long)track.Length)));
        Assert.That(File.Exists(Path.Combine(cache.Directory, DatKind.RelativePath(DatKind.Redump, IngestSeeder.TurboGrafxCdDat))), Is.True);
    }

    [Test]
    public void DatCache_LoadsAnEmptyCatalog()
    {
        using var dir = new TempDir();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>> { [IngestSeeder.TurboGrafxCdDat] = [] },
            layouts: IngestSeeder.RedumpLayouts);

        cache.Load();

        Assert.That(cache.For("tgcd")!.Roms, Is.Empty);
    }

    private static DatCache Cache(TempDir dir) =>
        new(Options.Create(new PathsOptions { Ingest = dir.Combine("ingest"), Roms = dir.Combine("roms"), Cache = dir.Combine("cache") }),
            new SystemRegistry([Handler]), NullLogger<DatCache>.Instance);
}
