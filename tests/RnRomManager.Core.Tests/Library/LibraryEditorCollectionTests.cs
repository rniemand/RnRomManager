using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Library;

/// <summary>
/// B18: collection memberships follow a merged game, or a game emptied by a release move, to the target. Also the
/// metadata a release moved to a new game takes from its DAT entry, by identification method.
/// </summary>
public class LibraryEditorCollectionTests
{
    private static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TargetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly AuditActor Actor = new(5, "boss", null);

    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;
    private IGameRepository _games = null!;
    private IRomRepository _roms = null!;
    private ILibraryMirror _mirror = null!;
    private ICollectionRepository _collections = null!;
    private LibraryEditor _editor = null!;
    private DatCache _dats = null!;
    private int _seed;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var handler = Substitute.For<ISystemHandler>();
        handler.EmbedRomsInZip.Returns(true);
        handler.Id.Returns("gb");
        handler.DatName.Returns("Nintendo - Game Boy");
        handler.RomExtensions.Returns([".gb"]);
        handler.DatFolder.Returns(DatKind.NoIntro);
        handler.MetadatKinds.Returns(DatKind.Metadat);
        handler.DefaultBoxArtPng.Returns(PngTests.Png(512, 512));
        var systems = Substitute.For<ISystemRegistry>();
        systems.All.Returns([handler]);
        systems.ById("gb").Returns(handler);
        _reader = new InfoJsonReader([], systems);
        _games = Substitute.For<IGameRepository>();
        _roms = Substitute.For<IRomRepository>();
        _mirror = Substitute.For<ILibraryMirror>();
        _collections = Substitute.For<ICollectionRepository>();
        var audit = Substitute.For<IAuditLog>();
        var paths = Options.Create(new PathsOptions { Roms = _dir.Path, Ingest = _dir.Combine("ingest"), Cache = _dir.Combine("cache") });
        var media = TestMedia.Offline();
        var retry = new GameMediaRetry(_mirror, _reader, systems, media, paths, audit, TimeProvider.System, NullLogger<GameMediaRetry>.Instance);
        _dats = new DatCache(paths, systems, NullLogger<DatCache>.Instance);
        _editor = new LibraryEditor(new JobLock(), _games, _roms, Substitute.For<IImageRepository>(), _mirror, _reader,
            _dats, systems, media, retry, paths, audit, new AuditPaths(paths),
            _collections, TimeProvider.System, NullLogger<LibraryEditor>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    /// <summary>Writes the game's ZIP with <paramref name="releases"/> ROMs and stubs its DB rows; returns its first ROM's row id.</summary>
    private async Task<long> SeedAsync(Guid id, string title, int releases, InfoJsonIdentification? identification = null)
    {
        var zipPath = $"gb/{title[0]}/{title}.zip";
        var bytes = Enumerable.Range(0, releases).Select(_ => RomBytes(++_seed)).ToArray();
        var roms = bytes.Select((b, i) => Rom($"{title} {i}.gb", b, isPrimary: i == 0) with
            {
                Identification = identification ?? new InfoJsonIdentification(IdentificationMethod.Manual),
            }).ToArray();
        var game = Game(title, roms) with { GameId = id };
        var abs = Path.Combine(_dir.Path, zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var entries = roms.Select((r, i) => (r, i)).ToDictionary(x => LibraryZip.RomEntry(x.r.FileName), x => (Func<Stream>)(() => new MemoryStream(bytes[x.i])));
        await new LibraryZipWriter(_reader).WriteAsync(abs, null, new LibraryZipChanges(game, entries));
        var rows = new MirrorEntry(game, zipPath, 1, DateTime.UtcNow).ToRows();
        _games.GetAsync(id, Arg.Any<CancellationToken>()).Returns(rows.Game);
        var romRow = rows.Roms[0] with { Id = 100 + _seed };
        _roms.GetAsync(id, romRow.Id, Arg.Any<CancellationToken>()).Returns(romRow);
        return romRow.Id;
    }

    [Test]
    public async Task Merge_MovesTheMemberships_AfterTheDbCommit()
    {
        await SeedAsync(SourceId, "Source", 1);
        await SeedAsync(TargetId, "Target", 1);

        var result = await _editor.MergeAsync(SourceId, TargetId, Actor, default);

        Assert.That(result.Code, Is.Null, result.Details);
        Received.InOrder(() =>
        {
            _mirror.ApplyAsync(Arg.Any<IReadOnlyList<MirrorEntry>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
            _collections.MoveGameAsync(SourceId, TargetId, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Merge_DbWriteFails_DoesNotMoveTheMemberships()
    {
        await SeedAsync(SourceId, "Source", 1);
        await SeedAsync(TargetId, "Target", 1);
        _mirror.ApplyAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        var result = await _editor.MergeAsync(SourceId, TargetId, Actor, default);

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.DbWriteFailed));
        await _collections.DidNotReceiveWithAnyArgs().MoveGameAsync(default, default, default);
    }

    [Test]
    public async Task Merge_MoveGameThrows_StillSucceeds()
    {
        await SeedAsync(SourceId, "Source", 1);
        await SeedAsync(TargetId, "Target", 1);
        _collections.MoveGameAsync(default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        var result = await _editor.MergeAsync(SourceId, TargetId, Actor, default);

        Assert.That((result.Code, result.GameId), Is.EqualTo(((string?)null, (Guid?)TargetId)));
    }

    [Test]
    public async Task MovingTheLastRelease_ToAnExistingGame_MovesTheMemberships()
    {
        var romId = await SeedAsync(SourceId, "Source", 1);
        await SeedAsync(TargetId, "Target", 1);

        var result = await _editor.MoveReleaseAsync(SourceId, romId, TargetId, null, Actor, default);

        Assert.That(result.Code, Is.Null, result.Details);
        await _collections.Received(1).MoveGameAsync(SourceId, TargetId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MovingTheLastRelease_ToANewGame_MovesTheMembershipsToIt()
    {
        var romId = await SeedAsync(SourceId, "Source", 1);

        var result = await _editor.MoveReleaseAsync(SourceId, romId, null, "Brand New", Actor, default);

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(result.GameId, Is.Not.Null.And.Not.EqualTo(SourceId));
        await _collections.Received(1).MoveGameAsync(SourceId, result.GameId!.Value, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MovingOneOfSeveralReleases_KeepsTheMemberships()
    {
        var romId = await SeedAsync(SourceId, "Source", 2);
        await SeedAsync(TargetId, "Target", 1);

        var result = await _editor.MoveReleaseAsync(SourceId, romId, TargetId, null, Actor, default);

        Assert.That(result.Code, Is.Null, result.Details);
        await _collections.DidNotReceiveWithAnyArgs().MoveGameAsync(default, default, default);
    }

    [Test]
    public async Task MovingTheLastRelease_DbWriteFails_DoesNotMoveTheMemberships()
    {
        var romId = await SeedAsync(SourceId, "Source", 1);
        await SeedAsync(TargetId, "Target", 1);
        _mirror.ApplyAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        var result = await _editor.MoveReleaseAsync(SourceId, romId, TargetId, null, Actor, default);

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.DbWriteFailed));
        await _collections.DidNotReceiveWithAnyArgs().MoveGameAsync(default, default, default);
    }

    /// <summary>B7: a <c>Serial</c> release's hashes are its <c>.wbfs</c> file's, so its DAT entry is found by <c>datGameName</c>, as for Manual.</summary>
    [TestCase(IdentificationMethod.Serial, 1995)]
    [TestCase(IdentificationMethod.Dat, null)]
    public async Task MovingToANewGame_FindsTheDatEntry_ByNameForSerial(IdentificationMethod method, int? expectedYear)
    {
        FixtureDatWriter.Write(_dats.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            ["Nintendo - Game Boy"] = [new FixtureGame("Disc Game (World)", "Disc Game (World).gb", RomBytes(999)) { ReleaseYear = 1995 }],
        });
        _dats.Load();
        var romId = await SeedAsync(SourceId, "Source", 2, new InfoJsonIdentification(method, "Nintendo - Game Boy", FixtureDatWriter.Version, "Disc Game (World)"));

        var result = await _editor.MoveReleaseAsync(SourceId, romId, null, "Brand New", Actor, default);

        Assert.That(result.Code, Is.Null, result.Details);
        using var zip = LibraryZip.Open(Path.Combine(_dir.Path, "gb", "B", "Brand New.zip"), _reader);
        Assert.That(zip.InfoJson!.Game!.ReleaseYear, Is.EqualTo(expectedYear), "Dat looks the entry up by the release's own SHA1, which isn't in the DAT");
    }
}
