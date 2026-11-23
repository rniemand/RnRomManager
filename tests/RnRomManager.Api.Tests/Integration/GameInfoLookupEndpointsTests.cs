using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using RnRomManager.Core.Database;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>IGDB metadata lookup L1/L2: <c>GET /api/games/{id}/igdb-matches</c>, with the IGDB client replaced.</summary>
[Category("Integration")]
public class GameInfoLookupEndpointsTests
{
    private static readonly DateTime Start = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly IGameInfoLookup _lookup = Substitute.For<IGameInfoLookup>();
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private Guid _game;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync()) { TestServices = s => s.AddSingleton(_lookup) };
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        _lookup.ClearReceivedCalls();
        _lookup.ClearSubstitute();
        _lookup.Configured.Returns(true);
        _lookup.LookupAsync(default!, default!, default).ReturnsForAnyArgs(new GameInfoLookupResult([], null));
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        _game = await InsertGameAsync("The Legend of Orbit");
    }

    [TearDown]
    public void TearDown() => _admin.Dispose();

    private async Task<Guid> InsertGameAsync(string displayTitle)
    {
        var id = Guid.NewGuid();
        await using var db = _factory.Services.GetRequiredService<IDbConnectionFactory>().Create();
        await db.ExecuteAsync("""
            INSERT INTO games (id, system_id, title, display_title, zip_path, bucket, developers, publishers, genres,
                               zip_size_bytes, zip_mtime_utc, info_schema_version, created_at, updated_at)
            VALUES (@id, 'snes', @displayTitle, @displayTitle, @zip, 'L', '[]', '[]', '[]', 1, @Start, 8, @Start, @Start)
            """, new { id, displayTitle, zip = $"snes/L/{id}.zip", Start });
        return id;
    }

    private Task<HttpResponseMessage> Get(string query = "", Guid? game = null) =>
        _admin.GetAsync($"/api/games/{game ?? _game}/igdb-matches{query}");

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private Task AskedNothing() => _lookup.DidNotReceiveWithAnyArgs().LookupAsync(default!, default!, default);

    [Test]
    public async Task NoQ_SearchesTheDisplayTitle()
    {
        using var response = await Get();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await _lookup.Received(1).LookupAsync("The Legend of Orbit", "snes", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoQ_CutsALongDisplayTitleTo100()
    {
        var long_ = new string('a', 150);
        var game = await InsertGameAsync(long_);

        using var response = await Get(game: game);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await _lookup.Received(1).LookupAsync(long_[..100], "snes", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Q_IsTrimmedAndSearched()
    {
        using var response = await Get("?q=%20%20Orbit%20Two%20");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await _lookup.Received(1).LookupAsync("Orbit Two", "snes", Arg.Any<CancellationToken>());
    }

    [TestCase("?q=")]
    [TestCase("?q=%20%20")]
    [TestCase("?q=%22%5C%22")]
    public async Task BadQ_IsValidationFailed_BeforeAnythingElse(string query)
    {
        using var response = await Get(query, Guid.NewGuid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await Json(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(problem.GetProperty("errors").GetProperty("q")[0].GetString(), Is.EqualTo("Must be 1–100 characters."));
        await AskedNothing();
    }

    [Test]
    public async Task Q_Of100IsAllowed_101IsNot()
    {
        using (var ok = await Get("?q=" + new string('q', 100)))
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var tooLong = await Get("?q=" + new string('q', 101));
        Assert.That(tooLong.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task UnknownGame_Is404_AndAsksNothing()
    {
        using var response = await Get("?q=Orbit", Guid.NewGuid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await AskedNothing();
    }

    [Test]
    public async Task NotConfigured_Is409_AndAsksNothing()
    {
        _lookup.Configured.Returns(false);

        using var response = await Get();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await Json(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("IgdbNotConfigured"));
        Assert.That(problem.GetProperty("title").GetString(), Is.EqualTo("Metadata lookup needs Sources:IgdbClientId and Sources:IgdbClientSecret."));
        await AskedNothing();
    }

    [Test]
    public async Task Failure_Is502_WithTheTitle()
    {
        _lookup.LookupAsync(default!, default!, default).ReturnsForAnyArgs(new GameInfoLookupResult(null, IgdbClient.Busy));

        using var response = await Get();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        var problem = await Json(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("IgdbFailed"));
        Assert.That(problem.GetProperty("title").GetString(), Is.EqualTo("IGDB is busy; try again in a moment."));
    }

    [Test]
    public async Task Matches_ArePassedThrough()
    {
        _lookup.LookupAsync(default!, default!, default).ReturnsForAnyArgs(new GameInfoLookupResult(
        [
            new GameInfoMatch(1234, "The Legend of Orbit", 1993, ["Nintendo"], ["Nintendo", "Capcom"], ["Adventure"], "A quest.",
                "https://images.igdb.com/igdb/image/upload/t_cover_small/co1.jpg", "https://www.igdb.com/games/orbit"),
            new GameInfoMatch(5, "Orbit 2", null, [], [], [], null, null, null),
        ], null));

        using var response = await Get();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var matches = (await Json(response)).GetProperty("matches");
        Assert.That(matches.GetArrayLength(), Is.EqualTo(2));
        var first = matches[0];
        Assert.That(first.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[]
        {
            "igdbId", "name", "releaseYear", "developers", "publishers", "genres", "description", "coverUrl", "url",
        }));
        Assert.That((first.GetProperty("igdbId").GetInt32(), first.GetProperty("name").GetString(), first.GetProperty("releaseYear").GetInt32()),
            Is.EqualTo((1234, "The Legend of Orbit", 1993)));
        Assert.That(first.GetProperty("publishers").EnumerateArray().Select(p => p.GetString()), Is.EqualTo(new[] { "Nintendo", "Capcom" }));
        Assert.That((first.GetProperty("coverUrl").GetString(), first.GetProperty("url").GetString()),
            Is.EqualTo(("https://images.igdb.com/igdb/image/upload/t_cover_small/co1.jpg", "https://www.igdb.com/games/orbit")));
        Assert.That((matches[1].GetProperty("releaseYear").ValueKind, matches[1].GetProperty("description").ValueKind),
            Is.EqualTo((JsonValueKind.Null, JsonValueKind.Null)));
    }
}
