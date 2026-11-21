using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;

namespace RnRomManager.Core.Tests.Media;

/// <summary>IGDB suggestions S3–S6 against a fake handler: no test reaches the network.</summary>
public class IgdbClientTests
{
    private const string TokenJson = """{"access_token":"tok-1","expires_in":5000000,"token_type":"bearer"}""";

    private sealed class FakeIgdb : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, Task<HttpResponseMessage>> Token { get; set; } = (_, _) => Task.FromResult(Json(TokenJson));
        public Func<HttpRequestMessage, string, Task<HttpResponseMessage>> Games { get; set; } = (_, _) => Task.FromResult(Json("[]"));
        public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

        public IEnumerable<(HttpRequestMessage Request, string Body)> TokenRequests => Requests.Where(r => r.Request.RequestUri!.Host == "id.twitch.tv");
        public IEnumerable<(HttpRequestMessage Request, string Body)> GameRequests => Requests.Where(r => r.Request.RequestUri!.Host == "api.igdb.com");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue((request, body));
            return request.RequestUri!.Host switch
            {
                "id.twitch.tv" => await Token(request, body),
                "api.igdb.com" => await Games(request, body),
                _ => throw new InvalidOperationException($"Unexpected host {request.RequestUri}"),
            };
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };

    private FakeIgdb _igdb = null!;
    private FakeTimeProvider _time = null!;
    private IgdbTokenCache _tokens = null!;

    [SetUp]
    public void SetUp()
    {
        _igdb = new FakeIgdb();
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        _tokens = new IgdbTokenCache(_time);
    }

    [TearDown]
    public void TearDown() => _igdb.Dispose();

    private IgdbClient Client(string id = "client-id", string secret = "s3cret") =>
        new(new HttpClient(_igdb, disposeHandler: false), _tokens,
            Options.Create(new SourcesOptions { IgdbClientId = id, IgdbClientSecret = secret }), NullLogger<IgdbClient>.Instance);

    private static string Game(string name, string? cover = null, params string[] screenshots)
    {
        var parts = new List<string> { $"\"name\":\"{name}\"" };
        if (cover is not null)
            parts.Add($"\"cover\":{{\"image_id\":\"{cover}\",\"width\":600,\"height\":800}}");
        if (screenshots.Length > 0)
            parts.Add("\"screenshots\":[" + string.Join(',', screenshots.Select(s => $"{{\"image_id\":\"{s}\",\"width\":3840,\"height\":2160}}")) + "]");
        return "{" + string.Join(',', parts) + "}";
    }

    private void Games(params string[] games) =>
        _igdb.Games = (_, _) => Task.FromResult(Json("[" + string.Join(',', games) + "]"));

    [Test]
    public void Configured_FollowsTheKeys()
    {
        Assert.That(Client().Configured, Is.True);
        Assert.That(Client(secret: " ").Configured, Is.False);
        Assert.That(Client(id: "").Configured, Is.False);
    }

    [Test]
    public async Task Token_IsRequestedWithAFormBody_AndNoQueryString()
    {
        await Client(" client-id ", " s3cret ").SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        var (request, body) = _igdb.TokenRequests.Single();
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.RequestUri!.ToString(), Is.EqualTo(IgdbClient.TokenUrl));
        Assert.That(request.RequestUri.Query, Is.Empty);
        Assert.That(request.Content!.Headers.ContentType!.MediaType, Is.EqualTo("application/x-www-form-urlencoded"));
        Assert.That(body, Is.EqualTo("client_id=client-id&client_secret=s3cret&grant_type=client_credentials"));
    }

    [Test]
    public async Task Search_SendsTheHeaders_AndTheApicalypseBody()
    {
        Games(Game("Hi", "co1"));

        await Client().SuggestAsync("Say \"Hi\" \\ Bye", "snes", ImageType.BoxArt, CancellationToken.None);

        var (request, body) = _igdb.GameRequests.Single();
        Assert.That(request.RequestUri!.ToString(), Is.EqualTo(IgdbClient.GamesUrl));
        Assert.That(request.Headers.GetValues("Client-ID").Single(), Is.EqualTo("client-id"));
        Assert.That(request.Headers.Authorization!.ToString(), Is.EqualTo("Bearer tok-1"));
        Assert.That(body, Is.EqualTo(
            "search \"Say Hi  Bye\"; fields name,cover.image_id,cover.width,cover.height,screenshots.image_id,screenshots.width,"
            + "screenshots.height; where platforms = (19); limit 5;"));
    }

    [Test]
    public async Task NoMatchOnThePlatform_SearchesOnceMoreWithoutIt()
    {
        var calls = 0;
        _igdb.Games = (_, _) => Task.FromResult(Json(++calls == 1 ? "[]" : "[" + Game("Orbit", "co1") + "]"));

        var result = await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        var bodies = _igdb.GameRequests.Select(r => r.Body).ToList();
        Assert.That(bodies, Has.Count.EqualTo(2));
        Assert.That(bodies[0], Does.Contain("where platforms = (19);"));
        Assert.That(bodies[1], Does.Not.Contain("where"));
        Assert.That(result.Suggestions, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task UnmappedSystem_SearchesWithoutAPlatform_Once()
    {
        await Client().SuggestAsync("Orbit", "nope", ImageType.BoxArt, CancellationToken.None);

        Assert.That(_igdb.GameRequests.Single().Body, Does.Not.Contain("where"));
    }

    [Test]
    public async Task Token_IsReused_UntilAnHourBeforeExpiry()
    {
        var client = Client();
        await client.SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5000000 - 3601));
        await client.SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);
        Assert.That(_igdb.TokenRequests.Count(), Is.EqualTo(1));

        _time.Advance(TimeSpan.FromSeconds(2));
        await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(_igdb.TokenRequests.Count(), Is.EqualTo(2), "a new client shares the cache, which has now expired");
    }

    [Test]
    public async Task ConcurrentCalls_ShareOneTokenRequest()
    {
        Games(Game("Orbit", "co1"));
        var release = new TaskCompletionSource();
        _igdb.Token = async (_, _) =>
        {
            await release.Task;
            return Json(TokenJson);
        };

        var calls = Enumerable.Range(0, 3).Select(_ => Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None)).ToList();
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(calls);

        Assert.That(_igdb.TokenRequests.Count(), Is.EqualTo(1));
        Assert.That(_igdb.GameRequests.Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task Unauthorized_RefreshesTheToken_AndRetriesOnce()
    {
        var tokens = 0;
        _igdb.Token = (_, _) => Task.FromResult(Json($$"""{"access_token":"tok-{{++tokens}}","expires_in":5000000}"""));
        _igdb.Games = (request, _) => Task.FromResult(request.Headers.Authorization!.Parameter == "tok-1"
            ? Status(HttpStatusCode.Unauthorized)
            : Json("[" + Game("Orbit", "co1") + "]"));

        var result = await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.Suggestions, Has.Count.EqualTo(1));
        Assert.That(_igdb.TokenRequests.Count(), Is.EqualTo(2));
        Assert.That(_igdb.GameRequests.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task UnauthorizedTwice_IsRefused()
    {
        _igdb.Games = (_, _) => Task.FromResult(Status(HttpStatusCode.Unauthorized));

        var result = await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.FailedTitle, Is.EqualTo("IGDB refused the Client ID or secret."));
        Assert.That(_igdb.GameRequests.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task BoxArt_IsOneCoverPerGame_InIgdbOrder_AtMostFive()
    {
        Games(Game("A", "coa"), Game("No cover"), Game("B", "cob"), Game("C", "coc"), Game("D", "cod"), Game("E", "coe"), Game("F", "cof"));

        var result = await Client().SuggestAsync("A", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.FailedTitle, Is.Null);
        Assert.That(result.Suggestions!.Select(s => s.GameName), Is.EqualTo(new[] { "A", "B", "C", "D", "E" }));
        var first = result.Suggestions![0];
        Assert.That(first.PreviewUrl, Is.EqualTo("https://images.igdb.com/igdb/image/upload/t_cover_big/coa.jpg"));
        Assert.That(first.ImageUrl, Is.EqualTo("https://images.igdb.com/igdb/image/upload/t_1080p/coa.jpg"));
        Assert.That((first.Width, first.Height), Is.EqualTo(((int?)600, (int?)800)));
    }

    [Test]
    public async Task Snap_IsTheFirstGameWithScreenshots_AtMostFive()
    {
        Games(Game("No shots", "co1"), Game("Shots", null, "s1", "s2", "s3", "s4", "s5", "s6"), Game("Later", null, "x1"));

        var result = await Client().SuggestAsync("Shots", "snes", ImageType.Snap, CancellationToken.None);

        Assert.That(result.Suggestions!.Select(s => s.ImageUrl), Is.EqualTo(new[] { "s1", "s2", "s3", "s4", "s5" }
            .Select(id => $"https://images.igdb.com/igdb/image/upload/t_1080p/{id}.jpg")));
        Assert.That(result.Suggestions!.All(s => s.GameName == "Shots"));
        Assert.That(result.Suggestions![0].PreviewUrl, Is.EqualTo("https://images.igdb.com/igdb/image/upload/t_screenshot_med/s1.jpg"));
        Assert.That((result.Suggestions![0].Width, result.Suggestions[0].Height), Is.EqualTo(((int?)1920, (int?)1080)));
    }

    [Test]
    public async Task NoImages_IsAnEmptyList()
    {
        Games(Game("Bare"));

        var result = await Client().SuggestAsync("Bare", "snes", ImageType.Snap, CancellationToken.None);

        Assert.That(result.Suggestions, Is.Empty);
        Assert.That(result.FailedTitle, Is.Null);
    }

    [Test]
    public async Task UnsafeImageIds_AreSkipped()
    {
        Games("""{"name":"A","cover":{"image_id":"../evil"}}""", """{"name":"B","cover":{"image_id":"ok1"}}""",
            """{"name":"C","cover":{"image_id":7}}""");

        var result = await Client().SuggestAsync("A", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.Suggestions!.Select(s => s.GameName), Is.EqualTo(new[] { "B" }));
        Assert.That((result.Suggestions![0].Width, result.Suggestions[0].Height), Is.EqualTo(((int?)null, (int?)null)));
    }

    [TestCase(600, 800, 600, 800)]
    [TestCase(3000, 4000, 810, 1080)]
    [TestCase(3840, 2160, 1920, 1080)]
    [TestCase(4000, 1000, 1920, 480)]
    public void StoredSize_FitsInto1080p_NeverUp(int width, int height, int storedWidth, int storedHeight) =>
        Assert.That(IgdbClient.StoredSize(width, height), Is.EqualTo(((int?)storedWidth, (int?)storedHeight)));

    [Test]
    public void StoredSize_MissingDimensions_AreNull()
    {
        Assert.That(IgdbClient.StoredSize(null, 100), Is.EqualTo(((int?)null, (int?)null)));
        Assert.That(IgdbClient.StoredSize(100, 0), Is.EqualTo(((int?)null, (int?)null)));
    }

    [TestCase(HttpStatusCode.BadRequest, "IGDB refused the Client ID or secret.")]
    [TestCase(HttpStatusCode.Unauthorized, "IGDB refused the Client ID or secret.")]
    [TestCase(HttpStatusCode.Forbidden, "IGDB refused the Client ID or secret.")]
    [TestCase(HttpStatusCode.TooManyRequests, "IGDB is busy; try again in a moment.")]
    [TestCase(HttpStatusCode.InternalServerError, "IGDB returned HTTP 500.")]
    public async Task TokenFailures_HaveTheirTitles(HttpStatusCode status, string title)
    {
        _igdb.Token = (_, _) => Task.FromResult(Status(status));

        var result = await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.FailedTitle, Is.EqualTo(title));
        Assert.That(result.Suggestions, Is.Null);
        Assert.That(_igdb.GameRequests, Is.Empty);
    }

    [TestCase(HttpStatusCode.TooManyRequests, "IGDB is busy; try again in a moment.")]
    [TestCase(HttpStatusCode.Forbidden, "IGDB returned HTTP 403.")]
    [TestCase(HttpStatusCode.BadGateway, "IGDB returned HTTP 502.")]
    public async Task SearchFailures_HaveTheirTitles(HttpStatusCode status, string title)
    {
        _igdb.Games = (_, _) => Task.FromResult(Status(status));

        var result = await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.FailedTitle, Is.EqualTo(title));
    }

    [TestCase("not json")]
    [TestCase("""{"token":"x"}""")]
    [TestCase("""{"access_token":"x","expires_in":0}""")]
    [TestCase("""{"access_token":5,"expires_in":100}""")]
    [TestCase("[]")]
    public async Task UnreadableToken_IsUnreadable(string body)
    {
        _igdb.Token = (_, _) => Task.FromResult(Json(body));

        var result = await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.FailedTitle, Is.EqualTo("IGDB returned an unreadable response."));
    }

    [TestCase("not json")]
    [TestCase("""{"name":"A"}""")]
    public async Task UnreadableSearch_IsUnreadable(string body)
    {
        _igdb.Games = (_, _) => Task.FromResult(Json(body));

        var result = await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.FailedTitle, Is.EqualTo("IGDB returned an unreadable response."));
    }

    [Test]
    public async Task ThrownException_IsCouldNotBeReached()
    {
        _igdb.Games = (_, _) => throw new HttpRequestException("connection refused");

        var result = await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.FailedTitle, Is.EqualTo("IGDB couldn't be reached."));
    }

    [Test]
    public async Task Timeout_IsCouldNotBeReached()
    {
        _igdb.Games = (_, _) => throw new TaskCanceledException("timed out");

        var result = await Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, CancellationToken.None);

        Assert.That(result.FailedTitle, Is.EqualTo("IGDB couldn't be reached."));
    }

    [Test]
    public void Cancelled_Propagates_WithNoFailureResult()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(),
            () => Client().SuggestAsync("Orbit", "snes", ImageType.BoxArt, cancelled.Token));
    }

    [Test]
    public void OtherImageTypes_AreRejected() =>
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client().SuggestAsync("Orbit", "snes", ImageType.TitleScreen, CancellationToken.None));
}
