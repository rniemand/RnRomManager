using System.Text.Json.Nodes;
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
using RnRomManager.Core.Naming;
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

    private IGameRepository _games = null!;
    private DatCache _dats = null!;
    private IRomRepository _roms = null!;
    private InfoJsonReader _reader = null!;

    private void Build(ISystemHandler? sibling = null)
    {
        _reviews = Substitute.For<IIngestReviewRepository>();
        _audit = Substitute.For<IAuditLog>();
        _reviews.GetAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _item);
        var systems = new SystemRegistry([new ExternalTestHandler(), .. sibling is null ? [] : new[] { sibling }]);
        var options = Options.Create(_paths);
        var dats = _dats = new DatCache(options, systems, NullLogger<DatCache>.Instance);
        var games = _games = Substitute.For<IGameRepository>();
        var media = TestMedia.Offline();
        var romRepository = _roms = Substitute.For<IRomRepository>();
        var reader = _reader = new InfoJsonReader([], systems);
        var pipeline = new IngestPipeline(dats, systems, romRepository, games, Substitute.For<ILibraryMirror>(),
            Substitute.For<IIntegrityIssueRepository>(), _reviews, reader, media, options, _audit,
            new AuditPaths(options), TimeProvider.System, NullLogger<IngestPipeline>.Instance);
        _resolver = new ReviewResolver(new JobLock(), _reviews, games, romRepository,
            new DbSync(games, romRepository, Substitute.For<ILibraryMirror>(), reader, systems, options, NullLogger<DbSync>.Instance), pipeline, dats, systems,
            new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance),
            new ReviewSets(new IngestFileScanner(options, systems, NullLogger<IngestFileScanner>.Instance), _reviews, systems, options, NullLogger<ReviewSets>.Instance),
            options, _audit, new AuditPaths(options), TimeProvider.System,
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
        await _reviews.DidNotReceiveWithAnyArgs().SetResolutionAsync(default(long), default!, default, default, default, default);
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
            ? await _resolver.ResolveInJobAsync(1, Manual, Actor, default)
            : await _resolver.DismissAsync(1, Actor, default);

        Assert.That(result.Code, Is.EqualTo("IoError"));
        await AssertPendingKeptAsync();
        Assert.That(Directory.Exists(Path.Combine(_ingest, "_failed")), Is.False, "nothing was moved");
    }

    [TestCase("Disc (Track 1).bin", "Resolve")]
    [TestCase("Disc (Track 1).BIN", "Dismiss")]
    [TestCase("Disc.cue", "Resolve")]
    [TestCase("Disc.cue", "Dismiss")]
    public async Task SetFileItem_CantBeResolvedOrDismissed_AndKeepsPending(string name, string action)
    {
        // B15: an unmatched .cue/.bin on a FileSets system can only be deleted.
        Build(new ExternalSetTestHandler());
        var folder = Directory.CreateDirectory(Path.Combine(_paths.Ingest, ExternalSetTestHandler.SystemId)).FullName;
        _item = _item with { SystemId = ExternalSetTestHandler.SystemId, SourcePath = Path.Combine(folder, name), OriginalFileName = name };
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]);

        var result = action == "Resolve" ? await _resolver.ResolveInJobAsync(1, Manual, Actor, default) : await _resolver.DismissAsync(1, Actor, default);

        Assert.That(result.Code, Is.EqualTo(ReviewResolver.SetFileNotResolvable));
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        await AssertPendingKeptAsync();
    }

    [Test]
    public async Task IsResolvableAsync_RefusesCueAndBinOnAFileSetsSystem_UnlessTheirSetIsFound_B12()
    {
        Build(new ExternalSetTestHandler());
        var folder = Directory.CreateDirectory(Path.Combine(_paths.Ingest, ExternalSetTestHandler.SystemId)).FullName;
        ReviewItem Item(string system, string name) => _item with { SystemId = system, OriginalFileName = name, SourcePath = Path.Combine(folder, name) };
        File.WriteAllBytes(Path.Combine(folder, "x.bin"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "y.cue"), [1]);

        Assert.That(await _resolver.IsResolvableAsync(Item(ExternalSetTestHandler.SystemId, "x.bin")), Is.False, "a lone .bin");
        Assert.That(await _resolver.IsResolvableAsync(Item(ExternalSetTestHandler.SystemId, "y.cue")), Is.False, "a cue naming nothing here");
        Assert.That(await _resolver.IsResolvableAsync(Item(ExternalSetTestHandler.SystemId, "x.iso")), Is.True);
        Assert.That(await _resolver.IsResolvableAsync(Item(ExternalSetTestHandler.SystemId, "x.nrg")), Is.True);
        Assert.That(await _resolver.IsResolvableAsync(Item(ExternalTestHandler.SystemId, "x.bin")), Is.True);

        await DiscSetAsync();
        Assert.That(await _resolver.IsResolvableAsync(_rows[2]), Is.True, "a complete Pending NoDatMatch set's member");
    }

    [Test]
    public async Task ConfirmedMissingSource_ResolveIsSourceMissing_AndDropsTheRow()
    {
        var result = await _resolver.ResolveInJobAsync(1, Manual, Actor, default);

        Assert.That(result.Code, Is.EqualTo("SourceMissing"));
        await _reviews.Received(1).DeleteAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 1 })), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PresentSource_WithOtherBytesThanTheItem_IsVerifyFailed_AndKeepsPending()
    {
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]); // the item's SHA1 is all zeros

        var result = await _resolver.ResolveInJobAsync(1, Manual, Actor, default);

        Assert.That(result.Code, Is.EqualTo("VerifyFailed"));
        Assert.That(File.Exists(_item.SourcePath), Is.True);
        await AssertPendingKeptAsync();
    }

    /// <summary>
    /// A <see cref="ExternalSetTestHandler"/> game "My Game" whose only (primary) release is disc 1, and a Pending
    /// <c>NoDatMatch</c> item for <paramref name="name"/> in its ingest folder; grouping finds the game.
    /// </summary>
    private async Task<(Guid GameId, string ZipPath)> DiscGameAsync(string name)
    {
        Build(new ExternalSetTestHandler());
        var disc1 = TestLibrary.Rom("My Game (Europe) (Disc 1).iso", TestLibrary.RomBytes(1), regions: ["Europe"]) with
        {
            BaseTitle = "My Game", Languages = ["En", "Fr"], Revision = "Rev 1", Flags = ["Disc 1"], ExternalFileName = "My Game (Europe) (Disc 1).iso",
        };
        var game = TestLibrary.Game("My Game", disc1) with { System = ExternalSetTestHandler.SystemId };
        var zipPath = $"{ExternalSetTestHandler.SystemId}/{Bucket.For("My Game.zip")}/My Game.zip";
        var zip = Path.Combine(_paths.Roms, zipPath);
        await TestLibrary.WriteZipAsync(_reader, zip, game);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(zip)!, disc1.ExternalFileName!), TestLibrary.RomBytes(1));
        _games.GetAsync(game.GameId, Arg.Any<CancellationToken>()).Returns(new GameRow
        {
            Id = game.GameId, SystemId = game.System, Title = game.Title, DisplayTitle = game.Title, ZipPath = zipPath,
            Bucket = Bucket.For("My Game.zip"), Developers = [], Publishers = [], Genres = [], ZipSizeBytes = 0, ZipMtimeUtc = default,
            InfoSchemaVersion = InfoJsonReader.CurrentVersion, CreatedAt = default, UpdatedAt = default,
        });
        _roms.ListGameIdsByBaseTitleAsync(default!, default!, default).ReturnsForAnyArgs([game.GameId]);
        _roms.ListExternalAsync(default).ReturnsForAnyArgs([]);

        var folder = Directory.CreateDirectory(Path.Combine(_paths.Ingest, ExternalSetTestHandler.SystemId)).FullName;
        var bytes = TestLibrary.RomBytes(2);
        _item = _item with
        {
            SystemId = ExternalSetTestHandler.SystemId, SourcePath = Path.Combine(folder, name), OriginalFileName = name,
            Sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes)), SizeBytes = bytes.Length,
            Crc32 = RnRomManager.Core.Storage.Crc32.ToHex(bytes), Md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes)),
        };
        File.WriteAllBytes(_item.SourcePath, bytes);
        return (game.GameId, zipPath);
    }

    private const string SetCue = "Mystery (Disc 2).cue";
    private const string SetTrack1 = "Mystery (Disc 2) (Track 1).bin";
    private const string SetTrack2 = "Mystery (Disc 2) (Track 2).bin";
    private Dictionary<long, ReviewItem> _rows = null!;

    /// <summary>
    /// <see cref="DiscGameAsync"/>'s game, and an unmatched set (a cue and 2 tracks) in ingest whose rows 1–3 are
    /// Pending <c>NoDatMatch</c>; the item is the cue (row 1).
    /// </summary>
    private async Task<(Guid GameId, string ZipPath)> DiscSetAsync()
    {
        var (gameId, zipPath) = await DiscGameAsync(SetCue);
        var folder = Path.GetDirectoryName(_item.SourcePath)!;
        var files = new (string Name, byte[] Bytes)[]
        {
            (SetCue, System.Text.Encoding.UTF8.GetBytes($"FILE \"{SetTrack1}\" BINARY\n  TRACK 01 MODE1/2352\nFILE \"{SetTrack2}\" BINARY\n  TRACK 02 AUDIO\n")),
            (SetTrack1, TestLibrary.RomBytes(3)),
            (SetTrack2, TestLibrary.RomBytes(4)),
        };
        _rows = [];
        for (var i = 0; i < files.Length; i++)
        {
            var (name, bytes) = files[i];
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            _rows[i + 1] = _item with
            {
                Id = i + 1, SourcePath = path, OriginalFileName = name, SizeBytes = bytes.Length,
                Sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes)),
            };
        }
        _item = _rows[1];
        _reviews.GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c => _rows.GetValueOrDefault(c.Arg<long>()));
        _reviews.ListPendingByPathsAsync(default!, default).ReturnsForAnyArgs(c =>
            _rows.Values.Where(r => c.Arg<IReadOnlyCollection<string>>().Contains(r.SourcePath)).ToList());
        return (gameId, zipPath);
    }

    private static readonly ReviewResolutionRequest AttachDisc2 = new() { Action = "Attach", Flag = "Disc", Disc = 2 };

    private void AssertSetStaysInIngest()
    {
        Assert.That(_rows.Values.All(r => File.Exists(r.SourcePath)), Is.True, "every member stays in ingest");
        _reviews.DidNotReceiveWithAnyArgs().SetResolutionAsync(default(IReadOnlyList<long>)!, default!, default, default, default, default);
    }

    [Test]
    public async Task ManualWithDisc2_AddsTheWholeSetAsOneRelease_OfTheDisc1Game_B14()
    {
        var (gameId, zipPath) = await DiscSetAsync();

        var result = await _resolver.ResolveInJobAsync(1, Manual with { Regions = ["Europe"], Disc = 2 }, Actor, new JobProgress());

        Assert.That(result.Code, Is.Null, string.Join("; ", result.Errors?.SelectMany(e => e.Value) ?? []));
        Assert.That(result.GameId, Is.EqualTo(gameId));
        Assert.That(result.Items.Select(r => r.Id), Is.EqualTo(new long[] { 1, 2, 3 }));
        var folder = Path.GetDirectoryName(Path.Combine(_paths.Roms, zipPath))!;
        Assert.That(new[] { SetCue, SetTrack1, SetTrack2 }.All(n => File.Exists(Path.Combine(folder, n))), Is.True, "names kept");
        var rom = StoredRelease(zipPath, SetTrack1);
        Assert.That((rom.BaseTitle, rom.Flags.Single(), rom.Regions.Single()), Is.EqualTo(("My Game", "Disc 2", "Europe")));
        Assert.That(rom.Files!.Select(f => f.ExternalFileName), Is.EqualTo(new[] { SetCue, SetTrack2 }));
        await _reviews.Received(1).SetResolutionAsync(Arg.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new long[] { 1, 2, 3 })), "Resolved",
            gameId, 42, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AttachDisc_OnASet_CopiesThePrimaryReleaseTags_B14()
    {
        var (gameId, zipPath) = await DiscSetAsync();

        var result = await _resolver.ResolveInJobAsync(2, AttachDisc2 with { TargetGameId = gameId }, Actor, new JobProgress());

        Assert.That(result.Code, Is.Null, string.Join("; ", result.Errors?.SelectMany(e => e.Value) ?? []));
        var rom = StoredRelease(zipPath, SetTrack1);
        Assert.That((rom.Regions.Single(), rom.Revision, rom.Flags.Single()), Is.EqualTo(("Europe", "Rev 1", "Disc 2")));
        Assert.That(rom.Languages, Is.EqualTo(new[] { "En", "Fr" }));
    }

    [Test]
    public async Task AMemberChangedSinceReview_IsVerifyFailed_AndEveryRowStaysPending_B14()
    {
        await DiscSetAsync();
        _rows[3] = _rows[3] with { Sha1 = new string('0', 40) };

        var result = await _resolver.ResolveInJobAsync(1, Manual with { Disc = 2 }, Actor, new JobProgress());

        Assert.That(result.Code, Is.EqualTo("VerifyFailed"));
        AssertSetStaysInIngest();
    }

    [Test]
    public async Task ASetMadeIncomplete_IsSetFileNotResolvable_AndNothingMoves_B14()
    {
        await DiscSetAsync();
        File.Delete(_rows[3].SourcePath);

        var result = await _resolver.ResolveInJobAsync(1, Manual with { Disc = 2 }, Actor, new JobProgress());

        Assert.That(result.Code, Is.EqualTo(ReviewResolver.SetFileNotResolvable));
        Assert.That(result.Items.Select(r => r.Id), Is.EqualTo(new long[] { 1 }), "no set: one job item, for the item");
        Assert.That(File.Exists(_rows[1].SourcePath) && File.Exists(_rows[2].SourcePath), Is.True);
    }

    [Test]
    public async Task TheRowsWriteFailing_IsDbWriteFailed_ThenMembersAreDeleteOnly_OneAtATime_B14()
    {
        var (gameId, _) = await DiscSetAsync();
        _reviews.SetResolutionAsync(default(IReadOnlyList<long>)!, default!, default, default, default, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        var result = await _resolver.ResolveInJobAsync(1, Manual with { Disc = 2 }, Actor, new JobProgress());

        Assert.That(result.Code, Is.EqualTo("DbWriteFailed"));
        Assert.That(_rows.Values.Any(r => File.Exists(r.SourcePath)), Is.False, "the release is in the library");
        Assert.That((await _resolver.ResolveInJobAsync(2, Manual with { Disc = 2 }, Actor, new JobProgress())).Code,
            Is.EqualTo(ReviewResolver.SetFileNotResolvable));
        Assert.That((await _resolver.DeleteSourceAsync(2, Actor, default)).Code, Is.Null);
        await _reviews.Received(1).DeletePendingBySourceAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _reviews.Received(1).DeletePendingBySourceAsync(_rows[2].SourcePath, null, Arg.Any<CancellationToken>());
    }

    private ReviewResolutionRequest SetDatPick(string system = ExternalSetTestHandler.SystemId)
    {
        FixtureDatWriter.Write(_dats.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [ExternalSetTestHandler.DatNameValue] = [new FixtureGame("My Game (Japan) (Disc 2)", "My Game (Japan) (Disc 2) (Track 1).bin", [7, 7, 7])],
        }, layouts: new Dictionary<string, FixtureDatLayout> { [ExternalSetTestHandler.DatNameValue] = FixtureDatLayout.Redump });
        _dats.Load();
        return new ReviewResolutionRequest { Action = "Dat", DatSystem = system, DatGameName = "My Game (Japan) (Disc 2)" };
    }

    [Test]
    public async Task ADatPickOnASet_IsNamedByTheDatGame_Unverified_AndKeepsTheFileNames_B14()
    {
        var (gameId, zipPath) = await DiscSetAsync();

        var result = await _resolver.ResolveInJobAsync(1, SetDatPick(), Actor, new JobProgress());

        Assert.That(result.Code, Is.Null, string.Join("; ", result.Errors?.SelectMany(e => e.Value) ?? []));
        Assert.That(result.GameId, Is.EqualTo(gameId), "grouped with the game that owns the base title");
        var rom = StoredRelease(zipPath, SetTrack1);
        Assert.That((rom.BaseTitle, rom.Regions.Single()), Is.EqualTo(("My Game", "Japan")));
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Disc 2", "Unverified" }));
        Assert.That(rom.Identification, Is.EqualTo(new InfoJsonIdentification(IdentificationMethod.Manual, ExternalSetTestHandler.DatNameValue,
            FixtureDatWriter.Version, "My Game (Japan) (Disc 2)")));
        Assert.That(rom.Files!.Select(f => f.ExternalFileName), Is.EqualTo(new[] { SetCue, SetTrack2 }));
    }

    [Test]
    public async Task ADatPickOnASet_WhoseMemberDoesntFitTheSystem_IsAValidationErrorOnDatSystem_B14()
    {
        await DiscSetAsync();
        var request = SetDatPick();
        _rows[3] = _rows[3] with { SizeBytes = new ExternalSetTestHandler().MaxRomSizeBytes + 1 };

        var result = await _resolver.ResolveInJobAsync(1, request, Actor, new JobProgress());

        Assert.That(result.Code, Is.EqualTo("ValidationFailed"));
        Assert.That(result.Errors!.Keys, Is.EqualTo(new[] { "datSystem" }));
        AssertSetStaysInIngest();
    }

    private InfoJsonRom StoredRelease(string zipPath, string fileName)
    {
        var target = LibraryTarget.Open(_paths.Roms, zipPath, _reader);
        using (target.Zip)
            return target.Game!.Roms.Single(r => r.FileName == fileName);
    }

    private async Task AssertAddedAsync(ReviewResolutionResult result, Guid gameId, string zipPath, string fileName)
    {
        Assert.That(result.Code, Is.Null, string.Join("; ", result.Errors?.SelectMany(e => e.Value) ?? []));
        Assert.That(result.GameId, Is.EqualTo(gameId));
        Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(Path.Combine(_paths.Roms, zipPath))!, fileName)), Is.True, "moved next to the ZIP");
        Assert.That(File.Exists(_item.SourcePath), Is.False);
        await _reviews.Received(1).SetResolutionAsync(1, "Resolved", gameId, 42, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [TestCase("Serial", IdentificationMethod.Serial)]
    [TestCase(null, IdentificationMethod.Dat)]
    public async Task AttachNone_PreservesTheQueuedIdentificationMethod(string? method, IdentificationMethod expected)
    {
        var (gameId, zipPath) = await DiscGameAsync("dump.iso");
        const string fileName = "My Game (Europe) (Disc 2).iso";
        var identity = new JsonObject
        {
            ["datName"] = "Fixture DAT", ["datVersion"] = "fixture-1", ["datGameName"] = "My Game (Europe) (Disc 2)",
            ["romName"] = fileName,
        };
        if (method is not null)
            identity["method"] = method;
        _item = _item with
        {
            Reason = "AmbiguousGroup", DatIdentification = identity.ToJsonString(), CandidateGameIds = $"[\"{gameId}\"]",
        };

        var request = new ReviewResolutionRequest { Action = "Attach", Flag = "None", TargetGameId = gameId };
        var result = await _resolver.ResolveInJobAsync(1, request, Actor, default);

        await AssertAddedAsync(result, gameId, zipPath, fileName);
        Assert.That(StoredRelease(zipPath, fileName).Identification.Method, Is.EqualTo(expected));
    }

    [Test]
    public async Task ManualWithDisc2_NextToDisc1_IsAddedToThatGame_B10()
    {
        var (gameId, zipPath) = await DiscGameAsync("dump.iso");

        var result = await _resolver.ResolveInJobAsync(1, Manual with { Regions = ["Europe"], Disc = 2 }, Actor, default);

        await AssertAddedAsync(result, gameId, zipPath, "My Game (Europe) (Disc 2).iso");
        Assert.That(StoredRelease(zipPath, "My Game (Europe) (Disc 2).iso").Flags, Is.EqualTo(new[] { "Disc 2" }));
    }

    [Test]
    public async Task ManualWithDisc_ReplacesATypedDiscFlag_AndAppendsItLast_B10()
    {
        var (gameId, zipPath) = await DiscGameAsync("dump.iso");

        var result = await _resolver.ResolveInJobAsync(1, Manual with { Flags = ["disc 3", "Beta"], Disc = 2 }, Actor, default);

        await AssertAddedAsync(result, gameId, zipPath, "My Game (Beta) (Disc 2).iso");
        Assert.That(StoredRelease(zipPath, "My Game (Beta) (Disc 2).iso").Flags, Is.EqualTo(new[] { "Beta", "Disc 2" }));
    }

    [Test]
    public async Task AttachDisc_CopiesThePrimaryReleaseTags_AndKeepsTheExtension_B11()
    {
        var (gameId, zipPath) = await DiscGameAsync("dump.iso");
        var request = new ReviewResolutionRequest { Action = "Attach", Flag = "Disc", Disc = 2, TargetGameId = gameId };

        var result = await _resolver.ResolveInJobAsync(1, request, Actor, default);

        await AssertAddedAsync(result, gameId, zipPath, "dump (Disc 2).iso");
        var rom = StoredRelease(zipPath, "dump (Disc 2).iso");
        Assert.That(rom.BaseTitle, Is.EqualTo("My Game"));
        Assert.That(rom.Regions, Is.EqualTo(new[] { "Europe" }));
        Assert.That(rom.Languages, Is.EqualTo(new[] { "En", "Fr" }));
        Assert.That(rom.Revision, Is.EqualTo("Rev 1"));
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Disc 2" }));
        Assert.That(rom.Identification.Method, Is.EqualTo(IdentificationMethod.Manual));
    }

    [Test]
    public async Task AttachDisc_DoesNotDoubleADiscTagInTheStem_B11()
    {
        var (gameId, zipPath) = await DiscGameAsync("My Game (disc 2).iso");
        var request = new ReviewResolutionRequest { Action = "Attach", Flag = "Disc", Disc = 2, TargetGameId = gameId };

        var result = await _resolver.ResolveInJobAsync(1, request, Actor, default);

        await AssertAddedAsync(result, gameId, zipPath, "My Game (disc 2).iso");
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

        var result = await _resolver.ResolveInJobAsync(1, request, Actor, default);

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

        var result = await _resolver.ResolveInJobAsync(1, new ReviewResolutionRequest { Action = "Dat", DatSystem = "ext2", DatGameName = "Any" }, Actor, default);

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
