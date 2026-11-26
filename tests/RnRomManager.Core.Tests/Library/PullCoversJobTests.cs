using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Admin actions P2–P5: the <c>PullCovers</c> job against temp ZIPs, with IGDB and the database substituted.</summary>
public class PullCoversJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 15, 30, 456, TimeSpan.Zero);
    private const string NowText = "2026-11-26T10:15:30.0000000+00:00";
    private static readonly byte[] Cover = PngTests.Png(600, 800);
    private static readonly byte[] Placeholder = PngTests.Png(512, 512);

    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;
    private ISystemRegistry _systems = null!;
    private ISystemHandler _gb = null!;
    private ILibraryMirror _mirror = null!;
    private IGameRepository _games = null!;
    private IIgdbGames _igdb = null!;
    private IJobItemRepository _items = null!;
    private IIntegrityIssueRepository _issues = null!;
    private IAuditLog _audit = null!;
    private List<JobItem> _written = null!;
    private int _seed;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _gb = TestHandlers.Fake("gb", embedRomsInZip: true);
        _systems = Substitute.For<ISystemRegistry>();
        _systems.All.Returns([_gb]);
        _systems.ById("gb").Returns(_gb);
        _reader = new InfoJsonReader([], _systems);
        _mirror = Substitute.For<ILibraryMirror>();
        _games = Substitute.For<IGameRepository>();
        _igdb = Substitute.For<IIgdbGames>();
        _igdb.DownloadCoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new IgdbCoverDownload(Cover, 200, null));
        _items = Substitute.For<IJobItemRepository>();
        _written = [];
        _items.InsertAsync(Arg.Do<JobItem>(_written.Add), Arg.Any<CancellationToken>()).Returns(1L);
        _issues = Substitute.For<IIntegrityIssueRepository>();
        _audit = Substitute.For<IAuditLog>();
        _seed = 0;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private PullCoversJob Job() => new(_games, _igdb, _mirror, _reader, _systems, _items, _issues,
        Options.Create(new PathsOptions { Roms = _dir.Path }), _audit, new FakeTimeProvider(Now), NullLogger<PullCoversJob>.Instance);

    private static InfoJsonImage Image(
        string name,
        byte[] bytes,
        bool cover,
        bool isDefault = false) => new()
    {
        FileName = name,
        Type = ImageType.BoxArt,
        Source = isDefault ? InfoJsonImage.SourceDefault : InfoJsonImage.SourceUpload,
        IsDefault = isDefault,
        IsCover = cover,
        SizeBytes = bytes.Length,
        Sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)),
        Width = 1,
        Height = 1,
        DateAdded = When,
    };

    private async Task<GameRow> Add(string title, int? igdbId, params (InfoJsonImage Image, byte[] Bytes)[] images) =>
        await Add(title, igdbId, g => g, images);

    private async Task<GameRow> Add(
        string title,
        int? igdbId,
        Func<InfoJsonGame, InfoJsonGame> edit,
        params (InfoJsonImage Image, byte[] Bytes)[] images)
    {
        var rom = RomBytes(++_seed);
        var info = Rom($"{title}.gb", rom);
        var game = edit(Game(title, info) with { IgdbId = igdbId, Images = images.Select(i => i.Image).ToList() });
        var zipPath = $"gb/{title[0]}/{title}.zip";
        var path = _dir.Combine("gb", title[..1], title + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(info.FileName)] = () => new MemoryStream(rom) };
        foreach (var (image, bytes) in images)
            entries[LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(bytes);
        await new LibraryZipWriter(_reader).WriteAsync(path, null, new LibraryZipChanges(game, entries));
        return new MirrorEntry(game, zipPath, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)).ToRows().Game;
    }

    private InfoJsonGame Read(GameRow row)
    {
        using var zip = LibraryZip.Open(_dir.Combine(row.ZipPath.Split('/')), _reader);
        return zip.InfoJson!.Game!;
    }

    private DateTime Mtime(GameRow row) => File.GetLastWriteTimeUtc(_dir.Combine(row.ZipPath.Split('/')));

    private void Igdb(params IgdbGameRating[] games) =>
        _igdb.GetGamesAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>()).Returns(games);

    private async Task<string> Run(params GameRow[] candidates)
    {
        _games.ListPullCoverCandidatesAsync(Arg.Any<CancellationToken>()).Returns(candidates);
        return JsonSerializer.Serialize(await Job().RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None));
    }

    private static string Summary(
        int pulled = 0,
        int noCover = 0,
        int imageLimit = 0,
        int skipped = 0,
        int error = 0) =>
        JsonSerializer.Serialize(new { pulled, noCover, imageLimit, skipped, error });

    [Test]
    public async Task Pulled_ReplacesAnUploadedCover_RecordsTheAction_MirrorsAndAudits()
    {
        var old = PngTests.Png(10, 10);
        var row = await Add("Tetris", 42, (Image("boxart-01.png", old, cover: true), old));
        Igdb(new IgdbGameRating(42, "co1abc", null, null));

        Assert.That(await Run(row), Is.EqualTo(Summary(pulled: 1)));

        var game = Read(row);
        Assert.That(game.Actions?.CoverImagePulled, Is.EqualTo(NowText));
        Assert.That(game.Images.Select(i => (i.FileName, i.IsCover)), Is.EqualTo(new[] { ("boxart-01.png", false), ("boxart-02.png", true) }));
        var pulled = game.Images[1];
        Assert.That((pulled.Source, pulled.SourceUrl, pulled.Width, pulled.Height, pulled.Sha1),
            Is.EqualTo((InfoJsonImage.SourceUpload, "https://images.igdb.com/igdb/image/upload/t_1080p/co1abc.jpg", 600, 800, Convert.ToHexStringLower(SHA1.HashData(Cover)))));
        await _mirror.Received(1).ReplaceAsync(Arg.Is<MirrorEntry>(e => e.Info.Actions!.CoverImagePulled == NowText), Arg.Any<CancellationToken>());
        _audit.Received(1).Record(Arg.Is<AuditEvent>(e => e.Action == AuditActions.MediaFetched && e.Details == "IGDB cover boxart-02.png"));
        await _issues.DidNotReceiveWithAnyArgs().ResolveOpenAsync(default!, default!, default, default);
        Assert.That(_written.Single(), Is.EqualTo(_written.Single() with
        {
            FileId = row.Id, GameId = row.Id, SystemId = "gb", SourcePath = "roms/gb/T/Tetris.zip", Outcome = "Pulled", Reason = null, Details = null,
        }));
    }

    [Test]
    public async Task Pulled_OverThePlaceholder_RemovesIt_AndResolvesTheBoxArtIssues()
    {
        var row = await Add("Tetris", 42, (Image("boxart-01.png", Placeholder, cover: true, isDefault: true), Placeholder));
        Igdb(new IgdbGameRating(42, "co1", null, null));

        Assert.That(await Run(row), Is.EqualTo(Summary(pulled: 1)));

        Assert.That(Read(row).Images.Select(i => (i.FileName, i.IsCover, i.IsDefault)), Is.EqualTo(new[] { ("boxart-01.png", true, false) }));
        await _issues.Received(1).ResolveOpenAsync(row.ZipPath, IntegrityIssueType.DefaultBoxArt, Now.UtcDateTime, Arg.Any<CancellationToken>());
        await _issues.Received(1).ResolveOpenAsync(row.ZipPath, IntegrityIssueType.MissingBoxArt, Now.UtcDateTime, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Pulled_NoImages_IsTheFirstCover_AndAFailingResolveOnlyWarns()
    {
        var row = await Add("Tetris", 42);
        Igdb(new IgdbGameRating(42, "co1", null, null));
        _issues.ResolveOpenAsync(default!, default!, default, default).ReturnsForAnyArgs(Task.FromException<int>(new InvalidOperationException("db")));

        Assert.That(await Run(row), Is.EqualTo(Summary(pulled: 1)));
        Assert.That(Read(row).Images.Single().IsCover, Is.True);
    }

    [Test]
    public async Task SameSha1_AlreadyInTheZip_BecomesTheCover_NoNewImage()
    {
        var other = PngTests.Png(10, 10);
        var row = await Add("Tetris", 42, (Image("boxart-01.png", other, cover: true), other), (Image("boxart-02.png", Cover, cover: false), Cover));
        Igdb(new IgdbGameRating(42, "co1", null, null));

        Assert.That(await Run(row), Is.EqualTo(Summary(pulled: 1)));

        var game = Read(row);
        Assert.That(game.Images.Select(i => (i.FileName, i.IsCover)), Is.EqualTo(new[] { ("boxart-01.png", false), ("boxart-02.png", true) }));
        Assert.That(game.Actions?.CoverImagePulled, Is.EqualTo(NowText));
        _audit.Received(1).Record(Arg.Is<AuditEvent>(e => e.Details == "IGDB cover boxart-02.png"));
    }

    [Test]
    public async Task ImageLimit_CountsAfterThePlaceholderIsRemoved()
    {
        (InfoJsonImage, byte[]) Real(int n) { var b = PngTests.Png((uint)(n + 20), 5); return (Image($"boxart-{n:00}.png", b, cover: false), b); }
        var withPlaceholder = await Add("Alpha", 1, [(Image("boxart-20.png", Placeholder, cover: true, isDefault: true), Placeholder),
            .. Enumerable.Range(1, 19).Select(Real)]);
        var full = await Add("Beta", 2, [.. Enumerable.Range(1, 20).Select(n => n == 1
            ? (Image("boxart-01.png", PngTests.Png(21, 5), cover: true), PngTests.Png(21, 5)) : Real(n))]);
        Igdb(new IgdbGameRating(1, "co1", null, null), new IgdbGameRating(2, "co2", null, null));
        var before = Mtime(full);

        Assert.That(await Run(withPlaceholder, full), Is.EqualTo(Summary(pulled: 1, imageLimit: 1)));

        Assert.That(Read(withPlaceholder).Images, Has.Count.EqualTo(20));
        Assert.That((Mtime(full), Read(full).Actions), Is.EqualTo((before, (InfoJsonActions?)null)));
    }

    [Test]
    public async Task EveryCase_InOrder_WithItsOutcome()
    {
        var pending = await Add("Pending", 1);
        var missing = new MirrorEntry(Game("Missing", Rom("m.gb", RomBytes(99))) with { IgdbId = 9 }, "gb/M/Missing.zip", 1, DateTime.UtcNow).ToRows().Game;
        var already = await Add("Already", 2, g => g with { Actions = new InfoJsonActions { CoverImagePulled = NowText } });
        var relinked = await Add("Relinked", 3) with { IgdbId = 30 };
        var noCover = await Add("NoCover", 4);
        var notReturned = await Add("NotReturned", 5);
        var broken = await Add("Broken", 6);
        var junk = await Add("Junk", 7);
        Igdb(new IgdbGameRating(1, "c1", null, null), new IgdbGameRating(30, "c3", null, null), new IgdbGameRating(4, null, null, null),
            new IgdbGameRating(6, "c6", null, null), new IgdbGameRating(7, "c7", null, null));
        _igdb.DownloadCoverAsync("c6", Arg.Any<CancellationToken>()).Returns(new IgdbCoverDownload(null, 404, "IGDB returned HTTP 404."));
        _igdb.DownloadCoverAsync("c7", Arg.Any<CancellationToken>()).Returns(new IgdbCoverDownload([1, 2, 3], 200, null));
        // Only the first game asked about is pending: a legacy system whose embedded games wait to be moved out.
        var legacy = TestHandlers.Fake("gb");
        legacy.LegacyEmbeddedRoms.Returns(true);
        _systems.ById("gb").Returns(legacy, _gb);

        var summary = await Run(pending, missing, already, relinked, noCover, notReturned, broken, junk);

        Assert.That(summary, Is.EqualTo(Summary(noCover: 2, skipped: 3, error: 3)));
        Assert.That(_written.Select(i => (i.SourcePath, i.Outcome, i.Reason)), Is.EqualTo(new (string, string, string?)[]
        {
            ("roms/gb/P/Pending.zip", "Skipped", null),
            ("roms/gb/M/Missing.zip", "Error", "TargetZipUnreadable"),
            ("roms/gb/A/Already.zip", "Skipped", null),
            ("roms/gb/R/Relinked.zip", "Skipped", null),
            ("roms/gb/N/NoCover.zip", "NoCover", null),
            ("roms/gb/N/NotReturned.zip", "NoCover", null),
            ("roms/gb/B/Broken.zip", "Error", "DownloadFailed"),
            ("roms/gb/J/Junk.zip", "Error", "ImageUnreadable"),
        }));
        Assert.That(_written[6].Details, Is.EqualTo("IGDB returned HTTP 404."));
        await _mirror.Received(1).ReplaceAsync(Arg.Is<MirrorEntry>(e => e.ZipPath == already.ZipPath), Arg.Any<CancellationToken>());
        await _mirror.ReceivedWithAnyArgs(1).ReplaceAsync(default!, default);
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
    }

    [Test]
    public async Task Batches_IdsOf500()
    {
        var rows = new List<GameRow>();
        for (var i = 1; i <= 501; i++)
            rows.Add(new MirrorEntry(Game($"G{i}", Rom("g.gb", RomBytes(1))) with { IgdbId = i }, $"gb/G/G{i}.zip", 1, DateTime.UtcNow).ToRows().Game);
        Igdb();

        await Run([.. rows]);

        await _igdb.Received(1).GetGamesAsync(Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 500), Arg.Any<CancellationToken>());
        await _igdb.Received(1).GetGamesAsync(Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 501 })), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IgdbReadFailure_FailsTheJob_WithTheTitle()
    {
        var row = await Add("Tetris", 42);
        _igdb.GetGamesAsync(default!, default).ReturnsForAnyArgs(Task.FromException<IReadOnlyList<IgdbGameRating>>(new InvalidOperationException(IgdbClient.Busy)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(row));

        Assert.That(ex!.Message, Is.EqualTo(IgdbClient.Busy));
    }

    [Test]
    public async Task OpenBreaker_FailsTheJob_After3Failures_A404DoesNotCount()
    {
        var rows = new List<GameRow>();
        for (var i = 1; i <= 5; i++)
            rows.Add(await Add($"Game{i}", i));
        Igdb([.. Enumerable.Range(1, 5).Select(i => new IgdbGameRating(i, $"c{i}", null, null))]);
        _igdb.DownloadCoverAsync("c1", Arg.Any<CancellationToken>()).Returns(new IgdbCoverDownload(null, 404, "IGDB returned HTTP 404."));
        _igdb.DownloadCoverAsync("c2", Arg.Any<CancellationToken>()).Returns(new IgdbCoverDownload(null, null, "down"));
        _igdb.DownloadCoverAsync("c3", Arg.Any<CancellationToken>()).Returns(new IgdbCoverDownload(null, 503, "IGDB returned HTTP 503."));
        _igdb.DownloadCoverAsync("c4", Arg.Any<CancellationToken>()).Returns(new IgdbCoverDownload(null, 500, "IGDB returned HTTP 500."));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run([.. rows]));

        Assert.That(ex!.Message, Is.EqualTo("IGDB images couldn't be reached."));
        Assert.That(_written, Has.Count.EqualTo(4), "stopped after the game that opened it");
    }

    [Test]
    public async Task Cancelled_StopsBetweenGames()
    {
        var row = await Add("Tetris", 42);
        Igdb(new IgdbGameRating(42, "co1", null, null));
        _games.ListPullCoverCandidatesAsync(Arg.Any<CancellationToken>()).Returns([row]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var summary = JsonSerializer.Serialize(await Job().RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), cancelled.Token));

        Assert.That(summary, Is.EqualTo(Summary()));
        Assert.That(_written, Is.Empty);
    }
}
