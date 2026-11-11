using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Tests.Library;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Media;

/// <summary>Spec §10.2 thumbnail key, lookup order, validation, empty base URL, circuit breaker (§15.1 row, minus MediaRetry).</summary>
public partial class MediaServiceTests
{
    private const string BaseUrl = "https://thumbs.test";
    private const string GbBox = BaseUrl + "/Nintendo%20-%20Game%20Boy/Named_Boxarts/";
    private const string GbSnap = BaseUrl + "/Nintendo%20-%20Game%20Boy/Named_Snaps/";

    private static readonly byte[] DefaultArt = PngTests.Png(512, 512);
    private static readonly byte[] Found = PngTests.Png(300, 400);

    private ISystemHandler _handler = null!;
    private Dictionary<string, Func<HttpResponseMessage>> _routes = null!;
    private StubHttpHandler _server = null!;
    private CircuitBreaker _breaker = null!;
    private DatCache _dats = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = Substitute.For<ISystemHandler>();
        _handler.EmbedRomsInZip.Returns(true);
        _handler.Id.Returns("gb");
        _handler.DatName.Returns("Nintendo - Game Boy");
        _handler.DefaultBoxArtPng.Returns(DefaultArt);
        _routes = new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.Ordinal);
        _server = new StubHttpHandler(r => _routes.TryGetValue(r.RequestUri!.AbsoluteUri, out var answer)
            ? answer()
            : StubHttpHandler.Status(HttpStatusCode.NotFound));
        _breaker = new CircuitBreaker();
        _dats = new DatCache(Options.Create(new PathsOptions()), new SystemRegistry([]), NullLogger<DatCache>.Instance);
    }

    private MediaService Service(string baseUrl = BaseUrl) =>
        TestMedia.Create(new HttpClient(_server), baseUrl, new HttpClient(_server), _dats);

    private Task<MediaResult> FindAsync(params string?[] names) => Service().FindAsync(_handler, names, _breaker, CancellationToken.None);

    private static Func<HttpResponseMessage> Png(byte[]? bytes = null) => () => StubHttpHandler.Bytes(bytes ?? Found);

    private static void AssertDefault(MediaImage boxArt)
    {
        Assert.That(boxArt.IsDefault, Is.True);
        Assert.That(boxArt.Bytes, Is.EqualTo(DefaultArt));
        Assert.That((boxArt.Width, boxArt.Height), Is.EqualTo((512, 512)));
        Assert.That(boxArt.SourceUrl, Is.Null);
    }

    // --- key ---

    [TestCase("&", "_")]
    [TestCase("*", "_")]
    [TestCase("/", "_")]
    [TestCase(":", "_")]
    [TestCase("`", "_")]
    [TestCase("<", "_")]
    [TestCase(">", "_")]
    [TestCase("?", "_")]
    [TestCase("\\", "_")]
    [TestCase("|", "_")]
    [TestCase("\"", "_")]
    public void Key_ReplacesEachSpecialCharacter(string c, string expected) => Assert.That(ThumbnailKey.For("a" + c + "b"), Is.EqualTo("a" + expected + "b"));

    [Test]
    public void Key_ThenUrlEncodes() =>
        Assert.That(ThumbnailKey.For("Asteroids & Missile Command (USA, Europe)"), Is.EqualTo("Asteroids%20_%20Missile%20Command%20%28USA%2C%20Europe%29"));

    [Test]
    public void Key_KeepsOtherPunctuationAndEncodesNonAscii() =>
        Assert.That(ThumbnailKey.For("Pokémon - Pinball, The! [b]"), Is.EqualTo("Pok%C3%A9mon%20-%20Pinball%2C%20The%21%20%5Bb%5D"));

    [Test]
    public void Url_EncodesTheDatFolder() =>
        Assert.That(ThumbnailKey.Url(BaseUrl + "/", "Nintendo - Game Boy", ThumbnailKey.BoxArtFolder, "Tetris (World) (Rev 1)"),
            Is.EqualTo(GbBox + "Tetris%20%28World%29%20%28Rev%201%29.png"));

    // --- lookup ---

    [Test]
    public async Task FirstHitPerTypeWins_InTheGivenOrder()
    {
        // Verified live: "Tetris (World)" is a 404, "(Rev 1)" a 200 (§10).
        _routes[GbBox + "Tetris%20%28World%29%20%28Rev%201%29.png"] = Png();
        _routes[GbSnap + "Tetris%20%28World%29.png"] = Png(PngTests.Png(160, 144));

        var result = await FindAsync("Tetris (World)", "Tetris (World) (Rev 1)");

        Assert.That(result.BoxArt.IsDefault, Is.False);
        Assert.That(result.BoxArt.SourceUrl, Is.EqualTo(GbBox + "Tetris%20%28World%29%20%28Rev%201%29.png"));
        Assert.That((result.BoxArt.Width, result.BoxArt.Height), Is.EqualTo((300, 400)));
        Assert.That(result.Snap!.SourceUrl, Is.EqualTo(GbSnap + "Tetris%20%28World%29.png"));
        Assert.That(_server.Requests.Select(r => r.RequestUri!.AbsoluteUri), Is.EqualTo(new[]
        {
            GbBox + "Tetris%20%28World%29.png",
            GbBox + "Tetris%20%28World%29%20%28Rev%201%29.png",
            GbSnap + "Tetris%20%28World%29.png",
        }));
    }

    [Test]
    public async Task ManualReleases_AreSkipped_AllManualGetsDefaultWithoutRequests()
    {
        var result = await FindAsync(null, null);

        AssertDefault(result.BoxArt);
        Assert.That(result.Snap, Is.Null);
        Assert.That(_server.Requests, Is.Empty);
    }

    [Test]
    public async Task NothingFound_DefaultBoxArtAndNoSnap()
    {
        var result = await FindAsync("Tetris (World)");

        AssertDefault(result.BoxArt);
        Assert.That(result.Snap, Is.Null);
        Assert.That(_server.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EmptyBaseUrl_NoLookup()
    {
        var result = await Service("").FindAsync(_handler, ["Tetris (World)"], _breaker, CancellationToken.None);

        AssertDefault(result.BoxArt);
        Assert.That(result.Snap, Is.Null);
        Assert.That(_server.Requests, Is.Empty);
    }

    [Test]
    public async Task InvalidPng_IsSkipped_NextNameTried()
    {
        _routes[GbBox + "A.png"] = Png("GIF89a not a png..........."u8.ToArray());
        _routes[GbBox + "B.png"] = Png();

        var result = await FindAsync("A", "B");

        Assert.That(result.BoxArt.SourceUrl, Is.EqualTo(GbBox + "B.png"));
    }

    [Test]
    public async Task Over5MiB_IsSkipped()
    {
        _routes[GbBox + "A.png"] = Png([.. Found, .. new byte[PngInfo.MaxBytes]]);

        var result = await FindAsync("A");

        AssertDefault(result.BoxArt);
    }

    [Test]
    public async Task ToInfoJson_FillsTheImageFields()
    {
        _routes[GbBox + "A.png"] = Png();
        var when = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

        var result = await FindAsync("A");
        var found = result.BoxArt.ToInfoJson(when, isCover: true);
        var fallback = MediaService.Default(_handler).ToInfoJson(when, isCover: true);

        Assert.That(found with { Sha1 = "" }, Is.EqualTo(new InfoJsonImage
        {
            FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = InfoJsonImage.SourceLibretro, SourceUrl = GbBox + "A.png",
            IsDefault = false, IsCover = true, SizeBytes = Found.Length, Sha1 = "", Width = 300, Height = 400, DateAdded = when,
        }));
        Assert.That(found.Sha1, Has.Length.EqualTo(40));
        Assert.That((fallback.Source, fallback.IsDefault, fallback.SourceUrl), Is.EqualTo((InfoJsonImage.SourceDefault, true, (string?)null)));
    }

    // --- circuit breaker ---

    [Test]
    public async Task ThreeNetworkFailures_TripTheBreaker_LaterLookupsSkipped()
    {
        _server = new StubHttpHandler(_ => throw new HttpRequestException("connection refused"));

        var first = await FindAsync("A", "B", "C", "D");

        AssertDefault(first.BoxArt);
        Assert.That(first.Snap, Is.Null);
        Assert.That(_breaker.IsOpen, Is.True);
        Assert.That(_server.Requests, Has.Count.EqualTo(3), "stops after the third failure");

        var later = await FindAsync("E");
        AssertDefault(later.BoxArt);
        Assert.That(_server.Requests, Has.Count.EqualTo(3), "nothing more requested this job");
    }

    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.RequestTimeout)]
    public async Task ServerErrorsCount(HttpStatusCode status)
    {
        _server = new StubHttpHandler(_ => StubHttpHandler.Status(status));

        await FindAsync("A", "B", "C");

        Assert.That(_breaker.IsOpen, Is.True);
    }

    [Test]
    public async Task NotFound_DoesNotCount()
    {
        await FindAsync("A", "B", "C", "D", "E");

        Assert.That(_breaker.IsOpen, Is.False);
        Assert.That(_server.Requests, Has.Count.EqualTo(10));
    }

    [Test]
    public async Task A200_ResetsTheCount()
    {
        var calls = 0;
        // Box art: fail, fail, 200. Snaps: fail, fail, 200. Without the reset that would be four failures in a row.
        _server = new StubHttpHandler(_ => ++calls is 3 or 6 ? StubHttpHandler.Bytes(Found) : throw new HttpRequestException("flaky"));

        var result = await FindAsync("A", "B", "C");

        Assert.That(result.BoxArt.IsDefault, Is.False);
        Assert.That(_breaker.IsOpen, Is.False);
    }

    [Test]
    public void Breaker_TripsOnce_AndStaysOpen()
    {
        var breaker = new CircuitBreaker();

        Assert.That(new[] { breaker.RecordFailure(), breaker.RecordFailure(), breaker.RecordFailure(), breaker.RecordFailure() },
            Is.EqualTo(new[] { false, false, true, false }));
        breaker.RecordSuccess();
        Assert.That(breaker.IsOpen, Is.True);
    }

    [Test]
    public async Task Cancellation_Propagates_AndDoesNotCount()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(),
            () => Service().FindAsync(_handler, ["A"], _breaker, cts.Token));
        Assert.That(_breaker.IsOpen, Is.False);
    }
}
