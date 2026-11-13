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
        _scanner = new IngestFileScanner(Options.Create(_paths), _systems, NullLogger<IngestFileScanner>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static ISystemHandler Handler(string id, string datName)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.EmbedRomsInZip.Returns(true);
        handler.Id.Returns(id);
        handler.DatName.Returns(datName);
        handler.DatFolder.Returns(DatKind.NoIntro);
        handler.MetadatKinds.Returns(DatKind.Metadat);
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

    [Test]
    public void ArchiveFate_ASkippedEntryKeepsTheArchive_EvenWhenOthersCommitted()
    {
        static IngestItemResult R(string outcome) => new(outcome, null);
        var ingested = R(IngestOutcome.Ingested);

        Assert.That(IngestJob.FateOf([ingested, new IngestItemResult(IngestOutcome.Skipped, IngestReason.TooRecent)]), Is.EqualTo(IngestJob.ArchiveFate.Keep));
        Assert.That(IngestJob.FateOf([ingested, R(IngestOutcome.Review)]), Is.EqualTo(IngestJob.ArchiveFate.Keep));
        Assert.That(IngestJob.FateOf([ingested, null]), Is.EqualTo(IngestJob.ArchiveFate.Keep));
        Assert.That(IngestJob.FateOf([ingested, R(IngestOutcome.Failed), R(IngestOutcome.Duplicate)]), Is.EqualTo(IngestJob.ArchiveFate.Failed));
        Assert.That(IngestJob.FateOf([R(IngestOutcome.Duplicate), R(IngestOutcome.Duplicate)]), Is.EqualTo(IngestJob.ArchiveFate.Duplicates));
        Assert.That(IngestJob.FateOf([ingested, R(IngestOutcome.Duplicate), R(IngestOutcome.Repaired)]), Is.EqualTo(IngestJob.ArchiveFate.Delete));
    }

    private IngestJob Job(ExternalIngestHooks? hooks = null)
    {
        var roms = Substitute.For<IRomRepository>();
        roms.ListSha1sAsync(default).ReturnsForAnyArgs([]);
        var sync = new DbSync(_games, roms, _mirror, _reader, _systems, Options.Create(_paths), NullLogger<DbSync>.Instance);
        var media = TestMedia.Offline();
        var pipeline = new IngestPipeline(_dats, _systems, roms, _games, _mirror, Substitute.For<RnRomManager.Core.Integrity.IIntegrityIssueRepository>(), _reviews, _reader, media, Options.Create(_paths), _audit, new RnRomManager.Core.Audit.AuditPaths(Options.Create(_paths)), TimeProvider.System,
            NullLogger<IngestPipeline>.Instance) { ExternalHooks = hooks };
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
            new PendingReviewSource(1, "gb", kept, null),
            new PendingReviewSource(2, "gb", _dir.Combine("ingest", "gb", "gone.gb"), null),
            new PendingReviewSource(3, "gb", _dir.Combine("cache", "work", "x", "entry.gb"), archive), // archive still there
            new PendingReviewSource(4, "gb", _dir.Combine("cache", "work", "y", "entry.gb"), _dir.Combine("ingest", "gb", "gone.zip")),
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

    // --- disc sets (B7) ---

    /// <summary>
    /// A FileSets system that takes no .cue: nothing is planned there. A class, not a substitute: Castle proxies can't
    /// intercept <see cref="ISystemHandler.ReadHeader"/>'s span parameter, which the external path calls.
    /// </summary>
    private sealed class DiscXHandler : ISystemHandler
    {
        public string Id => "discx";
        public string DisplayName => "Disc X";
        public string Family => "discx";
        public IReadOnlyList<string> RomExtensions => [".bin"];
        public long MinRomSizeBytes => 1;
        public long MaxRomSizeBytes => 1024 * 1024;
        public string DatName => "Test - Disc X";
        public bool EmbedRomsInZip => false;
        public bool FileSets => true;
        public string DatFolder => DatKind.Redump;
        public IReadOnlyList<string> MetadatKinds => [];
        public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;
        public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
    }

    private static ISystemHandler DiscX() => new DiscXHandler();

    /// <summary>Adds <see cref="ExternalSetTestHandler"/> and <see cref="DiscX"/> to the registry, with their empty Redump DATs.</summary>
    private void LoadDiscDats()
    {
        ISystemHandler[] handlers = [.. _systems.All, new ExternalSetTestHandler(), DiscX()];
        _systems.All.Returns(handlers);
        _systems.ById(Arg.Any<string>()).Returns(c => handlers.FirstOrDefault(h => h.Id == c.Arg<string>()));
        _systems.ByFamily(ExternalSetTestHandler.SystemId).Returns([handlers[^2]]);
        _systems.ByFamily("discx").Returns([handlers[^1]]);
        FixtureDatWriter.Write(_dats.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [ExternalSetTestHandler.DatNameValue] = [],
            ["Test - Disc X"] = [],
        }, layouts: new Dictionary<string, FixtureDatLayout>
        {
            [ExternalSetTestHandler.DatNameValue] = FixtureDatLayout.Redump,
            ["Test - Disc X"] = FixtureDatLayout.Redump,
        });
        _dats.Load();
    }

    /// <summary>An unmatched 3-file set (it goes to Review), a minute old.</summary>
    private void DropDisc(string system = ExternalSetTestHandler.SystemId)
    {
        var folder = Path.Combine(_paths.Ingest, system);
        Directory.CreateDirectory(folder);
        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("Disc.cue", System.Text.Encoding.UTF8.GetBytes("FILE \"Disc (Track 1).bin\" BINARY\nFILE \"Disc (Track 2).bin\" BINARY\n")),
            ("Disc (Track 1).bin", RomBytes(21)),
            ("Disc (Track 2).bin", RomBytes(22)),
        })
        {
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            File.SetLastWriteTimeUtc(Path.Combine(folder, name), DateTime.UtcNow.AddMinutes(-1));
        }
    }

    private List<JobItem> Recorded() =>
        [.. _items.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IJobItemRepository.InsertAsync)).Select(c => (JobItem)c.GetArguments()[0]!)];

    [Test]
    public async Task Cancel_DuringASet_FinishesTheSet_ThenStops()
    {
        LoadDiscDats();
        DropDisc();
        Drop(ExternalSetTestHandler.SystemId, "Later.iso", TimeSpan.FromMinutes(1));
        using var cancel = new CancellationTokenSource();
        var job = Job(new ExternalIngestHooks { BeforeHash = f => { if (f.Name == "Disc (Track 1).bin") cancel.Cancel(); } });

        Assert.That(async () => await job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), cancel.Token),
            Throws.InstanceOf<OperationCanceledException>());

        Assert.That(Recorded().Select(i => (Path.GetFileName(i.SourcePath), i.Outcome)), Is.EqualTo(new[]
        {
            ("Disc.cue", IngestOutcome.Review), ("Disc (Track 1).bin", IngestOutcome.Review), ("Disc (Track 2).bin", IngestOutcome.Review),
        }), "the started set ran to the end; Later.iso was never processed");
    }

    [Test]
    public async Task SelectingOneBinOfASet_CountsTheWholeSet()
    {
        LoadDiscDats();
        DropDisc();
        Drop(ExternalSetTestHandler.SystemId, "Other.iso", TimeSpan.FromMinutes(1));
        var progress = new JobProgress();
        var job = Job();
        job.Selection = new HashSet<(string, string)> { (ExternalSetTestHandler.SystemId, "Disc (Track 2).bin") };

        await job.RunAsync(new JobContext(Guid.NewGuid(), progress), CancellationToken.None);

        Assert.That((progress.Current, progress.Total), Is.EqualTo((3, 3)));
        Assert.That(Recorded().Select(i => Path.GetFileName(i.SourcePath)), Is.EqualTo(new[] { "Disc.cue", "Disc (Track 1).bin", "Disc (Track 2).bin" }));
    }

    [Test]
    public async Task FileSetsSystemWithoutCue_PlansNothing()
    {
        LoadDiscDats();
        DropDisc("discx");

        await RunAsync();

        Assert.That(Recorded().Select(i => (Path.GetFileName(i.SourcePath), i.Outcome, i.Reason)), Is.EquivalentTo(new[]
        {
            ("Disc.cue", IngestOutcome.Failed, (string?)IngestFailures.UnsupportedType),
            ("Disc (Track 1).bin", IngestOutcome.Review, "NoDatMatch"),
            ("Disc (Track 2).bin", IngestOutcome.Review, "NoDatMatch"),
        }));
    }
}
