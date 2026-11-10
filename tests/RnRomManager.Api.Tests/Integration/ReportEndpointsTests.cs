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
            [IngestSeeder.NesDat] = [],
            [IngestSeeder.SnesDat] = [],
            [IngestSeeder.Atari2600Dat] = [],
            [IngestSeeder.ColecoVisionDat] = [],
            [IngestSeeder.GameGearDat] = [],
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

    private async Task<(Guid Alpha, Guid Bravo, Guid Charlie, Guid Echo)> SeedSizes()
    {
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        var charlie = await Ingest("gb", "c.gb", Charlie);
        var echo = await Ingest("gbc", "e.gbc", Echo);
        // Fixed sizes so the order is known: Alpha and Charlie tie (title breaks it).
        await Sql("""
            UPDATE games SET zip_size_bytes = CASE id WHEN @alpha THEN 100 WHEN @bravo THEN 300 WHEN @charlie THEN 100 ELSE 1000 END
            """, new { alpha, bravo, charlie });
        return (alpha, bravo, charlie, echo);
    }

    [Test]
    public async Task LibrarySize_TotalsAndPerSystem_LargestFirst_EmptySystemsAbsent()
    {
        await SeedSizes();

        var size = await GetJson("/api/reports/library-size");

        Assert.That((size.GetProperty("totalBytes").GetInt64(), size.GetProperty("gameCount").GetInt32()), Is.EqualTo((1500L, 4)));
        var systems = size.GetProperty("systems").EnumerateArray()
            .Select(s => (s.GetProperty("system").GetString(), s.GetProperty("systemName").GetString(), s.GetProperty("bytes").GetInt64(),
                s.GetProperty("gameCount").GetInt32())).ToList();
        Assert.That(systems, Is.EqualTo(new[] { ("gbc", "Game Boy Color", 1000L, 1), ("gb", "Game Boy", 500L, 3) }));
    }

    [Test]
    public async Task LibrarySize_System_ListsGamesLargestFirst_TiesByTitle()
    {
        var (alpha, bravo, charlie, _) = await SeedSizes();

        var gb = await GetJson("/api/reports/library-size/gb");

        Assert.That((gb.GetProperty("system").GetString(), gb.GetProperty("systemName").GetString(), gb.GetProperty("bytes").GetInt64()),
            Is.EqualTo(("gb", "Game Boy", 500L)));
        var games = gb.GetProperty("games").EnumerateArray()
            .Select(g => (g.GetProperty("id").GetGuid(), g.GetProperty("displayTitle").GetString(), g.GetProperty("bytes").GetInt64(),
                g.GetProperty("romCount").GetInt32())).ToList();
        Assert.That(games, Is.EqualTo(new[] { (bravo, "Bravo", 300L, 1), (alpha, "Alpha", 100L, 1), (charlie, "Charlie", 100L, 1) }));
    }

    [Test]
    public async Task LibrarySize_RegisteredSystemWithoutGames_Is200Empty_UnknownIs404()
    {
        var gba = await GetJson("/api/reports/library-size/gba");
        Assert.That((gba.GetProperty("bytes").GetInt64(), gba.GetProperty("games").GetArrayLength()), Is.EqualTo((0L, 0)));

        var empty = await GetJson("/api/reports/library-size");
        Assert.That((empty.GetProperty("totalBytes").GetInt64(), empty.GetProperty("systems").GetArrayLength()), Is.EqualTo((0L, 0)));

        using var unknown = await _user.GetAsync("/api/reports/library-size/md");
        Assert.That((unknown.StatusCode, await AuthClient.CodeAsync(unknown)), Is.EqualTo((HttpStatusCode.NotFound, "NotFound")));
    }

    private async Task<AuthClient> Editor()
    {
        var editor = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await editor.LoginAsync("boss", TestUsers.Password)).Dispose();
        return editor;
    }

    private async Task<List<JsonElement>> Groups(string query = "")
    {
        var page = await GetJson("/api/reports/duplicates" + query);
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    private Task SetBaseTitle(string title, params Guid[] games) =>
        Sql("UPDATE roms SET base_title = @title WHERE game_id IN @games", new { title, games });

    private static List<Guid> GameIds(JsonElement group) =>
        group.GetProperty("games").EnumerateArray().Select(g => g.GetProperty("id").GetGuid()).ToList();

    private static object Dismissal(JsonElement group) => new
    {
        system = group.GetProperty("system").GetString(),
        baseTitle = group.GetProperty("baseTitle").GetString(),
        gamesKey = group.GetProperty("gamesKey").GetString(),
    };

    [Test]
    public async Task Duplicates_GroupsSameSystemAndBaseTitle_SuggestedTargetFirst_OtherSystemsApart()
    {
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        var echo = await Ingest("gbc", "e.gbc", Echo);
        await SetBaseTitle("Shared", alpha, bravo, echo);
        // Same release count: the older game is the suggested target.
        await Sql("UPDATE games SET created_at = '2020-01-01' WHERE id = @bravo", new { bravo });

        var page = await GetJson("/api/reports/duplicates");

        Assert.That((page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt64()), Is.EqualTo((25, 1L)));
        var group = page.GetProperty("items")[0];
        Assert.That((group.GetProperty("system").GetString(), group.GetProperty("systemName").GetString(), group.GetProperty("baseTitle").GetString()),
            Is.EqualTo(("gb", "Game Boy", "Shared")));
        Assert.That(group.GetProperty("gamesKey").GetString(), Is.EqualTo(RnRomManager.Core.Reports.DuplicateGroups.GamesKey([alpha, bravo])));
        Assert.That(GameIds(group), Is.EqualTo(new[] { bravo, alpha }));
        var first = group.GetProperty("games")[0];
        Assert.That((first.GetProperty("displayTitle").GetString(), first.GetProperty("romCount").GetInt32()), Is.EqualTo(("Bravo", 1)));
        Assert.That(first.GetProperty("zipSizeBytes").GetInt64(), Is.GreaterThan(0));
        Assert.That(first.GetProperty("boxArtUrl").GetString(), Does.StartWith($"/api/games/{bravo}/images/"));
        Assert.That(first.TryGetProperty("createdAt", out _), Is.True);
    }

    [Test]
    public async Task Dismiss_HidesTheGroup_UntilItsMembershipChanges_AndRepeatsAre204()
    {
        using var editor = await Editor();
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        var charlie = await Ingest("gb", "c.gb", Charlie);
        await SetBaseTitle("Shared", alpha, bravo);
        var group = (await Groups()).Single();

        for (var i = 0; i < 2; i++)
        {
            using var dismissed = await editor.PostAsync("/api/reports/duplicates/dismiss", Dismissal(group));
            Assert.That(dismissed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }
        Assert.That(await Groups(), Is.Empty);
        Assert.That((await GetJson("/api/reports/duplicates")).GetProperty("total").GetInt64(), Is.Zero);

        await SetBaseTitle("Shared", charlie);
        Assert.That(GameIds((await Groups()).Single()), Is.EquivalentTo(new[] { alpha, bravo, charlie }));
    }

    [Test]
    public async Task Duplicates_BaseTitleCaseVariants_GroupTogether_AndEitherSpellingDismisses()
    {
        using var editor = await Editor();
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        await SetBaseTitle("Quest", alpha);
        await SetBaseTitle("QUEST", bravo);
        var group = (await Groups()).Single();
        Assert.That(GameIds(group), Is.EquivalentTo(new[] { alpha, bravo }));

        var other = group.GetProperty("baseTitle").GetString() == "Quest" ? "QUEST" : "Quest";
        using var dismissed = await editor.PostAsync("/api/reports/duplicates/dismiss",
            new { system = "gb", baseTitle = other, gamesKey = group.GetProperty("gamesKey").GetString() });

        Assert.That(dismissed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await Groups(), Is.Empty);
    }

    [TestCase("{\"baseTitle\":\"Quest\",\"gamesKey\":\"" + Key + "\"}", "system")]
    [TestCase("{\"system\":\"md\",\"baseTitle\":\"Quest\",\"gamesKey\":\"" + Key + "\"}", "system")]
    [TestCase("{\"system\":\"gb\",\"baseTitle\":\"\",\"gamesKey\":\"" + Key + "\"}", "baseTitle")]
    [TestCase("{\"system\":\"gb\",\"baseTitle\":\"Quest\",\"gamesKey\":\"ABC\"}", "gamesKey")]
    [TestCase("{\"system\":\"gb\",\"baseTitle\":\"Quest\",\"gamesKey\":\"" + UpperKey + "\"}", "gamesKey")]
    public async Task Dismiss_InvalidBody_Is400(string json, string field)
    {
        using var editor = await Editor();

        using var response = await editor.PostAsync("/api/reports/duplicates/dismiss", JsonDocument.Parse(json).RootElement);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }

    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string UpperKey = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Test]
    public async Task Dismiss_StaleKey_IsStoredAnyway_204()
    {
        using var editor = await Editor();

        using var response = await editor.PostAsync("/api/reports/duplicates/dismiss", new { system = "gb", baseTitle = "Nothing", gamesKey = Key });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task Merge_MergesEverySourceIntoTheTarget()
    {
        using var editor = await Editor();
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        var charlie = await Ingest("gb", "c.gb", Charlie);

        using var response = await editor.PostAsync("/api/reports/duplicates/merge", new { targetGameId = alpha, sourceGameIds = new[] { bravo, charlie } });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("targetGameId").GetGuid(), Is.EqualTo(alpha));
        Assert.That((await GetJson($"/api/games/{alpha}")).GetProperty("roms").GetArrayLength(), Is.EqualTo(3));
        foreach (var gone in new[] { bravo, charlie })
        {
            using var detail = await _user.GetAsync($"/api/games/{gone}");
            Assert.That(detail.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }

    [TestCase(false, "[\"B\"]", "targetGameId")]
    [TestCase(true, "[]", "sourceGameIds")]
    [TestCase(true, "[\"A\"]", "sourceGameIds")]
    [TestCase(true, "[\"B\",\"B\"]", "sourceGameIds")]
    [TestCase(true, "many", "sourceGameIds")]
    public async Task Merge_InvalidBody_Is400(bool withTarget, string sources, string field)
    {
        using var editor = await Editor();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var ids = sources == "many"
            ? Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray()
            : JsonSerializer.Deserialize<string[]>(sources)!.Select(s => s == "A" ? a : b).ToArray();

        using var response = await editor.PostAsync("/api/reports/duplicates/merge",
            new { targetGameId = withTarget ? a : (Guid?)null, sourceGameIds = ids });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }

    [Test]
    public async Task Merge_StopsAtTheFirstFailure_AndListsWhatMerged()
    {
        using var editor = await Editor();
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        var charlie = await Ingest("gb", "c.gb", Charlie);
        var echo = await Ingest("gbc", "e.gbc", Echo);

        using (var missing = await editor.PostAsync("/api/reports/duplicates/merge",
            new { targetGameId = alpha, sourceGameIds = new[] { bravo, Guid.NewGuid(), charlie } }))
        {
            var problem = await AuthClient.JsonAsync(missing);
            Assert.That((missing.StatusCode, problem.GetProperty("code").GetString()), Is.EqualTo((HttpStatusCode.NotFound, "NotFound")));
            var merged = problem.GetProperty("merged").EnumerateArray().Select(m => m.GetGuid());
            Assert.That(merged, Is.EqualTo(new[] { bravo }));
        }
        Assert.That((await GetJson($"/api/games/{charlie}")).GetProperty("id").GetGuid(), Is.EqualTo(charlie), "untouched after the stop");

        using var otherSystem = await editor.PostAsync("/api/reports/duplicates/merge",
            new { targetGameId = alpha, sourceGameIds = new[] { charlie, echo } });
        var invalid = await AuthClient.JsonAsync(otherSystem);
        Assert.That((otherSystem.StatusCode, invalid.GetProperty("code").GetString()), Is.EqualTo((HttpStatusCode.BadRequest, "ValidationFailed")));
        Assert.That(invalid.GetProperty("merged").EnumerateArray().Select(m => m.GetGuid()), Is.EqualTo(new[] { charlie }));
    }

    [TestCase("system=md", "system")]
    [TestCase("pageSize=201", "pageSize")]
    public async Task Duplicates_InvalidQuery_Is400(string query, string field)
    {
        using var response = await _user.GetAsync("/api/reports/duplicates?" + query);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }

    [TestCase("system=md", "system")]
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
