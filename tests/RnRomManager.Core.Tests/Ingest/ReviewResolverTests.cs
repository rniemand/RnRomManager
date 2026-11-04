using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

public class ReviewResolverTests
{
    private TempDir _dir = null!;
    private IIngestReviewRepository _reviews = null!;
    private JobLock _lock = null!;
    private ReviewResolver _resolver = null!;
    private ReviewItem _item = null!;
    private FakeTimeProvider _time = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var paths = new PathsOptions { Ingest = _dir.Combine("ingest"), Roms = _dir.Combine("roms"), Cache = _dir.Combine("cache") };
        Directory.CreateDirectory(paths.Ingest);
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        _lock = new JobLock();
        _reviews = Substitute.For<IIngestReviewRepository>();
        _item = new ReviewItem { Id = 1, SystemId = "gb", Status = "Pending", SourcePath = Path.Combine(paths.Ingest, "test.gb"),
            OriginalFileName = "test.gb", Sha1 = new string('0', 40), SizeBytes = 3 };
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]);
        _reviews.GetAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _item);
        var systems = new SystemRegistry([new Headerless()]);
        var options = Options.Create(paths);
        var dats = new DatCache(options, systems, NullLogger<DatCache>.Instance);
        var games = Substitute.For<IGameRepository>();
        var reader = new InfoJsonReader([], systems);
        var media = new MediaService(new HttpClient(), Options.Create(new SourcesOptions { ThumbnailBaseUrl = "" }), NullLogger<MediaService>.Instance);
        var pipeline = new IngestPipeline(dats, systems, Substitute.For<IRomRepository>(), games, Substitute.For<ILibraryMirror>(),
            Substitute.For<IIntegrityIssueRepository>(), _reviews, reader, media, options, _time, NullLogger<IngestPipeline>.Instance);
        _resolver = new ReviewResolver(_lock, _reviews, games, pipeline, dats, systems,
            new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance), options, _time, NullLogger<ReviewResolver>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    [Test]
    public async Task BusyLock_RefusesWithoutReadingOrChangingReview()
    {
        using var held = _lock.TryAcquire();
        Assert.That((await _resolver.DismissAsync(1, 42, default)).Code, Is.EqualTo("JobRunning"));
        await _reviews.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
    }

    [TestCase("Resolved")]
    [TestCase("Dismissed")]
    public async Task CompletedItems_CannotBeActedOnAgain(string status)
    {
        _item = _item with { Status = status };
        Assert.That((await _resolver.DismissAsync(1, 42, default)).Code, Is.EqualTo("NotFound"));
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
    }

    [Test]
    public async Task MissingSource_DeletesStaleRowAndReleasesLock()
    {
        File.Delete(_item.SourcePath);
        Assert.That((await _resolver.DismissAsync(1, 42, default)).Code, Is.EqualTo("SourceMissing"));
        await _reviews.Received().DeleteAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.Single() == 1), default);
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
    }

    [Test]
    public async Task DismissLooseFile_MovesIt_UsesInjectedTimeAndAdmin()
    {
        Assert.That((await _resolver.DismissAsync(1, 42, default)).Code, Is.Null);
        var moved = _dir.Combine("ingest", "_failed", "test.gb");
        Assert.That(File.Exists(_item.SourcePath), Is.False);
        Assert.That(File.ReadAllText(moved + ".error.txt"), Does.Contain("Dismissed").And.Contain("2026-11-04T12:00:00"));
        await _reviews.Received().SetResolutionAsync(1, "Dismissed", null, 42, _time.GetUtcNow().UtcDateTime, default);
    }

    [Test]
    public async Task DismissArchive_DoesNotMoveOrExtractArchive()
    {
        _item = _item with { ArchivePath = _item.SourcePath, ArchiveEntry = "test.gb" };
        Assert.That((await _resolver.DismissAsync(1, 42, default)).Code, Is.Null);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        Assert.That(Directory.Exists(_dir.Combine("cache", "work")), Is.False);
        await _reviews.Received().SetResolutionAsync(1, "Dismissed", null, 42, _time.GetUtcNow().UtcDateTime, default);
    }

    [Test]
    public async Task ChangedBytes_ReturnVerifyFailed_WithoutCompletingReview()
    {
        var result = await _resolver.ResolveAsync(1, new ReviewResolutionRequest { Action = "Manual", Title = "My Game", Regions = [], Languages = [], Flags = [] }, 42, default);
        Assert.That(result.Code, Is.EqualTo("VerifyFailed"));
        await _reviews.DidNotReceiveWithAnyArgs().SetResolutionAsync(default, default!, default, default, default, default);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
    }

    private sealed class Headerless : ISystemHandler
    {
        public string Id => "gb";
        public string DisplayName => "Test";
        public string Family => "gameboy";
        public IReadOnlyList<string> RomExtensions => [".gb"];
        public long MinRomSizeBytes => 1;
        public long MaxRomSizeBytes => 1024;
        public string DatName => "GB";
        public ReadOnlyMemory<byte> DefaultBoxArtPng => ReadOnlyMemory<byte>.Empty;
        public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
    }
}
