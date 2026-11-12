using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Audit;
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

/// <summary>Spec I4: Review's Resolve, Dismiss and Delete classify an external source before acting on it.</summary>
public class ReviewResolverExternalTests
{
    private static readonly AuditActor Actor = new(42, "boss", null);
    private static readonly ReviewResolutionRequest Manual = new() { Action = "Manual", Title = "My Game", Regions = [], Languages = [], Flags = [] };

    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private IIngestReviewRepository _reviews = null!;
    private IAuditLog _audit = null!;
    private ReviewResolver _resolver = null!;
    private ReviewItem _item = null!;
    private string _ingest = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Ingest = _dir.Combine("ingest"), Roms = _dir.Combine("roms"), Cache = _dir.Combine("cache") };
        _ingest = Directory.CreateDirectory(Path.Combine(_paths.Ingest, ExternalTestHandler.SystemId)).FullName;
        _item = new ReviewItem
        {
            Id = 1, SystemId = ExternalTestHandler.SystemId, Status = "Pending", Reason = "NoDatMatch",
            SourcePath = Path.Combine(_ingest, "game.nsp"), OriginalFileName = "game.nsp", Sha1 = new string('0', 40), SizeBytes = 3,
        };
        Build();
    }

    private void Build(ISystemHandler? sibling = null)
    {
        _reviews = Substitute.For<IIngestReviewRepository>();
        _audit = Substitute.For<IAuditLog>();
        _reviews.GetAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _item);
        var systems = new SystemRegistry([new ExternalTestHandler(), .. sibling is null ? [] : new[] { sibling }]);
        var options = Options.Create(_paths);
        var dats = new DatCache(options, systems, NullLogger<DatCache>.Instance);
        var games = Substitute.For<IGameRepository>();
        var media = TestMedia.Offline();
        var pipeline = new IngestPipeline(dats, systems, Substitute.For<IRomRepository>(), games, Substitute.For<ILibraryMirror>(),
            Substitute.For<IIntegrityIssueRepository>(), _reviews, new InfoJsonReader([], systems), media, options, _audit,
            new AuditPaths(options), TimeProvider.System, NullLogger<IngestPipeline>.Instance);
        var romRepository = Substitute.For<IRomRepository>();
        var reader = new InfoJsonReader([], systems);
        _resolver = new ReviewResolver(new JobLock(), _reviews, games, romRepository,
            new DbSync(games, romRepository, Substitute.For<ILibraryMirror>(), reader, systems, options, NullLogger<DbSync>.Instance), pipeline, dats, systems,
            new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance), options, _audit, new AuditPaths(options), TimeProvider.System,
            NullLogger<ReviewResolver>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    /// <summary>Puts something that isn't a regular file at the source path; false when this platform can't.</summary>
    private bool MakeUnsafe(string kind) => kind switch
    {
        "directory" => Directory.CreateDirectory(_item.SourcePath).Exists,
        "dangling link" => TestLinks.TryLinkFile(_item.SourcePath, _dir.Combine("nowhere.nsp")),
        _ => TestLinks.TryCreateFifo(_item.SourcePath),
    };

    private async Task AssertPendingKeptAsync()
    {
        await _reviews.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        await _reviews.DidNotReceiveWithAnyArgs().SetResolutionAsync(default, default!, default, default, default, default);
        await _reviews.DidNotReceiveWithAnyArgs().DeletePendingBySourceAsync(default!, default, default);
    }

    [TestCase("directory", "Resolve")]
    [TestCase("directory", "Dismiss")]
    [TestCase("dangling link", "Resolve")]
    [TestCase("dangling link", "Dismiss")]
    [TestCase("fifo", "Resolve")]
    [TestCase("fifo", "Dismiss")]
    public async Task UnsafeSource_IsAnIoError_AndKeepsPending(string kind, string action)
    {
        if (!MakeUnsafe(kind))
            Assert.Ignore($"this platform or account can't create a {kind}");

        var result = action == "Resolve"
            ? await _resolver.ResolveAsync(1, Manual, Actor, default)
            : await _resolver.DismissAsync(1, Actor, default);

        Assert.That(result.Code, Is.EqualTo("IoError"));
        await AssertPendingKeptAsync();
        Assert.That(Directory.Exists(Path.Combine(_ingest, "_failed")), Is.False, "nothing was moved");
    }

    [Test]
    public async Task ConfirmedMissingSource_ResolveIsSourceMissing_AndDropsTheRow()
    {
        var result = await _resolver.ResolveAsync(1, Manual, Actor, default);

        Assert.That(result.Code, Is.EqualTo("SourceMissing"));
        await _reviews.Received(1).DeleteAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 1 })), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PresentSource_WithOtherBytesThanTheItem_IsVerifyFailed_AndKeepsPending()
    {
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]); // the item's SHA1 is all zeros

        var result = await _resolver.ResolveAsync(1, Manual, Actor, default);

        Assert.That(result.Code, Is.EqualTo("VerifyFailed"));
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        await AssertPendingKeptAsync();
    }

    /// <summary>A same-family sibling of <see cref="ExternalTestHandler"/> with its own extensions and size cap.</summary>
    private sealed class Sibling(IReadOnlyList<string> extensions, long maxBytes) : ISystemHandler
    {
        public string Id => "ext2";
        public string DisplayName => "External Sibling";
        public string Family => ExternalTestHandler.SystemId;
        public IReadOnlyList<string> RomExtensions => extensions;
        public long MinRomSizeBytes => 1;
        public long MaxRomSizeBytes => maxBytes;
        public string DatName => "Test - External Sibling";
        public bool EmbedRomsInZip => false;
        public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;
        public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
    }

    [Test]
    public async Task UpdatePick_OnANonTitleDbSystem_IsAnUpdateError_AndKeepsPending_B8()
    {
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]);
        var request = new ReviewResolutionRequest { Action = "Dat", DatSystem = ExternalTestHandler.SystemId, DatGameName = "Any", Update = true };

        var result = await _resolver.ResolveAsync(1, request, Actor, default);

        Assert.That((result.Code, result.Errors?.Keys.Single()), Is.EqualTo(("ValidationFailed", (string?)"update")));
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        await AssertPendingKeptAsync();
    }

    [TestCase(false, 64L, TestName = "DatPick_OfASiblingThatCantStoreTheFile_IsAValidationError extension")]
    [TestCase(true, 2L, TestName = "DatPick_OfASiblingThatCantStoreTheFile_IsAValidationError size")]
    public async Task DatPick_OfASiblingThatCantStoreTheFile_IsAValidationError(bool takesXci, long maxBytes)
    {
        Build(new Sibling(takesXci ? [".nsp", ".xci"] : [".nsp"], maxBytes));
        _item = _item with { SourcePath = Path.Combine(_ingest, "game.xci"), OriginalFileName = "game.xci" };
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]);

        var result = await _resolver.ResolveAsync(1, new ReviewResolutionRequest { Action = "Dat", DatSystem = "ext2", DatGameName = "Any" }, Actor, default);

        Assert.That(result.Code, Is.EqualTo("ValidationFailed"));
        Assert.That(result.Errors!.Keys, Is.EqualTo(new[] { "datSystem" }));
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        await AssertPendingKeptAsync();
    }

    [Test]
    public async Task Delete_ThroughALinkedAncestor_IsDeleteFailed_AndKeepsEverything()
    {
        var outside = Directory.CreateDirectory(_dir.Combine("outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "game.nsp"), [1, 2, 3]);
        var link = Path.Combine(_ingest, "linked");
        if (!TestLinks.TryLinkDirectory(link, outside))
            Assert.Ignore("this account can't create a directory link");
        _item = _item with { SourcePath = Path.Combine(link, "game.nsp") };
        try
        {
            var result = await _resolver.DeleteSourceAsync(1, Actor, default);

            Assert.That(result.Code, Is.EqualTo("DeleteFailed"));
            Assert.That(File.Exists(Path.Combine(outside, "game.nsp")), Is.True);
            await AssertPendingKeptAsync();
            _audit.DidNotReceiveWithAnyArgs().Record(default!);
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(link);
        }
    }

    [Test]
    public async Task Delete_OfAConfirmedAbsentSource_ClearsItsPendingRows_WithoutAnAudit()
    {
        var result = await _resolver.DeleteSourceAsync(1, Actor, default);

        Assert.That(result.Code, Is.Null);
        await _reviews.Received(1).DeletePendingBySourceAsync(_item.SourcePath, null, Arg.Any<CancellationToken>());
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
    }

    [Test]
    public async Task AbortedRequest_AfterDismissMovedTheFile_StillMarksTheRowDismissed()
    {
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]);
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var result = await _resolver.DismissAsync(1, Actor, aborted.Token);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.Exists(Path.Combine(_ingest, "_failed", "game.nsp")), Is.True);
        await _reviews.Received(1).SetResolutionAsync(1, "Dismissed", null, 42, Arg.Any<DateTime>(), CancellationToken.None);
    }

    [Test]
    public async Task AbortedRequest_AfterDeleteRemovedTheFile_StillClearsThePendingRows()
    {
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]);
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var result = await _resolver.DeleteSourceAsync(1, Actor, aborted.Token);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.Exists(_item.SourcePath), Is.False);
        await _reviews.Received(1).DeletePendingBySourceAsync(_item.SourcePath, null, CancellationToken.None);
        _audit.Received(1).Record(Arg.Is<AuditEvent>(e => e.Action == AuditActions.ReviewDeleted));
    }
}
