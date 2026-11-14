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

    private static readonly RnRomManager.Core.Audit.AuditActor Actor = new(42, "boss", null);
    private RnRomManager.Core.Audit.IAuditLog _audit = null!;

    private List<RnRomManager.Core.Audit.AuditEvent> Recorded() =>
        _audit.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Record")
            .Select(c => (RnRomManager.Core.Audit.AuditEvent)c.GetArguments()[0]!).ToList();

    private PathsOptions _paths = null!;
    private DatCache _dats = null!;

    [SetUp]
    public void SetUp() => Build(new Headerless());

    private void Build(ISystemHandler handler)
    {
        _audit = Substitute.For<RnRomManager.Core.Audit.IAuditLog>();
        _dir = new TempDir();
        var paths = _paths = new PathsOptions { Ingest = _dir.Combine("ingest"), Roms = _dir.Combine("roms"), Cache = _dir.Combine("cache") };
        Directory.CreateDirectory(paths.Ingest);
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        _lock = new JobLock();
        _reviews = Substitute.For<IIngestReviewRepository>();
        _item = new ReviewItem { Id = 1, SystemId = "gb", Status = "Pending", SourcePath = Path.Combine(paths.Ingest, "test.gb"),
            OriginalFileName = "test.gb", Sha1 = new string('0', 40), SizeBytes = 3 };
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]);
        _reviews.GetAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _item);
        var systems = new SystemRegistry([handler]);
        var options = Options.Create(paths);
        var dats = _dats = new DatCache(options, systems, NullLogger<DatCache>.Instance);
        var games = Substitute.For<IGameRepository>();
        var reader = new InfoJsonReader([], systems);
        var media = TestMedia.Offline();
        var pipeline = new IngestPipeline(dats, systems, Substitute.For<IRomRepository>(), games, Substitute.For<ILibraryMirror>(),
            Substitute.For<IIntegrityIssueRepository>(), _reviews, reader, media, options, _audit,
            new RnRomManager.Core.Audit.AuditPaths(options), _time, NullLogger<IngestPipeline>.Instance);
        var romRepository = Substitute.For<IRomRepository>();
        _resolver = new ReviewResolver(_lock, _reviews, games, romRepository,
            new DbSync(games, romRepository, Substitute.For<ILibraryMirror>(), reader, systems, options, NullLogger<DbSync>.Instance), pipeline, dats, systems,
            new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance),
            new ReviewSets(new IngestFileScanner(options, systems, NullLogger<IngestFileScanner>.Instance), _reviews, systems, options, NullLogger<ReviewSets>.Instance),
            options, _audit, new RnRomManager.Core.Audit.AuditPaths(options), _time,
            NullLogger<ReviewResolver>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    [Test]
    public async Task BusyLock_RefusesWithoutReadingOrChangingReview()
    {
        using var held = _lock.TryAcquire();
        Assert.That((await _resolver.DismissAsync(1, Actor, default)).Code, Is.EqualTo("JobRunning"));
        await _reviews.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
    }

    [TestCase("Resolved")]
    [TestCase("Dismissed")]
    public async Task CompletedItems_CannotBeActedOnAgain(string status)
    {
        _item = _item with { Status = status };
        Assert.That((await _resolver.DismissAsync(1, Actor, default)).Code, Is.EqualTo("NotFound"));
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
    }

    [Test]
    public async Task Disc_OnANonDiscSystem_IsAValidationError_AndKeepsPending()
    {
        var request = new ReviewResolutionRequest { Action = "Manual", Title = "My Game", Regions = [], Languages = [], Flags = [], Disc = 2 };

        var result = await _resolver.ResolveInJobAsync(1, request, Actor, default);

        Assert.That(result.Code, Is.EqualTo("ValidationFailed"));
        Assert.That(result.Errors!["disc"], Is.EqualTo(new[] { "Disc numbers are for disc systems." }));
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        await _reviews.DidNotReceiveWithAnyArgs().SetResolutionAsync(default(long), default!, default, default, default, default);
    }

    [Test]
    public async Task MissingSource_DeletesStaleRowAndReleasesLock()
    {
        File.Delete(_item.SourcePath);
        Assert.That((await _resolver.DismissAsync(1, Actor, default)).Code, Is.EqualTo("SourceMissing"));
        await _reviews.Received().DeleteAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.Single() == 1), default);
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
    }

    [Test]
    public async Task DismissLooseFile_MovesIt_UsesInjectedTimeAndAdmin()
    {
        Assert.That((await _resolver.DismissAsync(1, Actor, default)).Code, Is.Null);
        var moved = _dir.Combine("ingest", "_failed", "test.gb");
        Assert.That(File.Exists(_item.SourcePath), Is.False);
        Assert.That(File.ReadAllText(moved + ".error.txt"), Does.Contain("Dismissed").And.Contain("2026-11-04T12:00:00")
            .And.Contain("job: review by boss"));
        await _reviews.Received().SetResolutionAsync(1, "Dismissed", null, 42, _time.GetUtcNow().UtcDateTime, default);
        var row = Recorded().Single();
        Assert.That((row.Action, row.Actor, row.Details, row.TargetPath),
            Is.EqualTo(("MovedToFailed", Actor, "review dismiss", "ingest/_failed/test.gb")));
    }

    [Test]
    public async Task DismissArchive_DoesNotMoveOrExtractArchive()
    {
        _item = _item with { ArchivePath = _item.SourcePath, ArchiveEntry = "test.gb" };
        Assert.That((await _resolver.DismissAsync(1, Actor, default)).Code, Is.Null);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        Assert.That(Directory.Exists(_dir.Combine("cache", "work")), Is.False);
        await _reviews.Received().SetResolutionAsync(1, "Dismissed", null, 42, _time.GetUtcNow().UtcDateTime, default);
        Assert.That(Recorded(), Is.Empty, "dismissing an archive entry moves nothing");
    }

    [Test]
    public async Task ChangedBytes_ReturnVerifyFailed_WithoutCompletingReview()
    {
        var result = await _resolver.ResolveInJobAsync(1, new ReviewResolutionRequest { Action = "Manual", Title = "My Game", Regions = [], Languages = [], Flags = [] }, Actor, default);
        Assert.That(result.Code, Is.EqualTo("VerifyFailed"));
        await _reviews.DidNotReceiveWithAnyArgs().SetResolutionAsync(default(long), default!, default, default, default, default);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
    }

    private sealed class Headerless(params string[] extensions) : ISystemHandler
    {
        public string Id => "gb";
        public string DisplayName => "Test";
        public string Family => "gameboy";
        public IReadOnlyList<string> RomExtensions => extensions.Length == 0 ? [".gb"] : extensions;
        public long MinRomSizeBytes => 1;
        public long MaxRomSizeBytes => 1024;
        public string DatName => "GB";
        public bool EmbedRomsInZip => true;
        // A 512×512 PNG header: enough for MediaService's default-art check when a resolve stores a new game.
        public ReadOnlyMemory<byte> DefaultBoxArtPng { get; } = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 2, 0, 0, 0, 2, 0,
            8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
        };
        public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
    }

    [Test]
    public async Task Delete_LooseFile_DeletesItAndItsPendingRows()
    {
        _reviews.DeletePendingBySourceAsync(_item.SourcePath, null, Arg.Any<CancellationToken>()).Returns(1);

        Assert.That((await _resolver.DeleteSourceAsync(1, Actor, default)).Code, Is.Null);

        Assert.That(File.Exists(_item.SourcePath), Is.False);
        Assert.That(Directory.Exists(_dir.Combine("ingest", "_failed")), Is.False, "a hard delete, not a move");
        await _reviews.Received(1).DeletePendingBySourceAsync(_item.SourcePath, null, default);
        var row = Recorded().Single();
        Assert.That((row.Action, row.Actor, row.Path, row.TargetPath), Is.EqualTo(("ReviewDeleted", Actor, "ingest/test.gb", (string?)null)));
        await _reviews.DidNotReceiveWithAnyArgs().SetResolutionAsync(default(long), default!, default, default, default, default);
    }

    [Test]
    public async Task Delete_ArchiveEntry_DeletesTheWholeArchiveAndItsPendingRows()
    {
        var archive = _dir.Combine("ingest", "pack.zip");
        File.WriteAllBytes(archive, [9]);
        _item = _item with { SourcePath = archive, ArchivePath = archive, ArchiveEntry = "test.gb" };

        Assert.That((await _resolver.DeleteSourceAsync(1, Actor, default)).Code, Is.Null);

        Assert.That(File.Exists(archive), Is.False);
        Assert.That(File.Exists(_dir.Combine("ingest", "test.gb")), Is.True, "other files are untouched");
        await _reviews.Received(1).DeletePendingBySourceAsync(archive, archive, default);
    }

    [Test]
    public async Task Delete_MissingSource_StillRemovesTheRows()
    {
        File.Delete(_item.SourcePath);
        _item = _item with { SourcePath = _dir.Combine("ingest", "gone", "test.gb") };

        Assert.That((await _resolver.DeleteSourceAsync(1, Actor, default)).Code, Is.Null);
        await _reviews.Received(1).DeletePendingBySourceAsync(_item.SourcePath, null, default);
        Assert.That(Recorded(), Is.Empty, "nothing was deleted, so no ReviewDeleted row");
    }

    [Test]
    public async Task Delete_Failure_IsDeleteFailed_AndKeepsTheRows()
    {
        // A directory where the file should be: File.Delete refuses on every OS (a locked file only blocks on Windows).
        File.Delete(_item.SourcePath);
        Directory.CreateDirectory(_item.SourcePath);

        Assert.That((await _resolver.DeleteSourceAsync(1, Actor, default)).Code, Is.EqualTo("DeleteFailed"));
        await _reviews.DidNotReceiveWithAnyArgs().DeletePendingBySourceAsync(default!, default, default);
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
    }

    [Test]
    public async Task DeleteMany_CountsDeletedSkippedAndFailed_InOrder_UnderOneLock()
    {
        var broken = _dir.Combine("ingest", "broken.gb");
        Directory.CreateDirectory(broken); // File.Delete refuses a directory: DeleteFailed
        var archive = _dir.Combine("ingest", "pack.zip");
        File.WriteAllBytes(archive, [9]);
        var item2 = _item with { Id = 2, SourcePath = broken, OriginalFileName = "broken.gb" };
        var item3 = _item with { Id = 3, SourcePath = archive, ArchivePath = archive, ArchiveEntry = "a.gb" };
        var item4 = _item with { Id = 4, SourcePath = archive, ArchivePath = archive, ArchiveEntry = "b.gb" };
        _reviews.GetAsync(2, Arg.Any<CancellationToken>()).Returns(item2);
        _reviews.GetAsync(3, Arg.Any<CancellationToken>()).Returns(item3);
        // The archive sibling's row goes with the archive (DeletePendingBySourceAsync), so it's gone by its turn.
        _reviews.GetAsync(4, Arg.Any<CancellationToken>()).Returns(_ => File.Exists(archive) ? item4 : null);

        var result = await _resolver.DeleteSourcesAsync([1, 2, 3, 4, 99], Actor, default);

        Assert.That((result!.Deleted, result.Skipped), Is.EqualTo((2, 2)));
        Assert.That(result.Failed, Is.EqualTo(new[] { new ReviewBatchDeleteFailure(2, "DeleteFailed") }));
        Assert.That(File.Exists(_item.SourcePath) || File.Exists(archive), Is.False);
        Assert.That(Recorded().Select(r => (r.Action, r.Path)),
            Is.EqualTo(new[] { ("ReviewDeleted", (string?)"ingest/test.gb"), ("ReviewDeleted", "ingest/pack.zip") }));
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
    }

    [Test]
    public async Task DeleteMany_WhenBusy_IsNull_AndDeletesNothing()
    {
        using (_lock.TryAcquire())
            Assert.That(await _resolver.DeleteSourcesAsync([1], Actor, default), Is.Null);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        await _reviews.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [Test]
    public async Task Delete_NonPendingOrBusy()
    {
        _item = _item with { Status = "Resolved" };
        Assert.That((await _resolver.DeleteSourceAsync(1, Actor, default)).Code, Is.EqualTo("NotFound"));
        _item = _item with { Status = "Pending" };
        using (_lock.TryAcquire())
            Assert.That((await _resolver.DeleteSourceAsync(1, Actor, default)).Code, Is.EqualTo("JobRunning"));
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        await _reviews.DidNotReceiveWithAnyArgs().DeletePendingBySourceAsync(default!, default, default);
    }

    /// <summary>G4: a DAT-entry resolve keeps the DAT ROM name's extension, not <c>RomExtensions[0]</c>.</summary>
    [Test]
    public async Task Dat_UsesTheDatRomNamesExtension()
    {
        _dir.Dispose();
        Build(new Headerless(".gb", ".sms"));
        var bytes = new byte[] { 1, 2, 3 };
        _item = _item with
        {
            Reason = "NoDatMatch", Sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes)),
            Crc32 = RnRomManager.Core.Storage.Crc32.ToHex(bytes), Md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes)),
        };
        FixtureDatWriter.Write(_dats.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            ["GB"] = [new FixtureGame("Tide Runner (World)", "Tide Runner (World).sms", [9, 9, 9, 9])],
        });
        _dats.Load();

        var result = await _resolver.ResolveInJobAsync(1,
            new ReviewResolutionRequest { Action = "Dat", DatSystem = "gb", DatGameName = "Tide Runner (World)" }, Actor, default);

        Assert.That(result.Code, Is.Null, string.Join("; ", result.Errors?.SelectMany(e => e.Value) ?? []));
        var zip = Directory.GetFiles(_paths.Roms, "*.zip", SearchOption.AllDirectories).Single();
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.That(archive.Entries.Select(e => e.Name), Does.Contain("Tide Runner (World) (Unverified).sms"));
    }

    [TestCase("test.bin", "X (USA) (Unverified).bin", 1985)]
    [TestCase("test.a78", "X (USA) (Unverified).a78", 1984)]
    public async Task Dat_PrefersTheDatRomWithTheFilesExtension(string source, string stored, int year)
    {
        _dir.Dispose();
        Build(new Headerless(".a78", ".bin"));
        var bytes = new byte[] { 1, 2, 3 };
        var path = Path.Combine(_paths.Ingest, source);
        File.Move(_item.SourcePath, path);
        _item = _item with
        {
            SourcePath = path, OriginalFileName = source, Reason = "NoDatMatch",
            Sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes)),
            Crc32 = RnRomManager.Core.Storage.Crc32.ToHex(bytes), Md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes)),
        };
        FixtureDatWriter.Write(_dats.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            ["GB"] =
            [
                new FixtureGame("X (USA)", "X (USA).a78", [7, 7, 7, 7]) { ReleaseYear = 1984 },
                new FixtureGame("X (USA)", "X (USA).bin", [8, 8, 8, 8]) { ReleaseYear = 1985 },
            ],
        });
        _dats.Load();

        var result = await _resolver.ResolveInJobAsync(1,
            new ReviewResolutionRequest { Action = "Dat", DatSystem = "gb", DatGameName = "X (USA)" }, Actor, default);

        Assert.That(result.Code, Is.Null, string.Join("; ", result.Errors?.SelectMany(e => e.Value) ?? []));
        var zip = Directory.GetFiles(_paths.Roms, "*.zip", SearchOption.AllDirectories).Single();
        using var archive = LibraryZip.Open(zip, new InfoJsonReader([], new SystemRegistry([new Headerless(".a78", ".bin")])));
        var game = archive.InfoJson!.Game!;
        Assert.That((game.Roms.Single().FileName, game.ReleaseYear), Is.EqualTo((stored, (int?)year)));
    }
}
