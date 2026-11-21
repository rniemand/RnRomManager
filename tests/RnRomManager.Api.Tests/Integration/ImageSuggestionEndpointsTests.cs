using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>IGDB suggestions S8: <c>GET /api/games/{id}/image-suggestions</c>, with the IGDB client replaced.</summary>
[Category("Integration")]
public class ImageSuggestionEndpointsTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly IImageSuggestions _suggestions = Substitute.For<IImageSuggestions>();
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private Guid _game;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync()) { TestServices = s => s.AddSingleton(_suggestions) };
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        _suggestions.ClearReceivedCalls();
        _suggestions.ClearSubstitute();
        _suggestions.Configured.Returns(true);
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        _game = Guid.NewGuid();
        await using var db = _factory.Services.GetRequiredService<IDbConnectionFactory>().Create();
        await db.ExecuteAsync("""
            INSERT INTO games (id, system_id, title, display_title, zip_path, bucket, developers, publishers, genres,
                               zip_size_bytes, zip_mtime_utc, info_schema_version, created_at, updated_at)
            VALUES (@game, 'snes', 'Legend of Orbit, The', 'The Legend of Orbit', 'snes/L/l.zip', 'L', '[]', '[]', '[]', 1, @Start, 2, @Start, @Start)
            """, new { game = _game, Start });
    }

    [TearDown]
    public void TearDown() => _admin.Dispose();

    private Task<HttpResponseMessage> Get(string query, Guid? game = null) =>
        _admin.GetAsync($"/api/games/{game ?? _game}/image-suggestions{query}");

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [TestCase("")]
    [TestCase("?type=boxart")]
    [TestCase("?type=TitleScreen")]
    [TestCase("?type=0")]
    public async Task BadType_IsValidationFailed_BeforeAnythingElse(string query)
    {
        using var response = await Get(query, Guid.NewGuid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await Json(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(problem.GetProperty("errors").GetProperty("type")[0].GetString(), Is.EqualTo("Choose BoxArt or Snap."));
        await _suggestions.DidNotReceiveWithAnyArgs().SuggestAsync(default!, default!, default, default);
    }

    [Test]
    public async Task UnknownGame_Is404_AndAsksNothing()
    {
        using var response = await Get("?type=BoxArt", Guid.NewGuid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await _suggestions.DidNotReceiveWithAnyArgs().SuggestAsync(default!, default!, default, default);
    }

    [Test]
    public async Task NotConfigured_Is409_AndAsksNothing()
    {
        _suggestions.Configured.Returns(false);

        using var response = await Get("?type=BoxArt");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await Json(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("IgdbNotConfigured"));
        Assert.That(problem.GetProperty("title").GetString(), Is.EqualTo("Image suggestions need Sources:IgdbClientId and Sources:IgdbClientSecret."));
        await _suggestions.DidNotReceiveWithAnyArgs().SuggestAsync(default!, default!, default, default);
    }

    [Test]
    public async Task Failure_Is502_WithTheTitle()
    {
        _suggestions.SuggestAsync(default!, default!, default, default).ReturnsForAnyArgs(ImageSuggestionResult.Failed(IgdbClient.Busy));

        using var response = await Get("?type=Snap");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        var problem = await Json(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("IgdbFailed"));
        Assert.That(problem.GetProperty("title").GetString(), Is.EqualTo("IGDB is busy; try again in a moment."));
    }

    [TestCase("BoxArt", ImageType.BoxArt)]
    [TestCase("Snap", ImageType.Snap)]
    public async Task Suggestions_AreReturned_ForTheDisplayTitleAndSystem(string type, ImageType expected)
    {
        _suggestions.SuggestAsync(default!, default!, default, default).ReturnsForAnyArgs(ImageSuggestionResult.Ok(
        [
            new ImageSuggestion("https://images.igdb.com/igdb/image/upload/t_cover_big/co1.jpg",
                "https://images.igdb.com/igdb/image/upload/t_1080p/co1.jpg", "The Legend of Orbit", 600, 800),
            new ImageSuggestion("https://p/2.jpg", "https://i/2.jpg", "Orbit 2", null, null),
        ]));

        using var response = await Get($"?type={type}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await _suggestions.Received(1).SuggestAsync("The Legend of Orbit", "snes", expected, Arg.Any<CancellationToken>());
        var list = (await Json(response)).GetProperty("suggestions");
        Assert.That(list.GetArrayLength(), Is.EqualTo(2));
        Assert.That(list[0].GetProperty("previewUrl").GetString(), Is.EqualTo("https://images.igdb.com/igdb/image/upload/t_cover_big/co1.jpg"));
        Assert.That(list[0].GetProperty("imageUrl").GetString(), Is.EqualTo("https://images.igdb.com/igdb/image/upload/t_1080p/co1.jpg"));
        Assert.That(list[0].GetProperty("gameName").GetString(), Is.EqualTo("The Legend of Orbit"));
        Assert.That(list[0].GetProperty("width").GetInt32(), Is.EqualTo(600));
        Assert.That(list[0].GetProperty("height").GetInt32(), Is.EqualTo(800));
        Assert.That(list[1].GetProperty("width").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task NoSuggestions_IsAnEmptyList()
    {
        _suggestions.SuggestAsync(default!, default!, default, default).ReturnsForAnyArgs(ImageSuggestionResult.Ok([]));

        using var response = await Get("?type=BoxArt");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Json(response)).GetProperty("suggestions").GetArrayLength(), Is.Zero);
    }
}
