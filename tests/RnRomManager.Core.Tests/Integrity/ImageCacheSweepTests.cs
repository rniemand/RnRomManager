using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Integrity;

/// <summary>C5: what the sweep keeps and removes, and a cover that can't be cached.</summary>
public class ImageCacheSweepTests
{
    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";
    private TempDir _temp = null!;
    private IImageRepository _images = null!;
    private IOptions<PathsOptions> _paths = null!;

    [SetUp]
    public void SetUp()
    {
        _temp = new TempDir();
        _images = Substitute.For<IImageRepository>();
        _paths = Options.Create(new PathsOptions { Roms = _temp.Combine("roms"), ImageCache = _temp.Combine("cache") });
        Directory.CreateDirectory(_paths.Value.Roms);
    }

    [TearDown]
    public void TearDown() => _temp.Dispose();

    private ImageCacheSweep Sweep() => new(_images, new CoverCache(_paths, NullLogger<CoverCache>.Instance), _paths,
        new InfoJsonReader([], Substitute.For<ISystemRegistry>()), TimeProvider.System, NullLogger.Instance);

    private string Cache(params string[] parts) => Path.Combine([_paths.Value.ImageCache, .. parts]);

    private void Write(string path, DateTime? mtime = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        if (mtime is { } at)
            File.SetLastWriteTimeUtc(path, at);
    }

    [Test]
    public void SweepImageCache_MissingRoot_RemovesNothing() =>
        Assert.That(Sweep().SweepImageCache(new Dictionary<Guid, string>()), Is.Zero);

    [Test]
    public void SweepImageCache_KeepsCurrentCoversFreshTmpAndProbe_RemovesTheRest()
    {
        var game = Guid.NewGuid().ToString("D");
        Write(Cache(game, Sha1 + CoverCache.Extension));
        Write(Cache(game, "fresh.tmp"));
        Write(Cache(game, "stale.tmp"), DateTime.UtcNow - IntegrityJob.TmpGrace - TimeSpan.FromMinutes(5));
        Write(Cache(game, "old" + CoverCache.Extension));
        Write(Cache(Guid.NewGuid().ToString("D"), Sha1 + CoverCache.Extension));
        Write(Cache(StorageFolders.ProbeFileName));
        Write(Cache("stray.txt"));

        var removed = Sweep().SweepImageCache(new Dictionary<Guid, string> { [Guid.Parse(game)] = Sha1 });

        Assert.That(removed, Is.EqualTo(4));
        var left = Directory.EnumerateFiles(Cache(), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Cache(), f).Replace('\\', '/'));
        Assert.That(left, Is.EquivalentTo(new[] { $"{game}/{Sha1}{CoverCache.Extension}", $"{game}/fresh.tmp", StorageFolders.ProbeFileName }));
    }

    [Test]
    public async Task RefreshImageCacheAsync_UnreadableZip_CountsAFailure()
    {
        _images.ListCoversAsync(Arg.Any<CancellationToken>()).Returns([new CoverRow(Guid.NewGuid(), "gb/G/Gone.zip", "cover.png", Sha1)]);
        Assert.That(await Sweep().RefreshImageCacheAsync(CancellationToken.None), Is.EqualTo((0, 0, 1)));
    }
}
