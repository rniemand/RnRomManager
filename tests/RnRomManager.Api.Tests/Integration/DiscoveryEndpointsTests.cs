using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RnRomManager.Core.Database;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Library;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Collections discovery B3: <c>/api/discovery/{kind}*</c> against MariaDB, as a plain signed-in user.</summary>
[Category("Integration")]
public class DiscoveryEndpointsTests
{
    // The index's own clock: each test moves it past the index lifetime, so no index outlives a database reset.
    private readonly FakeTimeProvider _indexClock = new();
    private ApiFactory _factory = null!;
    private AuthClient _user = null!;
    private int _seed;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => services.AddSingleton<IDiscoveryRepository>(sp => new DiscoveryRepository(
                sp.GetRequiredService<IDbConnectionFactory>(), sp.GetRequiredService<IGameRepository>(), _indexClock)),
        };
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        _indexClock.Advance(IDiscoveryRepository.IndexLifetime);
        _user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "alice");
        (await _user.LoginAsync("alice", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown() => _user?.Dispose();

    private static string Key(string normalized) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    private async Task<Guid> AddGameAsync(string title, string system, string[] publishers, string[]? genres = null, string[]? developers = null)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with
        {
            System = system, Publishers = publishers, Genres = genres ?? [], Developers = developers ?? [],
        };
        await _factory.Services.GetRequiredService<ILibraryMirror>()
            .ReplaceAsync(new MirrorEntry(game, $"{system}/{title[0]}/{title}.zip", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(AuthClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Test]
    public async Task Groups_ListKeyNameAndCount_AcrossSystems()
    {
        await AddGameAsync("Tetris", "gb", ["Nintendo"], ["Puzzle"]);
        await AddGameAsync("Mario", "nes", [" NINTENDO", "Hudson"], ["Platform"]);
        await AddGameAsync("Sonic", "megadrive", ["Sega"], ["Platform"]);

        var (status, body) = await GetAsync(_user, "/api/discovery/publishers");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("page").GetInt32(), Is.EqualTo(1));
        Assert.That(body.GetProperty("pageSize").GetInt32(), Is.EqualTo(Page<object>.DefaultSize));
        Assert.That(body.GetProperty("total").GetInt64(), Is.EqualTo(3));
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.That(items.Select(i => i.GetProperty("name").GetString()), Is.EqualTo(new[] { "Hudson", "NINTENDO", "Sega" }));
        Assert.That(items[1].GetProperty("key").GetString(), Is.EqualTo(Key("nintendo")));
        Assert.That(items[1].GetProperty("gameCount").GetInt64(), Is.EqualTo(2));
        Assert.That(items[1].EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "key", "name", "gameCount", "logoUrl" }));
        Assert.That(items[1].GetProperty("logoUrl").ValueKind, Is.EqualTo(JsonValueKind.Null), "no override");

        var (_, genres) = await GetAsync(_user, "/api/discovery/genres?page=2&pageSize=1");
        Assert.That(genres.GetProperty("total").GetInt64(), Is.EqualTo(2));
        Assert.That(genres.GetProperty("items")[0].GetProperty("name").GetString(), Is.EqualTo("Puzzle"));
    }

    /// <summary>B16: <c>q</c> filters by label before paging; exactly 100 chars (after trim) is allowed, blank is absent.</summary>
    [Test]
    public async Task Groups_Q_FiltersByName_BeforePaging()
    {
        await AddGameAsync("Tetris", "gb", ["Nintendo"], ["Puzzle"]);
        await AddGameAsync("Sonic", "megadrive", ["Sega", "Sega Toys"], ["Platform"]);

        var (status, body) = await GetAsync(_user, "/api/discovery/publishers?q=%20sEgA%20&pageSize=1&page=2");
        var (_, blank) = await GetAsync(_user, "/api/discovery/publishers?q=%20%20");
        var (atLimit, _) = await GetAsync(_user, "/api/discovery/publishers?q=" + new string('a', 100));

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("total").GetInt64(), Is.EqualTo(2));
        Assert.That(body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()), Is.EqualTo(new[] { "Sega Toys" }));
        Assert.That(blank.GetProperty("total").GetInt64(), Is.EqualTo(3));
        Assert.That(atLimit, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>A1: sort/dir are case-insensitive; a missing dir is the sort's default (games → descending).</summary>
    [TestCase("", "Hudson,NINTENDO,Sega")]
    [TestCase("?sort=name&dir=desc", "Sega,NINTENDO,Hudson")]
    [TestCase("?SORT=Games", "NINTENDO,Hudson,Sega")]
    [TestCase("?sort=games&dir=DESC", "NINTENDO,Hudson,Sega")]
    [TestCase("?sort=games&dir=asc", "Hudson,Sega,NINTENDO")]
    public async Task Groups_SortAndDir_ChangeTheOrder(string query, string expected)
    {
        await AddGameAsync("Tetris", "gb", ["Nintendo"]);
        await AddGameAsync("Mario", "nes", [" NINTENDO", "Hudson"]);
        await AddGameAsync("Sonic", "megadrive", ["Sega"]);

        var (status, body) = await GetAsync(_user, "/api/discovery/publishers" + query);

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()), Is.EqualTo(expected.Split(',')));
    }

    [Test]
    public async Task GenreGroups_CarryTheirOwnLogoUrl()
    {
        await AddGameAsync("Tetris", "gb", ["Puzzle"], ["Puzzle"]);
        var at = new DateTime(2026, 11, 17, 4, 0, 0, DateTimeKind.Utc);
        await _factory.Services.GetRequiredService<IDiscoveryLogoRepository>().UpsertAsync(
            new DiscoveryLogo(LogoKind.Genres, Key("puzzle"), "image/png", [1], null, at), default);

        var (_, genres) = await GetAsync(_user, "/api/discovery/genres");
        var (_, publishers) = await GetAsync(_user, "/api/discovery/publishers");

        Assert.That(genres.GetProperty("items")[0].GetProperty("logoUrl").GetString(),
            Is.EqualTo($"/api/discovery/genres/logo?key={Key("puzzle")}&v={at.Ticks}"));
        Assert.That(publishers.GetProperty("items")[0].GetProperty("logoUrl").ValueKind, Is.EqualTo(JsonValueKind.Null),
            "the same key as a publisher has no logo");
    }

    [Test]
    public async Task Groups_NoGames_IsAnEmptyPage()
    {
        var (status, body) = await GetAsync(_user, "/api/discovery/genres");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("items").GetArrayLength(), Is.Zero);
        Assert.That(body.GetProperty("total").GetInt64(), Is.Zero);
    }

    [Test]
    public async Task Games_ReturnTheLibraryListContract_ForTheGroupOnly()
    {
        await AddGameAsync("Tetris", "gb", ["Nintendo"]);
        await AddGameAsync("Mario", "nes", ["nintendo"]);
        await AddGameAsync("Sonic", "megadrive", ["Sega"]);

        var (status, body) = await GetAsync(_user, $"/api/discovery/publishers/games?key={Key("nintendo")}");
        var (_, library) = await GetAsync(_user, "/api/games");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("total").GetInt64(), Is.EqualTo(2));
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.That(items.Select(i => i.GetProperty("displayTitle").GetString()), Is.EqualTo(new[] { "Mario", "Tetris" }));
        var expected = library.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("system").GetString() != "megadrive").ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetRawText());
        foreach (var item in items)
            Assert.That(item.GetRawText(), Is.EqualTo(expected[item.GetProperty("id").GetString()!]), "same row as /api/games");
        Assert.That(items[0].GetProperty("systemName").GetString(), Is.Not.EqualTo("nes"), "the registered system label");
    }

    [Test]
    public async Task Games_Page()
    {
        for (var i = 0; i < 5; i++)
            await AddGameAsync($"Game {i}", "gb", ["Konami"]);

        var (_, body) = await GetAsync(_user, $"/api/discovery/publishers/games?key={Key("konami")}&page=2&pageSize=2");

        Assert.That((body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("total").GetInt64()),
            Is.EqualTo((2, 2, 5L)));
        Assert.That(body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("displayTitle").GetString()),
            Is.EqualTo(new[] { "Game 2", "Game 3" }));
    }

    [TestCase("/api/discovery/studios")]
    [TestCase("/api/discovery/Publishers")]
    [TestCase("/api/discovery/publisher")]
    [TestCase("/api/discovery/studios/games?key=c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516")]
    [TestCase("/api/discovery/studios/key?name=Nintendo")]
    public async Task UnknownKind_Is404(string url)
    {
        Assert.That((await GetAsync(_user, url)).Status, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Developers_GroupGamesDevelopers_LikePublishers()
    {
        var tetris = await AddGameAsync("Tetris", "gb", ["Nintendo"], developers: ["Bullet-Proof"]);
        await AddGameAsync("Mario", "nes", ["Nintendo"], developers: [" bullet-proof", "Nintendo R&D1"]);

        var (status, body) = await GetAsync(_user, "/api/discovery/developers");
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.That(items.Select(i => (i.GetProperty("name").GetString(), i.GetProperty("gameCount").GetInt64())),
            Is.EqualTo(new[] { ("Bullet-Proof", 2L), ("Nintendo R&D1", 1L) }));

        var (_, games) = await GetAsync(_user, $"/api/discovery/developers/games?key={Key("bullet-proof")}");
        Assert.That(games.GetProperty("total").GetInt64(), Is.EqualTo(2));
        Assert.That(games.GetProperty("items").EnumerateArray().Select(g => g.GetProperty("id").GetGuid()), Does.Contain(tetris));
    }

    [TestCase("publishers", "nintendo", "nintendo")]
    [TestCase("developers", "  BULLET-PROOF ", "bullet-proof")]
    [TestCase("genres", "Puzzle", "puzzle")]
    public async Task Key_ByName_IsCaseAndSpaceInsensitive(string kind, string name, string normalized)
    {
        await AddGameAsync("Tetris", "gb", ["Nintendo"], ["Puzzle"], ["Bullet-Proof"]);

        var (status, body) = await GetAsync(_user, $"/api/discovery/{kind}/key?name={Uri.EscapeDataString(name)}");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("key").GetString(), Is.EqualTo(Key(normalized)));
    }

    [Test]
    public async Task Key_ByName_NoGameHasIt_Is404()
    {
        await AddGameAsync("Tetris", "gb", ["Nintendo"], ["Puzzle"]);

        Assert.That((await GetAsync(_user, "/api/discovery/publishers/key?name=Sega")).Status, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await GetAsync(_user, "/api/discovery/genres/key?name=Nintendo")).Status, Is.EqualTo(HttpStatusCode.NotFound),
            "names belong to their kind");
    }

    [TestCase("")]
    [TestCase("?name=")]
    [TestCase("?name=%20%20")]
    public async Task Key_ByName_InvalidName_Is400(string query)
    {
        var (status, body) = await GetAsync(_user, $"/api/discovery/publishers/key{query}");
        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("errors").GetProperty("name")[0].GetString(), Is.EqualTo("Must be 1–255 characters."));
    }

    [Test]
    public async Task Key_ByName_Over255Characters_Is400()
    {
        var (status, _) = await GetAsync(_user, $"/api/discovery/publishers/key?name={new string('a', 256)}");
        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task UnknownGroup_Is404()
    {
        await AddGameAsync("Tetris", "gb", ["Nintendo"], ["Puzzle"]);

        Assert.That((await GetAsync(_user, $"/api/discovery/publishers/games?key={Key("sega")}")).Status, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await GetAsync(_user, $"/api/discovery/genres/games?key={Key("nintendo")}")).Status, Is.EqualTo(HttpStatusCode.NotFound),
            "keys belong to their kind");
    }

    [TestCase("")]
    [TestCase("?key=")]
    [TestCase("?key=C0A4942143E872CD1AE29FC759E04526DE2E909AC1732734D38550A29C2E2516")]
    [TestCase("?key=c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e251")]
    [TestCase("?key=nintendo")]
    public async Task InvalidKey_Is400(string query)
    {
        var (status, body) = await GetAsync(_user, "/api/discovery/publishers/games" + query);

        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("errors").TryGetProperty("key", out _), Is.True);
    }

    [TestCase("/api/discovery/publishers?page=0", "page")]
    [TestCase("/api/discovery/genres?pageSize=201", "pageSize")]
    [TestCase("/api/discovery/genres?pageSize=0", "pageSize")]
    [TestCase("/api/discovery/publishers/games?key=c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516&page=0", "page")]
    [TestCase("/api/discovery/publishers/games?key=c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516&pageSize=201", "pageSize")]
    [TestCase("/api/discovery/publishers?q=%20aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "q")]
    [TestCase("/api/discovery/publishers?sort=size", "sort")]
    [TestCase("/api/discovery/genres?dir=up", "dir")]
    public async Task InvalidPaging_Is400(string url, string field)
    {
        var (status, body) = await GetAsync(_user, url);

        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }

    [TestCase("/api/discovery/publishers")]
    [TestCase("/api/discovery/genres/games?key=c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516")]
    public async Task Anonymous_Is401(string url)
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);

        Assert.That((await GetAsync(anonymous, url)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task GameList_SaveCount_IsTheSiteUsersOwn_AndTheTotalForAnAdmin()
    {
        var id = await AddGameAsync("Tetris", "gb", ["Nintendo"]);
        await using (var connection = _factory.Services.GetRequiredService<IDbConnectionFactory>().Create())
            await Dapper.SqlMapper.ExecuteAsync(connection, """
                UPDATE games SET save_count = 3 WHERE id = @id;
                INSERT INTO game_user_save_counts (game_id, username, save_count) VALUES (@id, 'alice', 2);
                """, new { id });
        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        var url = $"/api/discovery/publishers/games?key={Key("nintendo")}";

        var (_, own) = await GetAsync(_user, url);
        var (_, all) = await GetAsync(admin, url);

        Assert.That(own.GetProperty("items")[0].GetProperty("saveCount").GetInt32(), Is.EqualTo(2));
        Assert.That(all.GetProperty("items")[0].GetProperty("saveCount").GetInt32(), Is.EqualTo(3));
    }
}
