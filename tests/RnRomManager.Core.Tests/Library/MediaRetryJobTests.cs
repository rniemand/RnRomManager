using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec §10.2 "Retry media" + <c>MediaRetry</c> job (D38): §15.1 "Thumbnail key + lookup" MediaRetry items (S35).</summary>
public class MediaRetryJobTests
{
    private const string BaseUrl = "https://thumbs.test";
    private const string GbBox = BaseUrl + "/Nintendo%20-%20Game%20Boy/Named_Boxarts/";
    private const string GbSnap = BaseUrl + "/Nintendo%20-%20Game%20Boy/Named_Snaps/";
    private static readonly byte[] DefaultArt = PngTests.Png(512, 512);

    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;
    private ISystemRegistry _systems = null!;
    private ILibraryMirror _mirror = null!;
    private IGameRepository _games = null!;
    private Dictionary<string, Func<HttpResponseMessage>> _routes = null!;
    private StubHttpHandler _server = null!;
    private int _seed;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var handler = Substitute.For<ISystemHandler>();
        handler.Id.Returns("gb");
        handler.DatName.Returns("Nintendo - Game Boy");
        handler.DefaultBoxArtPng.Returns(DefaultArt);
        _systems = Substitute.For<ISystemRegistry>();
        _systems.All.Returns([handler]);
        _systems.ById("gb").Returns(handler);
        _reader = new InfoJsonReader([], _systems);
        _mirror = Substitute.For<ILibraryMirror>();
        _games = Substitute.For<IGameRepository>();
        _routes = new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.Ordinal);
        _server = new StubHttpHandler(r => _routes.TryGetValue(r.RequestUri!.AbsoluteUri, out var answer)
            ? answer()
            : StubHttpHandler.Status(HttpStatusCode.NotFound));
        _seed = 0;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private GameMediaRetry Retry() => new(_mirror, _reader, _systems,
        new MediaService(new HttpClient(_server), Options.Create(new SourcesOptions { ThumbnailBaseUrl = BaseUrl }), NullLogger<MediaService>.Instance),
        Options.Create(new PathsOptions { Roms = _dir.Path }), TimeProvider.System, NullLogger<GameMediaRetry>.Instance);

    private MediaRetryJob Job() => new(_games, Retry(), NullLogger<MediaRetryJob>.Instance);

    private static InfoJsonImage Image(ImageType type, byte[] bytes, bool isDefault) => new()
    {
        FileName = type == ImageType.BoxArt ? MediaImage.BoxArtFileName : MediaImage.SnapFileName,
        Type = type,
        Source = isDefault ? InfoJsonImage.SourceDefault : InfoJsonImage.SourceLibretro,
        IsDefault = isDefault,
        IsCover = type == ImageType.BoxArt,
        SizeBytes = bytes.Length,
        Sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)),
        Width = 1,
        Height = 1,
        DateAdded = When,
    };

    /// <summary>Writes a one-ROM game ZIP with the given images; <paramref name="datGameName"/> null = a Manual release.</summary>
    private async Task<GameRow> Add(string title, string? datGameName, bool defaultBoxArt = true, bool boxArt = true, bool snap = false)
    {
        var rom = RomBytes(++_seed);
        var info = Rom($"{title}.gb", rom) with
        {
            Identification = datGameName is null
                ? new InfoJsonIdentification(IdentificationMethod.Manual)
                : new InfoJsonIdentification(IdentificationMethod.Dat, "Nintendo - Game Boy", "1", datGameName),
        };
        var realArt = PngTests.Png(100, 100);
        var snapArt = PngTests.Png(160, 144);
        var images = new List<(InfoJsonImage Image, byte[] Bytes)>();
        if (boxArt)
            images.Add(defaultBoxArt ? (Image(ImageType.BoxArt, DefaultArt, true), DefaultArt) : (Image(ImageType.BoxArt, realArt, false), realArt));
        if (snap)
            images.Add((Image(ImageType.Snap, snapArt, false), snapArt));
        var game = Game(title, info) with { Images = images.Select(i => i.Image).ToList() };

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

    private async Task<string> RunJob(params GameRow[] candidates)
    {
        _games.ListMediaRetryCandidatesAsync(Arg.Any<CancellationToken>()).Returns(candidates);
        return JsonSerializer.Serialize(await Job().RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None));
    }

    /// <summary>The job's <c>summary</c> as stored (§10.2: <c>{updated, notFound, skipped, error}</c>).</summary>
    private static string Summary(int updated = 0, int notFound = 0, int skipped = 0, int error = 0) =>
        JsonSerializer.Serialize(new { updated, notFound, skipped, error });

    [Test]
    public async Task DefaultBoxArt_FoundArtAndSnap_RewritesZipAndDb()
    {
        var row = await Add("Tetris", "Tetris (World)");
        _routes[GbBox + "Tetris%20%28World%29.png"] = () => StubHttpHandler.Bytes(PngTests.Png(300, 400));
        _routes[GbSnap + "Tetris%20%28World%29.png"] = () => StubHttpHandler.Bytes(PngTests.Png(160, 144));

        var summary = await RunJob(row);

        Assert.That(summary, Is.EqualTo(Summary(updated: 1)));
        var game = Read(row);
        var boxArt = game.Images.Single(i => i.Type == ImageType.BoxArt);
        Assert.That((boxArt.IsDefault, boxArt.Width, boxArt.SourceUrl), Is.EqualTo((false, 300, GbBox + "Tetris%20%28World%29.png")));
        Assert.That(game.Images.Single(i => i.Type == ImageType.Snap).Width, Is.EqualTo(160));
        await _mirror.Received(1).ReplaceAsync(Arg.Is<MirrorEntry>(e => e.Info.GameId == row.Id && e.ZipPath == row.ZipPath), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NothingNew_DoesNotRewrite()
    {
        var row = await Add("Tetris", "Tetris (World)");
        var before = await File.ReadAllBytesAsync(_dir.Combine(row.ZipPath.Split('/')));

        var summary = await RunJob(row);

        Assert.That(summary, Is.EqualTo(Summary(notFound: 1)));
        Assert.That(await File.ReadAllBytesAsync(_dir.Combine(row.ZipPath.Split('/'))), Is.EqualTo(before));
        await _mirror.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Test]
    public async Task RealBoxArt_OnlyTheMissingSnapIsAdded()
    {
        var row = await Add("Tetris", "Tetris (World)", defaultBoxArt: false);
        var boxArtBefore = Read(row).Images.Single();
        _routes[GbBox + "Tetris%20%28World%29.png"] = () => StubHttpHandler.Bytes(PngTests.Png(300, 400));
        _routes[GbSnap + "Tetris%20%28World%29.png"] = () => StubHttpHandler.Bytes(PngTests.Png(160, 144));

        var summary = await RunJob(row);

        Assert.That(summary, Is.EqualTo(Summary(updated: 1)));
        var images = Read(row).Images;
        Assert.That(images.Single(i => i.Type == ImageType.BoxArt), Is.EqualTo(boxArtBefore), "a real box art is never replaced");
        Assert.That(images.Single(i => i.Type == ImageType.Snap).Width, Is.EqualTo(160));
    }

    [Test]
    public async Task MissingBoxArt_GetsAtLeastTheDefault()
    {
        var row = await Add("Tetris", "Tetris (World)", boxArt: false);

        var summary = await RunJob(row);

        Assert.That(summary, Is.EqualTo(Summary(updated: 1)));
        Assert.That(Read(row).Images.Single().IsDefault, Is.True);
    }

    [Test]
    public async Task ManualOnlyGames_AreSkippedWithoutALookup_CompleteGamesToo()
    {
        var manual = await Add("Homebrew", null);
        var complete = await Add("Tetris", "Tetris (World)", defaultBoxArt: false, snap: true);

        var summary = await RunJob(manual, complete);

        Assert.That(summary, Is.EqualTo(Summary(skipped: 2)));
        Assert.That(_server.Requests, Is.Empty);
    }

    [Test]
    public async Task PerGameError_IsCountedAndTheJobContinues()
    {
        var broken = await Add("Alpha", "Alpha (World)");
        await File.WriteAllBytesAsync(_dir.Combine(broken.ZipPath.Split('/')), [1, 2, 3]);
        var fine = await Add("Tetris", "Tetris (World)");
        _routes[GbBox + "Tetris%20%28World%29.png"] = () => StubHttpHandler.Bytes(PngTests.Png(300, 400));

        var summary = await RunJob(broken, fine);

        Assert.That(summary, Is.EqualTo(Summary(updated: 1, error: 1)));
    }

    [Test]
    public async Task ProcessesCandidatesInTheGivenOrder_WithProgress()
    {
        var b = await Add("Beta", "Beta (World)");
        var a = await Add("Alpha", "Alpha (World)");
        _games.ListMediaRetryCandidatesAsync(Arg.Any<CancellationToken>()).Returns([a, b]);
        var progress = new JobProgress();

        await Job().RunAsync(new JobContext(Guid.NewGuid(), progress), CancellationToken.None);

        Assert.That(_server.Requests.Select(r => r.RequestUri!.AbsoluteUri).First(), Does.Contain("Alpha"));
        Assert.That((progress.Total, progress.Current), Is.EqualTo((2, 2)));
    }

    [Test]
    public async Task BreakerTrips_JobStopsAndFails()
    {
        var rows = new[] { await Add("Alpha", "Alpha (World)"), await Add("Beta", "Beta (World)"), await Add("Gamma", "Gamma (World)") };
        _server = new StubHttpHandler(_ => throw new HttpRequestException("connection refused"));
        _games.ListMediaRetryCandidatesAsync(Arg.Any<CancellationToken>()).Returns(rows);
        var progress = new JobProgress();

        var ex = await Assert.ThrowsAsync<ThumbnailHostUnreachableException>(() =>
            Job().RunAsync(new JobContext(Guid.NewGuid(), progress), CancellationToken.None));

        Assert.That(ex!.Message, Is.EqualTo("thumbnail host unreachable"));
        Assert.That(progress.Current, Is.LessThan(rows.Length), "stops before the end");
        await _mirror.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Test]
    public async Task Cancelled_StopsBetweenGames()
    {
        var row = await Add("Tetris", "Tetris (World)");
        _games.ListMediaRetryCandidatesAsync(Arg.Any<CancellationToken>()).Returns([row]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var summary = JsonSerializer.Serialize(await Job().RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), cancelled.Token));

        Assert.That(summary, Is.EqualTo(Summary()));
        Assert.That(_server.Requests, Is.Empty);
    }

    [Test]
    public async Task PerGameRetry_DoesNotSkipManualOnlyGames()
    {
        var row = await Add("Homebrew", null, boxArt: false);

        var result = await Retry().RetryAsync(row, new CircuitBreaker(), skipManualOnly: false, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(MediaRetryOutcome.Updated), "missing box art gets the default");
    }
}
