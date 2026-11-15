using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Bundle Creator B5: <c>POST /api/bundle-files/resolve</c> against MariaDB.</summary>
[Category("Integration")]
public class BundleFileEndpointsTests
{
    private const string Url = "/api/bundle-files/resolve";

    private ApiFactory _factory = null!;
    private AuthClient _alice = null!;

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
        _alice = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "alice");
        (await _alice.LoginAsync("alice", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown() => _alice?.Dispose();

    private static InfoJsonRomFile File(string name, int seed)
    {
        var bytes = RomBytes(seed);
        return new InfoJsonRomFile(name, name, name, bytes.Length, Crc32.ToHex(bytes),
            Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA1.HashData(bytes)));
    }

    private static InfoJsonGame DiscGame() => Game("Disc Runner",
        Rom("Disc Runner (USA) (Track 1).bin", RomBytes(1)) with
        {
            ExternalFileName = "Disc Runner (USA) (Track 1).bin",
            Files = [File("Disc Runner (USA).cue", 101), File("Disc Runner (USA) (Track 2).bin", 50)],
        }) with { System = ExternalSetTestHandler.SystemId };

    private Task MirrorAsync(InfoJsonGame game, string zip) =>
        _factory.Services.GetRequiredService<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, zip, 100, When.UtcDateTime), default);

    private static object MainFile(InfoJsonGame game) => new { gameId = game.GameId, releaseSha1 = game.Roms[0].Metadata.Sha1, member = (object?)null };

    private static object Member(InfoJsonGame game, int file, string? name = null)
    {
        var f = game.Roms[0].Files![file];
        return new { gameId = game.GameId, releaseSha1 = game.Roms[0].Metadata.Sha1, member = new { sha1 = f.Sha1, fileName = name ?? f.FileName } };
    }

    private async Task<List<JsonElement>> ResolveAsync(params object[] items)
    {
        using var response = await _alice.PostAsync(Url, new { items });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await AuthClient.JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
    }

    private async Task<(long RomId, long[] FileIds)> IdsAsync(InfoJsonGame game)
    {
        var roms = _factory.Services.GetRequiredService<IRomRepository>();
        var rom = (await roms.ListByGameAsync(game.GameId, default)).Single();
        var files = await roms.ListFilesByGameAsync(game.GameId, default);
        return (rom.Id, files.Select(f => f.Id).ToArray());
    }

    [Test]
    public async Task MixedItems_ResolveInInputOrder_WithCurrentNamesAndLinks()
    {
        var disc = DiscGame();
        var cart = Game("Orbit Blocks", Rom("Orbit Blocks (World).gb", RomBytes(7)));
        await MirrorAsync(disc, "extset/D/Disc Runner.zip");
        await MirrorAsync(cart, "gb/O/Orbit Blocks.zip");
        var (discRom, discFiles) = await IdsAsync(disc);
        var (cartRom, _) = await IdsAsync(cart);
        var unknown = new { gameId = Guid.NewGuid(), releaseSha1 = new string('a', 40), member = (object?)null };

        var items = await ResolveAsync(Member(disc, 1), unknown, MainFile(cart), Member(disc, 1, "disc runner (usa) (track 2).bin"), MainFile(disc), MainFile(cart));

        Assert.That(items.Select(i => i.GetProperty("available").GetBoolean()), Is.EqualTo(new[] { true, false, true, false, true, true }));
        Assert.That(items[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "available", "gameTitle", "fileName", "downloadUrl" }));
        Assert.That((items[0].GetProperty("gameTitle").GetString(), items[0].GetProperty("fileName").GetString(), items[0].GetProperty("downloadUrl").GetString()),
            Is.EqualTo(("Disc Runner", "Disc Runner (USA) (Track 2).bin", $"/api/games/{disc.GameId}/roms/{discRom}/files/{discFiles[1]}/download")));
        Assert.That(items[2].GetProperty("downloadUrl").GetString(), Is.EqualTo($"/api/games/{cart.GameId}/roms/{cartRom}/download"));
        Assert.That(items[4].GetProperty("fileName").GetString(), Is.EqualTo("Disc Runner (USA) (Track 1).bin"));
        foreach (var unavailable in new[] { items[1], items[3] })
            foreach (var field in new[] { "gameTitle", "fileName", "downloadUrl" })
                Assert.That(unavailable.GetProperty(field).ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task AMirrorRewrite_KeepsTheIdentity_AndReturnsTheNewLink()
    {
        var disc = DiscGame();
        await MirrorAsync(disc, "extset/D/Disc Runner.zip");
        var before = (await ResolveAsync(Member(disc, 0)))[0].GetProperty("downloadUrl").GetString();

        await MirrorAsync(disc, "extset/D/Disc Runner.zip");
        var (rom, files) = await IdsAsync(disc);
        var after = (await ResolveAsync(Member(disc, 0)))[0].GetProperty("downloadUrl").GetString();

        Assert.That(after, Is.Not.EqualTo(before));
        Assert.That(after, Is.EqualTo($"/api/games/{disc.GameId}/roms/{rom}/files/{files[0]}/download"));
    }

    [Test]
    public async Task AnEmptyBatch_IsEmpty_And500IsTheLimit()
    {
        Assert.That(await ResolveAsync(), Is.Empty);

        var max = Enumerable.Range(0, 500).Select(i => (object)new { gameId = Guid.NewGuid(), releaseSha1 = i.ToString("x40"), member = (object?)null }).ToArray();
        Assert.That((await ResolveAsync(max)).Select(i => i.GetProperty("available").GetBoolean()).ToList(), Has.Count.EqualTo(500).And.All.False);

        using var over = await _alice.PostAsync(Url, new { items = max.Append(max[0]).ToArray() });
        Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(over)).GetProperty("errors").EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "items" }));
    }

    [Test]
    public async Task MalformedItems_Are400_NamingTheField()
    {
        using var response = await _alice.PostAsync(Url, new
        {
            items = new object?[]
            {
                null,
                new { gameId = Guid.Empty, releaseSha1 = new string('a', 40) },
                new { gameId = Guid.NewGuid(), releaseSha1 = new string('A', 40) },
                new { gameId = Guid.NewGuid(), releaseSha1 = new string('a', 40), member = new { sha1 = "x", fileName = "f" } },
                new { gameId = Guid.NewGuid(), releaseSha1 = new string('a', 40), member = new { sha1 = new string('b', 40), fileName = "" } },
            },
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[]
        {
            "items[0]", "items[1].gameId", "items[2].releaseSha1", "items[3].member.sha1", "items[4].member.fileName",
        }));
        using var missing = await _alice.PostAsync(Url, new { });
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task AnOversizedBody_Is413()
    {
        var json = $$"""{ "items": [], "pad": "{{new string('a', BundleQueueEndpoints.RequestLimit)}}" }""";

        using var response = await _alice.SendContentAsync(HttpMethod.Post, Url, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task SignedOut_Is401()
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);

        using var response = await anonymous.PostAsync(Url, new { items = Array.Empty<object>() });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
