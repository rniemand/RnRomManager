using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Spec I1–I3 for an external system: streamed preparation of an ingest item, before placement (step 9f).</summary>
public class IngestExternalPreparationTests
{
    private static readonly AuditActor Actor = new(null, null, Guid.NewGuid());

    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private IIngestReviewRepository _reviews = null!;
    private IRomRepository _roms = null!;
    private IJobItemRepository _items = null!;
    private string _ingest = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Roms = _dir.Combine("roms"), Ingest = _dir.Combine("ingest"), Cache = _dir.Combine("cache") };
        _reviews = Substitute.For<IIngestReviewRepository>();
        _reviews.ListPendingSourcesAsync(default).ReturnsForAnyArgs([]);
        _roms = Substitute.For<IRomRepository>();
        _roms.ListGameIdsByBaseTitleAsync(default!, default!, default).ReturnsForAnyArgs([]);
        _roms.ListSha1sAsync(default).ReturnsForAnyArgs([]);
        _items = Substitute.For<IJobItemRepository>();
        _ingest = Directory.CreateDirectory(Path.Combine(_paths.Ingest, "ext")).FullName;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    /// <summary>A headerless external handler that records how many bytes each <c>ReadHeader</c> call got.</summary>
    private sealed class Handler(string id, string[] extensions, long maxBytes = 64L * 1024 * 1024) : ISystemHandler
    {
        public List<int> HeaderLengths { get; } = [];
        public string Id => id;
        public string DisplayName => id;
        public string Family => "ext";
        public IReadOnlyList<string> RomExtensions => extensions;
        public long MinRomSizeBytes => 1;
        public long MaxRomSizeBytes => maxBytes;
        public string DatName => "Test - " + id;
        public bool EmbedRomsInZip => false;
        public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;

        public RomHeader? ReadHeader(ReadOnlySpan<byte> rom)
        {
            HeaderLengths.Add(rom.Length);
            return null;
        }
    }

    private (IngestPipeline Pipeline, SystemRegistry Systems, DatCache Dats) Build(ISystemHandler[] handlers, Action<IngestFile>? beforeHash = null)
    {
        var systems = new SystemRegistry(handlers);
        var options = Options.Create(_paths);
        var dats = new DatCache(options, systems, NullLogger<DatCache>.Instance);
        var pipeline = new IngestPipeline(dats, systems, _roms, Substitute.For<IGameRepository>(), Substitute.For<ILibraryMirror>(),
            Substitute.For<IIntegrityIssueRepository>(), _reviews, new InfoJsonReader([], systems),
            TestMedia.Offline(),
            options, Substitute.For<IAuditLog>(), new AuditPaths(options), TimeProvider.System, NullLogger<IngestPipeline>.Instance)
        {
            ExternalHooks = new ExternalIngestHooks { BeforeHash = beforeHash },
        };
        return (pipeline, systems, dats);
    }

    private static void LoadDat(DatCache dats, ISystemHandler handler, FixtureGame game)
    {
        FixtureDatWriter.Write(dats.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>> { [handler.DatName] = [game] });
        dats.Load();
    }

    private IngestFile Drop(string name, byte[] bytes, TimeSpan? age = null)
    {
        var path = Path.Combine(_ingest, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(1)));
        return new IngestFile("ext", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
    }

    private static byte[] RomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    private Task<IngestItemResult?> ProcessAsync(IngestPipeline pipeline, IngestFile file) =>
        pipeline.ProcessAsync(file, Guid.NewGuid(), Actor, new CircuitBreaker(), CancellationToken.None);

    private static (string?, string?) Code(IngestItemResult? result) => (result?.Outcome, result?.Reason);

    [Test]
    public async Task Unmatched_IsQueuedFromItsStreamedData_WithItsOriginalExtension()
    {
        var handler = new Handler("ext", [".nsp", ".xci"]);
        var (pipeline, _, _) = Build([handler]);
        var bytes = RomBytes(3 * 1024 * 1024 + 5);
        var file = Drop("Some Game.XCI", bytes);

        var result = await ProcessAsync(pipeline, file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
        await _reviews.Received(1).UpsertAsync(Arg.Is<ReviewItem>(r => r.SystemId == "ext" && r.OriginalFileName == "Some Game.XCI"
            && r.SourcePath == file.FullPath && r.SizeBytes == bytes.Length && r.Sha1 == Convert.ToHexStringLower(SHA1.HashData(bytes))
            && r.Md5 == Convert.ToHexStringLower(MD5.HashData(bytes)) && r.Crc32 == Crc32.ToHex(bytes)), Arg.Any<CancellationToken>());
        Assert.That(handler.HeaderLengths, Is.EqualTo(new[] { RomFileHasher.HeaderBytes }), "ReadHeader gets at most 1 MiB");
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(bytes), "left in place, not renamed");
    }

    [TestCase(1, 0, 60, TestName = "FreshSnapshot_DifferentFromTheListing_SkipsBeforeHashing length")]
    [TestCase(0, 5, 60, TestName = "FreshSnapshot_DifferentFromTheListing_SkipsBeforeHashing mtime")]
    [TestCase(0, 0, 2, TestName = "FreshSnapshot_DifferentFromTheListing_SkipsBeforeHashing under 10 s old")]
    public async Task FreshSnapshot_DifferentFromTheListing_SkipsBeforeHashing(int extraListedBytes, int listedSecondsEarlier, int ageSeconds)
    {
        var hashed = false;
        var (pipeline, _, _) = Build([new Handler("ext", [".nsp"])], _ => hashed = true);
        var listed = Drop("game.nsp", RomBytes(64), TimeSpan.FromSeconds(ageSeconds));
        listed = listed with { SizeBytes = listed.SizeBytes + extraListedBytes, ModifiedAtUtc = listed.ModifiedAtUtc.AddSeconds(-listedSecondsEarlier) };

        var result = await ProcessAsync(pipeline, listed);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Skipped, IngestReason.TooRecent)));
        Assert.That(hashed, Is.False);
        Assert.That(File.Exists(listed.FullPath), Is.True);
        await _reviews.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [TestCase(1024, TestName = "GrowthDuringHashing_Skips within the cap")]
    [TestCase(64, TestName = "GrowthDuringHashing_Skips past the cap")]
    public async Task GrowthDuringHashing_Skips(int maxBytes)
    {
        var bytes = RomBytes(64);
        var (pipeline, _, _) = Build([new Handler("ext", [".nsp"], maxBytes)], f =>
        {
            using var append = new FileStream(f.FullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            append.Write(new byte[16]);
        });
        var file = Drop("game.nsp", bytes);

        var result = await ProcessAsync(pipeline, file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Skipped, IngestReason.TooRecent)));
        Assert.That(new FileInfo(file.FullPath).Length, Is.EqualTo(80), "left in place");
        await _reviews.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [TestCase("extension", TestName = "SiblingDatHit_ThatCantStoreTheSource_IsIgnored extension")]
    [TestCase("size", TestName = "SiblingDatHit_ThatCantStoreTheSource_IsIgnored size")]
    public async Task SiblingDatHit_ThatCantStoreTheSource_IsIgnored(string rejects)
    {
        var bytes = RomBytes(64);
        var ext = new Handler("ext", [".nsp", ".xci"]);
        var sibling = rejects == "extension" ? new Handler("ext2", [".nsp"]) : new Handler("ext2", [".nsp", ".xci"], maxBytes: 32);
        var (pipeline, _, dats) = Build([ext, sibling]);
        LoadDat(dats, sibling, new FixtureGame("Game (USA)", "Game (USA).nsp", bytes));

        var result = await ProcessAsync(pipeline, Drop("game.xci", bytes));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
        Assert.That(result!.SystemId, Is.EqualTo("ext"));
        Assert.That(sibling.HeaderLengths, Is.Empty, "an ineligible handler isn't asked");
    }

    [Test]
    public async Task DatHitForAGameWithoutAGamesRow_IsTargetZipUnreadable_AndKeepsItsPendingRow()
    {
        var bytes = RomBytes(64);
        var ext = new Handler("ext", [".nsp", ".xci"]);
        var (pipeline, _, dats) = Build([ext]);
        LoadDat(dats, ext, new FixtureGame("Game (USA)", "Game (USA).nsp", bytes));
        _roms.ListGameIdsByBaseTitleAsync(default!, default!, default).ReturnsForAnyArgs([Guid.NewGuid()]);
        var file = Drop("game.xci", bytes);

        var result = await ProcessAsync(pipeline, file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.TargetZipUnreadable)));
        Assert.That(File.Exists(file.FullPath), Is.True);
        Assert.That(Directory.Exists(_paths.Roms), Is.False, "nothing written");
        await _reviews.DidNotReceiveWithAnyArgs().CompleteSourceAsync(default!, default, default!, default, default, default);
    }

    [Test]
    public async Task AmbiguousGroup_QueuesTheDatStem_WithTheSourceExtension()
    {
        var bytes = RomBytes(64);
        var ext = new Handler("ext", [".nsp", ".xci"]);
        var (pipeline, _, dats) = Build([ext]);
        LoadDat(dats, ext, new FixtureGame("Game (USA)", "Game (USA).nsp", bytes));
        _roms.ListGameIdsByBaseTitleAsync(default!, default!, default).ReturnsForAnyArgs([Guid.NewGuid(), Guid.NewGuid()]);

        var result = await ProcessAsync(pipeline, Drop("dump.XCI", bytes));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Review, "AmbiguousGroup")));
        await _reviews.Received(1).UpsertAsync(Arg.Is<ReviewItem>(r => r.DatIdentification!.Contains("\"romName\":\"Game (USA).xci\"")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VanishedSource_IsAnIoError_AndKeepsItsPendingRow()
    {
        var (pipeline, _, _) = Build([new Handler("ext", [".nsp"])]);
        var file = Drop("game.nsp", RomBytes(64));
        File.Delete(file.FullPath);

        var result = await ProcessAsync(pipeline, file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.IoError)));
        await _reviews.DidNotReceiveWithAnyArgs().CompleteSourceAsync(default!, default, default!, default, default, default);
    }

    private IngestJob Job(IngestPipeline pipeline, SystemRegistry systems, DatCache dats, IngestFileScanner? scanner = null)
    {
        var options = Options.Create(_paths);
        var games = Substitute.For<IGameRepository>();
        games.ListSyncStateAsync(default).ReturnsForAnyArgs([]);
        var reader = new InfoJsonReader([], systems);
        var sync = new DbSync(games, _roms, Substitute.For<ILibraryMirror>(), reader, systems, options, NullLogger<DbSync>.Instance);
        return new IngestJob(dats, systems, sync, _reviews, _items,
            scanner ?? new IngestFileScanner(options, systems, NullLogger<IngestFileScanner>.Instance), pipeline,
            new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance), options, TimeProvider.System, NullLogger<IngestJob>.Instance);
    }

    [Test]
    public async Task CancelDuringAnExternalItem_FinishesItAndItsBookkeeping_ThenStops()
    {
        using var cancel = new CancellationTokenSource();
        var (pipeline, systems, dats) = Build([new Handler("ext", [".nsp"])], _ => cancel.Cancel());
        var first = Drop("a.nsp", RomBytes(64));
        var second = Drop("b.nsp", RomBytes(64));

        Assert.That(async () => await Job(pipeline, systems, dats).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), cancel.Token),
            Throws.InstanceOf<OperationCanceledException>());

        await _reviews.Received(1).UpsertAsync(Arg.Is<ReviewItem>(r => r.SourcePath == first.FullPath), CancellationToken.None);
        await _reviews.Received(1).CompleteSourceAsync(first.FullPath, null, Arg.Any<IngestItemResult>(), Arg.Any<Guid>(), Arg.Any<DateTime>(), CancellationToken.None);
        await _items.Received(1).InsertAsync(Arg.Is<JobItem>(i => i.SourcePath == first.FullPath && i.Outcome == IngestOutcome.Review), Arg.Any<CancellationToken>());
        await _items.DidNotReceive().InsertAsync(Arg.Is<JobItem>(i => i.SourcePath == second.FullPath), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StaleCleanup_PrunesOnlyConfirmedAbsence()
    {
        var (pipeline, systems, dats) = Build([new Handler("ext", [".nsp"])]);
        var present = Drop("present.nsp", RomBytes(64));
        var folderAtSource = Directory.CreateDirectory(Path.Combine(_ingest, "folder.nsp")).FullName;
        var folderAtContainer = Directory.CreateDirectory(Path.Combine(_ingest, "pack.zip")).FullName;
        _reviews.ListPendingSourcesAsync(default).ReturnsForAnyArgs([
            new PendingReviewSource(1, "ext", Path.Combine(_ingest, "gone.nsp"), null),
            new PendingReviewSource(2, "ext", folderAtSource, null),
            new PendingReviewSource(3, "ext", Path.Combine(_paths.Cache, "work", "x", "entry.nsp"), folderAtContainer),
            new PendingReviewSource(4, "ext", present.FullPath, null),
            new PendingReviewSource(5, "ext", Path.Combine(_paths.Cache, "work", "y", "entry.nsp"), Path.Combine(_ingest, "gone.zip")),
        ]);

        await Job(pipeline, systems, dats).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);

        await _reviews.Received(1).DeleteAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 1, 5 })), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnexpectedError_KeepsThePendingRow()
    {
        var (pipeline, systems, dats) = Build([new Handler("ext", [".nsp"])]);
        _reviews.GetBySha1Async(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("boom"));
        var file = Drop("a.nsp", RomBytes(64));

        await Job(pipeline, systems, dats).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);

        await _items.Received(1).InsertAsync(Arg.Is<JobItem>(i => i.SourcePath == file.FullPath && i.Reason == IngestErrors.Unexpected), Arg.Any<CancellationToken>());
        await _reviews.DidNotReceiveWithAnyArgs().CompleteSourceAsync(default!, default, default!, default, default, default);
    }

    [TestCase(1025, TestName = "SourcePathLimit_IsChecked_BeforeTheFileIsTouched 1025 scalars")]
    [TestCase(1024, TestName = "SourcePathLimit_IsChecked_BeforeTheFileIsTouched 1024 scalars")]
    public async Task SourcePathLimit_IsChecked_BeforeTheFileIsTouched(int scalars)
    {
        var (pipeline, systems, dats) = Build([new Handler("ext", [".nsp"])]);
        var prefix = _ingest + Path.DirectorySeparatorChar;
        // The limit counts scalars: the emoji is one, though it is two UTF-16 chars.
        var name = "😀" + new string('a', scalars - prefix.Length - 1 - 4) + ".nsp";
        var path = prefix + name;
        var scanner = Substitute.For<IngestFileScanner>(Options.Create(_paths), systems, NullLogger<IngestFileScanner>.Instance);
        scanner.Scan().Returns([("ext", (IReadOnlyList<IngestFile>)[new IngestFile("ext", path, name, 64, DateTime.UtcNow.AddHours(-1))])]);

        await Job(pipeline, systems, dats, scanner).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);

        var item = (JobItem)_items.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IJobItemRepository.InsertAsync)).GetArguments()[0]!;
        Assert.That(item.Outcome, Is.EqualTo(IngestOutcome.Error));
        if (scalars > ExternalRomName.MaxSourcePathScalars)
        {
            Assert.That(item.SourcePath, Is.EqualTo(path[..^1]), "cut at the limit");
            Assert.That(item.Details, Does.Contain("1024"));
        }
        else
        {
            Assert.That(item.SourcePath, Is.EqualTo(path));
            Assert.That(item.Details, Does.Not.Contain("1024"), "passed the limit check; the pipeline found no file");
        }
    }
}
