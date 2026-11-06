using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Media;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary><c>/api/reports/*</c> (RP2–RP8) against MariaDB + real ZIPs.</summary>
[Category("Integration")]
public class ReportEndpointsTests
{
    private const string Gb = "Nintendo - Game Boy";
    private const string Gbc = "Nintendo - Game Boy Color";
    private static readonly byte[] Alpha = new SyntheticRomBuilder { Seed = 11, Title = "ALPHA" }.Build();
    private static readonly byte[] Bravo = new SyntheticRomBuilder { Seed = 12, Title = "BRAVO" }.Build();
    private static readonly byte[] Charlie = new SyntheticRomBuilder { Seed = 13, Title = "CHARLIE" }.Build();
    private static readonly byte[] Delta = new SyntheticRomBuilder { Seed = 14, Title = "DELTA" }.Build();
    private static readonly byte[] Echo = new SyntheticRomBuilder { Seed = 15, CgbFlag = 0xC0, Title = "ECHO" }.Build();

    private ApiFactory _factory = null!;
    private AuthClient _user = null!;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        _user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await _user.LoginAsync("plain", TestUsers.Password)).Dispose();
        var cache = Service<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.GbaDat] = [],
            [IngestSeeder.N64Dat] = [],
            [Gb] =
            [
                new FixtureGame("Alpha (World)", "Alpha (World).gb", Alpha),
                new FixtureGame("Bravo (World)", "Bravo (World).gb", Bravo),
                new FixtureGame("Charlie (World)", "Charlie (World).gb", Charlie),
                new FixtureGame("Delta (World)", "Delta (World).gb", Delta),
            ],
            [Gbc] = [new FixtureGame("Echo (World)", "Echo (World).gbc", Echo)],
        });
        cache.Load();
    }

    [TearDown]
    public void TearDown()
    {
        _user.Dispose();
        _factory.Dispose();
    }

    private async Task<Guid> Ingest(string system, string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", system, name);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var file = new IngestFile(system, path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
        var result = await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default);
        return result!.GameId!.Value;
    }

    private async Task Sql(string sql, object args)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        await db.ExecuteAsync(sql, args);
    }

    private async Task<JsonElement> GetJson(string url)
    {
        using var response = await _user.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await AuthClient.JsonAsync(response);
    }

    /// <summary>
    /// Offline ingest gives every game the placeholder cover (<c>has_default_boxart = 1</c>). Alpha keeps it, Bravo loses its
    /// cover, Charlie gets "real" art, Delta drifts into both, Echo (GBC) keeps the placeholder.
    /// </summary>
    private async Task<(Guid Alpha, Guid Bravo, Guid Delta, Guid Echo)> SeedMissingImages()
    {
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        var charlie = await Ingest("gb", "c.gb", Charlie);
        var delta = await Ingest("gb", "d.gb", Delta);
        var echo = await Ingest("gbc", "e.gbc", Echo);
        await Sql("UPDATE images SET is_cover = 0 WHERE game_id = @bravo; UPDATE games SET has_default_boxart = 0 WHERE id = @bravo", new { bravo });
        await Sql("UPDATE games SET has_default_boxart = 0 WHERE id = @charlie", new { charlie });
        await Sql("UPDATE images SET is_cover = 0 WHERE game_id = @delta; UPDATE games SET has_default_boxart = 1 WHERE id = @delta", new { delta });
        return (alpha, bravo, delta, echo);
    }

    private static List<(Guid Id, string Reason)> Rows(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("id").GetGuid(), i.GetProperty("reason").GetString()!)).ToList();

    [Test]
    public async Task MissingImages_ListsNoCoverAndPlaceholder_OnceEach_SortedBySystemThenTitle()
    {
        var (alpha, bravo, delta, echo) = await SeedMissingImages();

        var page = await GetJson("/api/reports/missing-images");

        Assert.That((page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt64()),
            Is.EqualTo((1, 50, 4L)));
        Assert.That(Rows(page), Is.EqualTo(new[]
        {
            (alpha, "Placeholder"), (bravo, "NoCover"), (delta, "NoCover"), (echo, "Placeholder"),
        }), "Charlie has real art; Delta matches both and is listed once as NoCover");
        var first = page.GetProperty("items")[0];
        Assert.That((first.GetProperty("displayTitle").GetString(), first.GetProperty("system").GetString(), first.GetProperty("systemName").GetString()),
            Is.EqualTo(("Alpha", "gb", "Game Boy")));
    }

    [Test]
    public async Task MissingImages_SystemFilterAndPaging()
    {
        var (_, bravo, _, echo) = await SeedMissingImages();

        Assert.That(Rows(await GetJson("/api/reports/missing-images?system=gbc")), Is.EqualTo(new[] { (echo, "Placeholder") }));
        var second = await GetJson("/api/reports/missing-images?system=gb&page=2&pageSize=1");
        Assert.That((second.GetProperty("total").GetInt64(), Rows(second)), Is.EqualTo((3L, new List<(Guid, string)> { (bravo, "NoCover") })));
    }

    [TestCase("system=nes", "system")]
    [TestCase("pageSize=201", "pageSize")]
    [TestCase("page=0", "page")]
    public async Task MissingImages_InvalidQuery_Is400(string query, string field)
    {
        using var response = await _user.GetAsync("/api/reports/missing-images?" + query);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await AuthClient.JsonAsync(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(problem.GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }
}
