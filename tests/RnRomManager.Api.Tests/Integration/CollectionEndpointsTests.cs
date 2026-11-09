using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Library;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B1–B7, B13–B14: <c>/api/collections*</c> against MariaDB, with two plain users.</summary>
[Category("Integration")]
public class CollectionEndpointsTests
{
    private ApiFactory _factory = null!;
    private AuthClient _alice = null!;
    private AuthClient _bob = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _alice = await SignInAsync("alice");
        _bob = await SignInAsync("bob");
    }

    [TearDown]
    public void TearDown()
    {
        _alice?.Dispose();
        _bob?.Dispose();
        _factory?.Dispose();
    }

    private async Task<AuthClient> SignInAsync(string username)
    {
        var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, username);
        (await client.LoginAsync(username, TestUsers.Password)).Dispose();
        return client;
    }

    private async Task<Guid> AddGameAsync(string title, int seed)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(seed)));
        await _factory.Services.GetRequiredService<ILibraryMirror>()
            .ReplaceAsync(new MirrorEntry(game, $"gb/{title[0]}/{title}.zip", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    private static async Task<string> CreateAsync(AuthClient client, string name, string? notes = null)
    {
        using var response = await client.PostAsync("/api/collections", new { name, notes });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        return (await AuthClient.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    [Test]
    public async Task Crud_CreateGetListPatchDelete()
    {
        using (var created = await _alice.PostAsync("/api/collections", new { name = " Pokémon ", notes = "a\r\nb" }))
        {
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            var body = await AuthClient.JsonAsync(created);
            Assert.That(created.Headers.Location!.ToString(), Is.EqualTo($"/api/collections/{body.GetProperty("id").GetString()}"));
            Assert.That(body.GetProperty("name").GetString(), Is.EqualTo("Pokémon"));
            Assert.That(body.GetProperty("notes").GetString(), Is.EqualTo("a\nb"));
            Assert.That(body.GetProperty("gameCount").GetInt64(), Is.Zero);
            Assert.That(body.GetProperty("coverUrl").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
        }
        var id = await CreateAsync(_alice, "Favourites");

        using (var patched = await _alice.PatchAsync($"/api/collections/{id}", new { name = "My favourites" }))
        {
            Assert.That(patched.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await AuthClient.JsonAsync(patched);
            Assert.That(body.GetProperty("name").GetString(), Is.EqualTo("My favourites"));
        }
        using (var cleared = await _alice.SendAsync(HttpMethod.Patch, $"/api/collections/{id}", new { notes = (string?)null }, sendXsrf: true))
            Assert.That((await AuthClient.JsonAsync(cleared)).GetProperty("notes").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));

        using (var list = await _alice.GetAsync("/api/collections?q=pokemon"))
        {
            var body = await AuthClient.JsonAsync(list);
            Assert.That(body.GetProperty("total").GetInt64(), Is.EqualTo(1));
            var item = body.GetProperty("items")[0];
            Assert.That(item.GetProperty("name").GetString(), Is.EqualTo("Pokémon"));
            Assert.That(item.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "id", "name", "gameCount", "coverUrl", "updatedAt" }));
        }

        using (var deleted = await _alice.SendAsync(HttpMethod.Delete, $"/api/collections/{id}", null, sendXsrf: true))
            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using (var gone = await _alice.GetAsync($"/api/collections/{id}"))
            Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AnotherUsersCollection_Is404_ForEveryRoute_AndListsAreSeparate()
    {
        var id = await CreateAsync(_alice, "Mine");
        var game = await AddGameAsync("Tetris", 1);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, $"/api/collections/{id}", null),
            (HttpMethod.Patch, $"/api/collections/{id}", new { name = "Stolen" }),
            (HttpMethod.Post, $"/api/collections/{id}/games", new { gameIds = new[] { game } }),
            (HttpMethod.Delete, $"/api/collections/{id}/games/{game}", null),
            (HttpMethod.Delete, $"/api/collections/{id}", null),
        };
        foreach (var (method, url, body) in calls)
        {
            using var response = await _bob.SendAsync(method, url, body, sendXsrf: true);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), $"{method} {url}");
        }

        using (var bobs = await _bob.GetAsync("/api/collections"))
            Assert.That((await AuthClient.JsonAsync(bobs)).GetProperty("total").GetInt64(), Is.Zero);
        using (var alices = await _alice.GetAsync($"/api/collections/{id}"))
        {
            var body = await AuthClient.JsonAsync(alices);
            Assert.That((body.GetProperty("name").GetString(), body.GetProperty("gameCount").GetInt64()), Is.EqualTo(("Mine", 0L)));
        }
    }

    [Test]
    public async Task DuplicateName_Is409_ForTheSameUserOnly()
    {
        await CreateAsync(_alice, "Pokemon");
        var other = await CreateAsync(_alice, "Other");

        using (var duplicate = await _alice.PostAsync("/api/collections", new { name = "POKEMON" }))
        {
            Assert.That(duplicate.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(duplicate), Is.EqualTo("CollectionNameTaken"));
        }
        using (var rename = await _alice.PatchAsync($"/api/collections/{other}", new { name = "pokemon" }))
            Assert.That(await AuthClient.CodeAsync(rename), Is.EqualTo("CollectionNameTaken"));
        await CreateAsync(_bob, "Pokemon");
    }

    [Test]
    public async Task Validation_Is400_WithTheField()
    {
        var id = await CreateAsync(_alice, "Mine");

        async Task<string[]> FieldsAsync(HttpResponseMessage response)
        {
            using (response)
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                return (await AuthClient.JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name).ToArray();
            }
        }

        Assert.That(await FieldsAsync(await _alice.PostAsync("/api/collections", new { name = "" })), Is.EqualTo(new[] { "name" }));
        Assert.That(await FieldsAsync(await _alice.PostAsync("/api/collections", new { name = new string('x', 101) })), Is.EqualTo(new[] { "name" }));
        Assert.That(await FieldsAsync(await _alice.PostAsync("/api/collections", new { name = "n", notes = new string('x', 65537) })), Is.EqualTo(new[] { "notes" }));
        Assert.That(await FieldsAsync(await _alice.PatchAsync($"/api/collections/{id}", new { name = (string?)null })), Is.EqualTo(new[] { "name" }));
        Assert.That(await FieldsAsync(await _alice.PatchAsync($"/api/collections/{id}", new { notes = 5 })), Is.EqualTo(new[] { "notes" }));
        Assert.That(await FieldsAsync(await _alice.GetAsync("/api/collections?pageSize=201")), Is.EqualTo(new[] { "pageSize" }));
        Assert.That(await FieldsAsync(await _alice.GetAsync("/api/collections?q=1+2+3+4+5+6+7+8+9")), Is.EqualTo(new[] { "q" }));
    }

    [Test]
    public async Task AddAndRemoveGames()
    {
        var id = await CreateAsync(_alice, "Mine");
        var tetris = await AddGameAsync("Tetris", 1);
        var zelda = await AddGameAsync("Zelda", 2);

        async Task<long> AddedAsync(params Guid[] gameIds)
        {
            using var response = await _alice.PostAsync($"/api/collections/{id}/games", new { gameIds });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            return (await AuthClient.JsonAsync(response)).GetProperty("added").GetInt64();
        }

        Assert.That(await AddedAsync(tetris, Guid.NewGuid()), Is.EqualTo(1));
        Assert.That(await AddedAsync(tetris, zelda), Is.EqualTo(1));
        using (var detail = await _alice.GetAsync($"/api/collections/{id}"))
            Assert.That((await AuthClient.JsonAsync(detail)).GetProperty("gameCount").GetInt64(), Is.EqualTo(2));

        using (var removed = await _alice.SendAsync(HttpMethod.Delete, $"/api/collections/{id}/games/{tetris}", null, sendXsrf: true))
            Assert.That(removed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using (var again = await _alice.SendAsync(HttpMethod.Delete, $"/api/collections/{id}/games/{tetris}", null, sendXsrf: true))
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using (var detail = await _alice.GetAsync($"/api/collections/{id}"))
            Assert.That((await AuthClient.JsonAsync(detail)).GetProperty("gameCount").GetInt64(), Is.EqualTo(1));
    }

    [TestCase(0)]
    [TestCase(501)]
    public async Task AddGames_OutsideOneTo500Ids_Is400(int count)
    {
        var id = await CreateAsync(_alice, "Mine");

        using var response = await _alice.PostAsync($"/api/collections/{id}/games",
            new { gameIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray() });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("gameIds", out _), Is.True);
    }

    [Test]
    public async Task Mutations_WithoutTheAntiforgeryToken_Are400()
    {
        var id = await CreateAsync(_alice, "Mine");

        using var create = await _alice.PostAsync("/api/collections", new { name = "x" }, sendXsrf: false);
        using var patch = await _alice.SendAsync(HttpMethod.Patch, $"/api/collections/{id}", new { name = "y" }, sendXsrf: false);
        using var delete = await _alice.SendAsync(HttpMethod.Delete, $"/api/collections/{id}", null, sendXsrf: false);
        using var add = await _alice.PostAsync($"/api/collections/{id}/games", new { gameIds = new[] { Guid.NewGuid() } }, sendXsrf: false);

        foreach (var response in new[] { create, patch, delete, add })
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), response.RequestMessage!.Method.Method);
        using var still = await _alice.GetAsync($"/api/collections/{id}");
        Assert.That((await AuthClient.JsonAsync(still)).GetProperty("name").GetString(), Is.EqualTo("Mine"));
    }

    private async Task<string[]> TitlesAsync(AuthClient client, string query)
    {
        using var response = await client.GetAsync($"/api/games?{query}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), query);
        return (await AuthClient.JsonAsync(response)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("displayTitle").GetString()!).ToArray();
    }

    [Test]
    public async Task GamesList_CollectionFilter_KeepsOtherFilters_AndSortsByAddedToCollection_B15()
    {
        var id = await CreateAsync(_alice, "Mine");
        var alpha = await AddGameAsync("Alpha", 1);
        var bravo = await AddGameAsync("Bravo", 2);
        await AddGameAsync("Charlie", 3);
        foreach (var game in new[] { bravo, alpha }) // bravo first: added order differs from title order
            (await _alice.PostAsync($"/api/collections/{id}/games", new { gameIds = new[] { game } })).Dispose();

        Assert.That(await TitlesAsync(_alice, $"collection={id}"), Is.EqualTo(new[] { "Alpha", "Bravo" }));
        Assert.That(await TitlesAsync(_alice, $"collection={id}&sort=added"), Is.EqualTo(new[] { "Alpha", "Bravo" }), "default desc");
        Assert.That(await TitlesAsync(_alice, $"collection={id}&sort=added&dir=asc"), Is.EqualTo(new[] { "Bravo", "Alpha" }));
        Assert.That(await TitlesAsync(_alice, $"collection={id}&q=brav"), Is.EqualTo(new[] { "Bravo" }));
    }

    [Test]
    public async Task GamesList_ForeignOrUnknownCollection_Is404_AndMalformedIs400()
    {
        var id = await CreateAsync(_alice, "Mine");

        using (var foreign = await _bob.GetAsync($"/api/games?collection={id}"))
            Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var unknown = await _alice.GetAsync($"/api/games?collection={Guid.NewGuid()}"))
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using var malformed = await _alice.GetAsync("/api/games?collection=nope");
        Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(malformed)).GetProperty("errors").TryGetProperty("collection", out _), Is.True);
    }

    [Test]
    public async Task GameCollections_AreTheCallersOnly_SortedByName_B16()
    {
        var game = await AddGameAsync("Tetris", 1);
        var b = await CreateAsync(_alice, "b");
        var a = await CreateAsync(_alice, "A");
        await CreateAsync(_alice, "without");
        var bobs = await CreateAsync(_bob, "bobs");
        foreach (var (client, id) in new[] { (_alice, b), (_alice, a), (_bob, bobs) })
            (await client.PostAsync($"/api/collections/{id}/games", new { gameIds = new[] { game } })).Dispose();

        using var response = await _alice.GetAsync($"/api/games/{game}/collections");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var list = (await AuthClient.JsonAsync(response)).EnumerateArray()
            .Select(c => (c.GetProperty("id").GetString(), c.GetProperty("name").GetString())).ToArray();
        Assert.That(list, Is.EqualTo(new[] { (a, "A"), (b, "b") }));
        using var unknown = await _alice.GetAsync($"/api/games/{Guid.NewGuid()}/collections");
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
