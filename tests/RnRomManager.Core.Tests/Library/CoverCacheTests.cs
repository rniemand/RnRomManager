using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using SkiaSharp;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Library;

/// <summary>C2: resizing, encoding, caching and the generation limit.</summary>
public class CoverCacheTests
{
    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";
    private TempDir _temp = null!;
    private string _root = null!;
    private readonly Guid _game = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _temp = new TempDir();
        _root = _temp.Path;
    }

    [TearDown]
    public void TearDown() => _temp.Dispose();

    private CoverCache Cache(string? imageCache = null) =>
        new(Options.Create(new PathsOptions { ImageCache = imageCache ?? Path.Combine(_root, "imagecache") }), NullLogger<CoverCache>.Instance);

    private static MemoryStream Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Coral);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new MemoryStream(data.ToArray());
    }

    private static (int Width, int Height, SKEncodedImageFormat Format) Describe(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        return (codec.Info.Width, codec.Info.Height, codec.EncodedFormat);
    }

    [TestCase(1000, 600, 512, 307)]
    [TestCase(600, 1000, 307, 512)]
    [TestCase(200, 100, 200, 100)]
    public async Task Generate_ScalesTheLongestSideDownTo512_NeverUp_AsWebp(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
    {
        var cache = Cache();

        var result = await cache.GenerateAsync(_game, Sha1, Png(width, height), default);

        Assert.That(result.Stored, Is.True);
        Assert.That(Describe(result.Webp!), Is.EqualTo((expectedWidth, expectedHeight, SKEncodedImageFormat.Webp)));
        Assert.That(cache.CachedPath(_game, Sha1), Is.EqualTo(Path.Combine(_root, "imagecache", _game.ToString("D"), Sha1 + ".webp")));
        Assert.That(File.ReadAllBytes(cache.CachedPath(_game, Sha1)!), Is.EqualTo(result.Webp));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(cache.CachedPath(_game, Sha1))!, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task Generate_SideOver4096_OrCorruptBytes_GivesNull_CachesNothing()
    {
        var cache = Cache();

        var huge = await cache.GenerateAsync(_game, Sha1, Png(4097, 1), default);
        var corrupt = await cache.GenerateAsync(_game, Sha1, new MemoryStream([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]), default);

        Assert.That((huge, corrupt), Is.EqualTo((new CoverCacheResult(null, false), new CoverCacheResult(null, false))));
        Assert.That(cache.CachedPath(_game, Sha1), Is.Null);
    }

    [Test]
    public async Task Generate_UnwritableFolder_ServesTheBytes_StoredFalse()
    {
        var file = Path.Combine(_root, "not-a-folder");
        await File.WriteAllTextAsync(file, "x");

        var result = await Cache(file).GenerateAsync(_game, Sha1, Png(10, 10), default);

        Assert.That((result.Webp is not null, result.Stored), Is.EqualTo((true, false)));
    }

    [Test]
    public async Task Generate_RunsAtMostTwoAtOnce()
    {
        var cache = Cache();
        var gate = new TaskCompletionSource();
        var streams = Enumerable.Range(0, 3).Select(_ => new GatedStream(Png(4, 4).ToArray(), gate.Task)).ToArray();

        var runs = streams.Select((s, i) => cache.GenerateAsync(_game, $"{Sha1[..^1]}{i}", s, default)).ToArray();
        await Task.Delay(200);
        var startedBeforeRelease = streams.Count(s => s.Started);
        gate.SetResult();
        await Task.WhenAll(runs);

        Assert.That(startedBeforeRelease, Is.EqualTo(CoverCache.MaxConcurrent));
        Assert.That(runs.All(r => r.Result.Stored), Is.True);
    }

    [Test]
    public void Generate_WaitingForASlot_IsCancelledByTheToken()
    {
        var cache = Cache();
        var gate = new TaskCompletionSource();
        var held = Enumerable.Range(0, 2).Select(i => cache.GenerateAsync(_game, $"{Sha1[..^1]}{i}", new GatedStream([1], gate.Task), default)).ToArray();
        using var cts = new CancellationTokenSource();

        var waiting = cache.GenerateAsync(_game, Sha1, Png(4, 4), cts.Token);
        cts.Cancel();

        Assert.That(async () => await waiting, Throws.InstanceOf<OperationCanceledException>());
        gate.SetResult();
        Assert.That(async () => await Task.WhenAll(held), Throws.Nothing);
    }
}
