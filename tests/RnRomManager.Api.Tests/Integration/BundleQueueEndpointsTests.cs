using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Bundles;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Bundle Creator B2/B4: <c>/api/bundle-queues*</c> against MariaDB, with two plain users.</summary>
[Category("Integration")]
public partial class BundleQueueEndpointsTests
{
    private const string Game = "8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Sha2 = "fedcba9876543210fedcba9876543210fedcba98";

    private ApiFactory _factory = null!;
    private AuthClient _alice = null!;
    private AuthClient _bob = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        _alice = await SignInAsync("alice");
        _bob = await SignInAsync("bob");
    }

    [TearDown]
    public void TearDown()
    {
        _alice?.Dispose();
        _bob?.Dispose();
    }

    private async Task<AuthClient> SignInAsync(string username)
    {
        var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, username);
        (await client.LoginAsync(username, TestUsers.Password)).Dispose();
        return client;
    }

    private static object MainEntry(string sha = Sha, string title = "Orbit Blocks", string file = "Orbit Blocks (World).gb") =>
        new { gameId = Game, releaseSha1 = sha, member = (object?)null, gameTitle = title, fileName = file };

    private static object SetEntry(string name) =>
        new { gameId = Game, releaseSha1 = Sha, member = new { sha1 = Sha2, fileName = name }, gameTitle = "Disc Runner", fileName = name };

    private static async Task<JsonElement> CreateAsync(AuthClient client, string name, params object[] entries)
    {
        using var response = await client.PostAsync("/api/bundle-queues", new { name, entries });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created), await response.Content.ReadAsStringAsync());
        return await AuthClient.JsonAsync(response);
    }

    [Test]
    public async Task Create_ThenLoad_RestoresTheOrderedSnapshot()
    {
        object[] entries = [SetEntry("Disc Runner (USA) (Track 2).bin"), MainEntry(title: "Pokémon – Rouge", file: "Pokémon.gb"), SetEntry("Disc Runner (USA).cue")];

        using var created = await _alice.PostAsync("/api/bundle-queues", new { name = "  Weekend  ", entries });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var body = await AuthClient.JsonAsync(created);
        var id = body.GetProperty("id").GetString();
        Assert.That(created.Headers.Location!.ToString(), Is.EqualTo($"/api/bundle-queues/{id}"));
        Assert.That(body.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "id", "name", "revision", "entries" }));
        Assert.That((body.GetProperty("name").GetString(), body.GetProperty("revision").GetInt32()), Is.EqualTo(("Weekend", 1)));

        using var loaded = await _alice.GetAsync($"/api/bundle-queues/{id}");
        var snapshot = await AuthClient.JsonAsync(loaded);
        Assert.That(snapshot.GetProperty("entries").ToString(), Is.EqualTo(body.GetProperty("entries").ToString()));
        var items = snapshot.GetProperty("entries").EnumerateArray().ToList();
        Assert.That(items.Select(e => e.GetProperty("fileName").GetString()),
            Is.EqualTo(new[] { "Disc Runner (USA) (Track 2).bin", "Pokémon.gb", "Disc Runner (USA).cue" }));
        Assert.That(items[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "gameId", "releaseSha1", "member", "gameTitle", "fileName" }));
        Assert.That(items[0].GetProperty("member").GetProperty("sha1").GetString(), Is.EqualTo(Sha2));
        Assert.That(items[1].GetProperty("member").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(items[1].GetProperty("gameTitle").GetString(), Is.EqualTo("Pokémon – Rouge"));
    }

    [Test]
    public async Task UnresolvedButWellFormedIdentities_AreAccepted()
    {
        // No game in this library has this GUID or hash.
        var body = await CreateAsync(_alice, "Later", MainEntry());

        Assert.That(body.GetProperty("entries").GetArrayLength(), Is.EqualTo(1));
    }

    [Test]
    public async Task List_IsTheCallersOnly_SortedByName_WithSummaries()
    {
        await CreateAsync(_alice, "b", MainEntry(), SetEntry("x.bin"));
        await CreateAsync(_alice, "A");
        await CreateAsync(_bob, "Bob's");

        using var response = await _alice.GetAsync("/api/bundle-queues");
        var list = (await AuthClient.JsonAsync(response)).EnumerateArray().ToList();

        Assert.That(list.Select(q => (q.GetProperty("name").GetString(), q.GetProperty("itemCount").GetInt32())),
            Is.EqualTo(new[] { ("A", 0), ("b", 2) }));
        Assert.That(list[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "id", "name", "revision", "itemCount" }));
    }

    [Test]
    public async Task AnotherUsersQueue_Is404_AndAGuessedIdToo()
    {
        var id = (await CreateAsync(_alice, "Mine", MainEntry())).GetProperty("id").GetString();

        using (var foreign = await _bob.GetAsync($"/api/bundle-queues/{id}"))
            Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var guessed = await _alice.GetAsync($"/api/bundle-queues/{Guid.NewGuid()}"))
            Assert.That(guessed.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var notAGuid = await _alice.GetAsync("/api/bundle-queues/123"))
            Assert.That(notAGuid.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Names_AreUniquePerUser_IgnoringCase_ButNotAccents()
    {
        await CreateAsync(_alice, "Pokemon");

        using (var taken = await _alice.PostAsync("/api/bundle-queues", new { name = " POKEMON ", entries = Array.Empty<object>() }))
        {
            Assert.That(taken.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(taken), Is.EqualTo("BundleQueueNameTaken"));
        }
        await CreateAsync(_alice, "Pokémon");
        await CreateAsync(_bob, "pokemon");
    }

    [Test]
    public async Task TheAccountCap_Is409_AfterTheNameCheck()
    {
        for (var i = 0; i < BundleQueueLimits.MaxQueuesPerUser; i++)
            await CreateAsync(_alice, $"q{i}");

        using (var limit = await _alice.PostAsync("/api/bundle-queues", new { name = "one more", entries = Array.Empty<object>() }))
            Assert.That(await AuthClient.CodeAsync(limit), Is.EqualTo("BundleQueueLimit"));
        using (var name = await _alice.PostAsync("/api/bundle-queues", new { name = "Q1", entries = Array.Empty<object>() }))
            Assert.That(await AuthClient.CodeAsync(name), Is.EqualTo("BundleQueueNameTaken"));
        await CreateAsync(_bob, "one more");
    }

    [Test]
    public async Task MalformedRequests_Are400_WithFieldErrors()
    {
        async Task<string[]> FieldsAsync(object body)
        {
            using var response = await _alice.PostAsync("/api/bundle-queues", body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            return (await AuthClient.JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name).ToArray();
        }

        Assert.That(await FieldsAsync(new { name = " ", entries = Array.Empty<object>() }), Is.EqualTo(new[] { "name" }));
        Assert.That(await FieldsAsync(new { name = new string('a', 101), entries = Array.Empty<object>() }), Is.EqualTo(new[] { "name" }));
        Assert.That(await FieldsAsync(new { name = "x" }), Is.EqualTo(new[] { "entries" }));
        Assert.That(await FieldsAsync(new { name = "x", entries = new[] { MainEntry(), MainEntry(file: "relabelled.gb") } }), Is.EqualTo(new[] { "entries[1]" }));
        Assert.That(await FieldsAsync(new { name = "x", entries = new[] { MainEntry(sha: Sha.ToUpperInvariant()) } }), Is.EqualTo(new[] { "entries[0].releaseSha1" }));
        Assert.That(await FieldsAsync(new { name = "x", entries = new object[] { new { gameId = Guid.Empty, releaseSha1 = Sha, gameTitle = "g", fileName = "f" } } }),
            Is.EqualTo(new[] { "entries[0].gameId" }));
        Assert.That(await FieldsAsync(new { name = "x", entries = Enumerable.Range(0, 501).Select(i => MainEntry(sha: i.ToString("x40"))).ToArray() }),
            Is.EqualTo(new[] { "entries" }));

        using var notJson = await _alice.SendContentAsync(HttpMethod.Post, "/api/bundle-queues", new StringContent("{ nope", Encoding.UTF8, "application/json"));
        Assert.That(notJson.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var wrongType = await _alice.SendContentAsync(HttpMethod.Post, "/api/bundle-queues",
            new StringContent("""{ "name": "x", "entries": [ { "gameId": 5 } ] }""", Encoding.UTF8, "application/json"));
        Assert.That(wrongType.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task FiveHundredFiles_WithLongLabels_FitTheLimit()
    {
        var label = string.Concat(Enumerable.Repeat("🎮", 255));
        // Unescaped UTF-8, as the SPA's JSON.stringify sends it (System.Text.Json would escape every astral character):
        // 500 × 2 × 255 four-byte characters, about 1 MiB.
        var entries = Enumerable.Range(0, 500).Select(i =>
            $$"""{"gameId":"{{Game}}","releaseSha1":"{{i:x40}}","member":null,"gameTitle":"{{label}}","fileName":"{{label}}"}""");
        var json = $$"""{"name":"Big","entries":[{{string.Join(',', entries)}}]}""";
        Assert.That(Encoding.UTF8.GetByteCount(json), Is.LessThan(BundleQueueEndpoints.RequestLimit));

        using var response = await _alice.SendContentAsync(HttpMethod.Post, "/api/bundle-queues", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("entries").GetArrayLength(), Is.EqualTo(500));
    }

    [TestCase(false, TestName = "Oversized queue body with Content-Length → 413")]
    [TestCase(true, TestName = "Oversized chunked queue body → 413")]
    public async Task OversizedBody_Is413(bool chunked)
    {
        var json = $$"""{ "name": "x", "entries": [], "pad": "{{new string('a', BundleQueueEndpoints.RequestLimit)}}" }""";
        HttpContent content = new StringContent(json, Encoding.UTF8, "application/json");
        if (chunked)
            content = new UnknownLengthContent(content);

        using var response = await _alice.SendContentAsync(HttpMethod.Post, "/api/bundle-queues", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("PayloadTooLarge"));
        using var list = await _alice.GetAsync("/api/bundle-queues");
        Assert.That((await AuthClient.JsonAsync(list)).GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task Writes_NeedTheAntiforgeryToken()
    {
        using var response = await _alice.PostAsync("/api/bundle-queues", new { name = "x", entries = Array.Empty<object>() }, sendXsrf: false);

        Assert.That((int)response.StatusCode, Is.EqualTo(400).Or.EqualTo(403));
    }

    /// <summary>A body without a Content-Length, so the server sees it chunked.</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly HttpContent _inner;

        public UnknownLengthContent(HttpContent inner)
        {
            _inner = inner;
            Headers.ContentType = inner.Headers.ContentType ?? new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
