using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Spec §7.1 job start (preflight, pre-pass, stale review rows, snapshot) and §5 ingest file rules.</summary>
public class IngestJobTests
{
    private const string Gb = "Nintendo - Game Boy";
    private const string Gbc = "Nintendo - Game Boy Color";

    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private ISystemRegistry _systems = null!;
    private DatCache _dats = null!;
    private IGameRepository _games = null!;
    private ILibraryMirror _mirror = null!;
    private IIngestReviewRepository _reviews = null!;
    private IJobItemRepository _items = null!;
    private RnRomManager.Core.Audit.IAuditLog _audit = null!;
    private InfoJsonReader _reader = null!;
    private IngestFileScanner _scanner = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Roms = _dir.Combine("roms"), Ingest = _dir.Combine("ingest"), Cache = _dir.Combine("cache") };
        ISystemHandler[] handlers = [Handler("gb", Gb), Handler("gbc", Gbc)];
        _systems = Substitute.For<ISystemRegistry>();
        _systems.All.Returns(handlers);
        _systems.ById(Arg.Any<string>()).Returns(c => handlers.FirstOrDefault(h => h.Id == c.Arg<string>()));
        _systems.ByFamily("gameboy").Returns(handlers);
        _dats = new DatCache(Options.Create(_paths), _systems, NullLogger<DatCache>.Instance);
        _games = Substitute.For<IGameRepository>();
        _games.ListSyncStateAsync(default).ReturnsForAnyArgs([]);
        _mirror = Substitute.For<ILibraryMirror>();
        _reviews = Substitute.For<IIngestReviewRepository>();
        _reviews.ListPendingSourcesAsync(default).ReturnsForAnyArgs([]);
        _items = Substitute.For<IJobItemRepository>();
        _audit = Substitute.For<RnRomManager.Core.Audit.IAuditLog>();
        _reader = new InfoJsonReader([], _systems);
        _scanner = new IngestFileScanner(Options.Create(_paths), _systems);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static ISystemHandler Handler(string id, string datName)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.Id.Returns(id);
        handler.DatName.Returns(datName);
        handler.Family.Returns("gameboy");
        handler.RomExtensions.Returns(["." + id]);
        handler.MinRomSizeBytes.Returns(0x150);
        handler.MaxRomSizeBytes.Returns(16L * 1024 * 1024);
        return handler;
    }

    private void LoadDats(params string[] datNames)
    {
        FixtureDatWriter.Write(_dats.Directory, datNames.ToDictionary(n => n, _ => (IReadOnlyList<FixtureGame>)[]));
        _dats.Load();
    }

    [Test]
    public async Task Selection_ProcessesOnlyListedFiles_TotalIsTheSelectedCount()
    {
        var chosen = Drop("gb", "chosen.gb", TimeSpan.FromMinutes(5));
        var other = Drop("gb", "other.gb", TimeSpan.FromMinutes(5));
        var progress = new JobProgress();
        var job = Job();
        // Unknown system and file entries are ignored; the set already drops repeats.
        job.Selection = new HashSet<(string, string)> { ("gb", "chosen.gb"), ("nes", "chosen.gb"), ("gb", "missing.gb"), ("gb", "CHOSEN.GB") };

        await job.RunAsync(new JobContext(Guid.NewGuid(), progress), CancellationToken.None);

        await _items.Received(1).InsertAsync(Arg.Any<JobItem>(), Arg.Any<CancellationToken>());
        await _items.DidNotReceive().InsertAsync(Arg.Is<JobItem>(i => i.SourcePath == other), Arg.Any<CancellationToken>());
        Assert.That((progress.Current, progress.Total), Is.EqualTo((1, 1)));
        Assert.That(File.Exists(other), Is.True, "unselected file untouched");
        Assert.That(File.Exists(chosen), Is.False, "the selected 3-byte file moved to _failed");
    }

    private IngestJob Job()
    {
        var roms = Substitute.For<IRomRepository>();
        roms.ListSha1sAsync(default).ReturnsForAnyArgs([]);
        var sync = new DbSync(_games, roms, _mirror, _reader, _systems, Options.Create(_paths), NullLogger<DbSync>.Instance);
        var media = new RnRomManager.Core.Media.MediaService(new HttpClient(), Options.Create(new SourcesOptions { ThumbnailBaseUrl = "" }),
            NullLogger<RnRomManager.Core.Media.MediaService>.Instance);
        var pipeline = new IngestPipeline(_dats, _systems, roms, _games, _mirror, Substitute.For<RnRomManager.Core.Integrity.IIntegrityIssueRepository>(), _reviews, _reader, media, Options.Create(_paths), _audit, new RnRomManager.Core.Audit.AuditPaths(Options.Create(_paths)), TimeProvider.System,
            NullLogger<IngestPipeline>.Instance);
        return new IngestJob(_dats, _systems, sync, _reviews, _items, _scanner, pipeline, new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance),
            Options.Create(_paths), TimeProvider.System, NullLogger<IngestJob>.Instance);
    }

    private Task<object?> RunAsync(JobProgress? progress = null) =>
        Job().RunAsync(new JobContext(Guid.NewGuid(), progress ?? new JobProgress()), CancellationToken.None);

    private string Drop(string system, string name, TimeSpan age)
    {
        var path = Path.Combine(_paths.Ingest, system, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Test]
    public void Preflight_DatsMissingForAnySystem()
    {
        Assert.That(Job().Preflight(), Is.EqualTo(IngestJob.DatsMissing), "no DATs at all");

        LoadDats(Gb);
        Assert.That(Job().Preflight(), Is.EqualTo(IngestJob.DatsMissing), "gbc has none");

        LoadDats(Gb, Gbc);
        Assert.That(Job().Preflight(), Is.Null);
    }

    [Test]
    public async Task PrePass_InsertsAZipWithoutADbRow()
    {
        var game = Game("Tetris", Rom("Tetris.gb", RomBytes(1)));
        await WriteZipAsync(_reader, Path.Combine(_paths.Roms, "gb", "T", "Tetris.zip"), game, 1);

        await RunAsync();

        await _mirror.Received(1).ApplyAsync(
            Arg.Is<IReadOnlyList<MirrorEntry>>(e => e.Single().Info.GameId == game.GameId && e.Single().ZipPath == "gb/T/Tetris.zip"),
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StalePendingReviewRows_AreDeleted()
    {
        var kept = Drop("gb", "kept.gb", TimeSpan.FromHours(1));
        var archive = Drop("gb", "pack.zip", TimeSpan.FromHours(1));
        _reviews.ListPendingSourcesAsync(default).ReturnsForAnyArgs([
            new PendingReviewSource(1, kept, null),
            new PendingReviewSource(2, _dir.Combine("ingest", "gb", "gone.gb"), null),
            new PendingReviewSource(3, _dir.Combine("cache", "work", "x", "entry.gb"), archive), // archive still there
            new PendingReviewSource(4, _dir.Combine("cache", "work", "y", "entry.gb"), _dir.Combine("ingest", "gb", "gone.zip")),
        ]);

        await RunAsync();

        await _reviews.Received(1).DeleteAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 2, 4 })), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TooRecentFiles_AreSkipped_OlderOnesProcessed()
    {
        var recent = Drop("gb", "copying.gb", TimeSpan.FromSeconds(2));
        Drop("gbc", "ready.gbc", TimeSpan.FromMinutes(5));
        var progress = new JobProgress();

        var summary = (Dictionary<string, int>)(await RunAsync(progress))!;

        await _items.Received(1).InsertAsync(Arg.Is<JobItem>(i =>
            i.SourcePath == recent && i.SystemId == "gb" && i.Outcome == IngestOutcome.Skipped && i.Reason == IngestReason.TooRecent),
            Arg.Any<CancellationToken>());
        // The older 3-byte file is processed: too small for a GBC ROM.
        await _items.Received(1).InsertAsync(Arg.Is<JobItem>(i => i.Outcome == IngestOutcome.Failed && i.Reason == IngestFailures.TooSmall),
            Arg.Any<CancellationToken>());
        Assert.That(summary[IngestOutcome.Skipped], Is.EqualTo(1));
        Assert.That(summary[IngestOutcome.Failed], Is.EqualTo(1));
        Assert.That((progress.Current, progress.Total), Is.EqualTo((2, 2)));
        Assert.That(File.Exists(recent), Is.True, "file untouched");
    }

    [Test]
    public void Scanner_TopLevelOnly_NoDotFilesOrJunk()
    {
        var old = TimeSpan.FromHours(1);
        Drop("gb", "Tetris.GB", old);
        Drop("gb", ".DS_Store", old);
        Drop("gb", "._Tetris.gb", old);
        Drop("gb", "THUMBS.DB", old);
        Drop("gb", "Desktop.ini", old);
        Drop("gb", Path.Combine("_failed", "bad.gb"), old);
        Drop("gb", Path.Combine("sub", "nested.gb"), old);
        Drop("md", "unregistered.md", old);

        var scan = _scanner.Scan();

        Assert.That(scan.Select(s => s.SystemId), Is.EqualTo(new[] { "gb", "gbc" }));
        Assert.That(scan[0].Files.Select(f => f.Name), Is.EqualTo(new[] { "Tetris.GB" }));
        Assert.That(scan[0].Files[0].SizeBytes, Is.EqualTo(3));
        Assert.That(scan[1].Files, Is.Empty, "missing folder = no files");
    }

    [Test]
    public async Task ThroughTheRunner_DatsMissing_NoRow_LockFree()
    {
        var jobs = Substitute.For<IJobRepository>();
        var jobLock = new JobLock();
        var runner = new JobRunner(jobs, jobLock, Substitute.For<RnRomManager.Core.Users.IUserRepository>(), TimeProvider.System, NullLogger<JobRunner>.Instance);

        var ex = await Assert.ThrowsAsync<JobRefusedException>(() => runner.StartAsync(Job(), JobTrigger.Manual, null, CancellationToken.None));

        Assert.That(ex!.Code, Is.EqualTo(IngestJob.DatsMissing));
        await jobs.DidNotReceiveWithAnyArgs().InsertAsync(default!, default);
        using var free = jobLock.TryAcquire();
        Assert.That(free, Is.Not.Null);
    }
}
