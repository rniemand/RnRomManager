using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Timeline T8/T9: <c>/api/timeline*</c> against MariaDB as a plain signed-in user, with the IGDB client replaced.</summary>
[Category("Integration")]
public class TimelineEndpointsTests
{
    private readonly IGameInfoLookup _lookup = Substitute.For<IGameInfoLookup>();
    private ApiFactory _factory = null!;
    private AuthClient _user = null!;
    private int _seed;

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
        _lookup.ClearSubstitute();
        _lookup.Configured.Returns(true);
        _user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "alice");
        (await _user.LoginAsync("alice", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown() => _user?.Dispose();

    private async Task AddGameAsync(string title, int? year, int? igdbId, string system = "gb")
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with { System = system, ReleaseYear = year, IgdbId = igdbId };
        await _factory.Services.GetRequiredService<ILibraryMirror>()
            .ReplaceAsync(new MirrorEntry(game, $"{system}/{title[0]}/{title}.zip", 100, When.UtcDateTime), default);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url)
    {
        using var response = await _user.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string[] Titles(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("displayTitle").GetString()!).ToArray();

    private async Task SeedAsync()
    {
        await AddGameAsync("Zeta", 1990, 1);
        await AddGameAsync("Alpha", 1990, 2);
        await AddGameAsync("Early", 1985, 3, "gbc");
        await AddGameAsync("Late", 1995, 4);
        await AddGameAsync("Unlinked", 1990, null);
        await AddGameAsync("Zero", 1990, 0);
        await AddGameAsync("No year", null, 5);
    }

    [Test]
    public async Task Years_CountLinkedGamesWithAYear_PerSystem_AndSayWhetherIgdbIsConfigured()
    {
        await SeedAsync();

        var (status, body) = await GetAsync("/api/timeline");
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("igdbConfigured").GetBoolean(), Is.True);
        Assert.That(body.GetProperty("total").GetInt32(), Is.EqualTo(4));
        Assert.That(body.GetProperty("years").EnumerateArray().Select(y => (y.GetProperty("year").GetInt32(), y.GetProperty("count").GetInt32())),
            Is.EqualTo(new[] { (1985, 1), (1990, 2), (1995, 1) }));

        _lookup.Configured.Returns(false);
        var (_, gb) = await GetAsync("/api/timeline?system=gb");
        Assert.That(gb.GetProperty("igdbConfigured").GetBoolean(), Is.False);
        Assert.That(gb.GetProperty("total").GetInt32(), Is.EqualTo(3));
    }

    [Test]
    public async Task Years_EmptyLibrary_HasNoYears()
    {
        var (status, body) = await GetAsync("/api/timeline");
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((body.GetProperty("total").GetInt32(), body.GetProperty("years").GetArrayLength()), Is.EqualTo((0, 0)));
    }

    [TestCase("/api/timeline?system=nope")]
    [TestCase("/api/timeline/games?system=nope")]
    public async Task UnknownSystem_Is400(string url)
    {
        var (status, body) = await GetAsync(url);
        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("errors").GetProperty("system")[0].GetString(), Is.EqualTo("Choose a registered system."));
    }

    [Test]
    public async Task Games_Default_LeaveOutYearlessAndUnlinked_SortedByYearThenTitle_50APage()
    {
        await SeedAsync();

        var (status, body) = await GetAsync("/api/timeline/games");
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Titles(body), Is.EqualTo(new[] { "Early", "Alpha", "Zeta", "Late" }));
        Assert.That((body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("total").GetInt64()),
            Is.EqualTo((1, 50, 4L)));
        Assert.That(body.GetProperty("items")[1].GetProperty("igdbId").GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public async Task Games_RangeIsInclusive_SystemAndPagingApply()
    {
        await SeedAsync();

        var (_, range) = await GetAsync("/api/timeline/games?from=1990&to=1995");
        Assert.That(Titles(range), Is.EqualTo(new[] { "Alpha", "Zeta", "Late" }));
        var (_, gbc) = await GetAsync("/api/timeline/games?system=gbc");
        Assert.That(Titles(gbc), Is.EqualTo(new[] { "Early" }));
        var (_, paged) = await GetAsync("/api/timeline/games?page=2&pageSize=2");
        Assert.That(Titles(paged), Is.EqualTo(new[] { "Zeta", "Late" }));
    }

    [TestCase("from=0", "from", "Must be 1–9999.")]
    [TestCase("to=10000", "to", "Must be 1–9999.")]
    [TestCase("from=1995&to=1990", "from", "Must not be after to.")]
    [TestCase("pageSize=0", "pageSize", null)]
    public async Task Games_InvalidQuery_Is400(string query, string field, string? message)
    {
        var (status, body) = await GetAsync($"/api/timeline/games?{query}");
        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
        var errors = body.GetProperty("errors");
        Assert.That(errors.TryGetProperty(field, out var error), Is.True, body.ToString());
        if (message is not null)
            Assert.That(error[0].GetString(), Is.EqualTo(message));
    }
}
