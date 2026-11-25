using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
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

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
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
            [IngestSeeder.LynxDat] = [],
            [IngestSeeder.JaguarDat] = [],
            [IngestSeeder.VirtualBoyDat] = [],
            [IngestSeeder.Atari5200Dat] = [],
            [IngestSeeder.Atari7800Dat] = [],
            [IngestSeeder.ColecoVisionDat] = [],
            [IngestSeeder.GameGearDat] = [],
            [IngestSeeder.MasterSystemDat] = [],
            [IngestSeeder.MegaDriveDat] = [],
            [IngestSeeder.Sega32xDat] = [],
            [IngestSeeder.TurboGrafx16Dat] = [],
            [IngestSeeder.PsxDat] = [],
            [IngestSeeder.PspDat] = [],
            [IngestSeeder.GameCubeDat] = [],
            [IngestSeeder.WiiDat] = [],
            [IngestSeeder.Xbox360Dat] = [],
            [IngestSeeder.SaturnDat] = [],
            [IngestSeeder.SegaCdDat] = [],
            [IngestSeeder.ThreeDoDat] = [],
            [IngestSeeder.C64Dat] = [],
            [IngestSeeder.PetDat] = [],
            [IngestSeeder.Vic20Dat] = [], [IngestSeeder.SatellaviewDat] = [], [IngestSeeder.SufamiTurboDat] = [], [IngestSeeder.FdsDat] = [],
            [IngestSeeder.AmigaDat] = [], [IngestSeeder.ThreeDsDat] = [], [IngestSeeder.TurboGrafxCdDat] = [], [IngestSeeder.PcfxDat] = [], [IngestSeeder.NeoGeoPocketDat] = [], [IngestSeeder.NeoGeoPocketColorDat] = [],
            [IngestSeeder.NintendoDsDat] = [],
            [Gb] =
            [
                new FixtureGame("Alpha (World)", "Alpha (World).gb", Alpha),
                new FixtureGame("Bravo (World)", "Bravo (World).gb", Bravo),
                new FixtureGame("Charlie (World)", "Charlie (World).gb", Charlie),
                new FixtureGame("Delta (World)", "Delta (World).gb", Delta),
            ],
            [Gbc] = [new FixtureGame("Echo (World)", "Echo (World).gbc", Echo)],
        }, titleDb: IngestSeeder.EmptySwitch, mame: IngestSeeder.MinimalMame, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
    }

    [TearDown]
    public void TearDown()
    {
        _user.Dispose();
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

    private async Task<(Guid? Id, bool CurrentIsOnly)> Next(Guid? after = null)
    {
        var json = await GetJson("/api/reports/missing-images/next" + (after is { } id ? $"?afterGameId={id}" : ""));
        var next = json.GetProperty("id");
        return (next.ValueKind == JsonValueKind.Null ? null : next.GetGuid(), json.GetProperty("currentIsOnly").GetBoolean());
    }

    [Test]
    public async Task NextMissingImage_FollowsTheReportOrder_AcrossSystems_AndWraps()
    {
        var (alpha, bravo, delta, echo) = await SeedMissingImages();

        Assert.That(await Next(), Is.EqualTo(((Guid?)alpha, false)), "no anchor starts at the first row");
        Assert.That(await Next(alpha), Is.EqualTo(((Guid?)bravo, false)));
        Assert.That(await Next(delta), Is.EqualTo(((Guid?)echo, false)), "gb's last row is followed by gbc's first");
        Assert.That(await Next(echo), Is.EqualTo(((Guid?)alpha, false)), "the last row wraps to the first");
    }

    [Test]
    public async Task NextMissingImage_FixedAnchor_KeepsItsPosition()
    {
        var (alpha, bravo, delta, _) = await SeedMissingImages();
        var charlie = await GameId("Charlie");
        // Bravo has just been fixed: the next press still continues after it, not from the top.
        await Sql("UPDATE images SET is_cover = 1 WHERE game_id = @bravo", new { bravo });

        Assert.That(await Next(bravo), Is.EqualTo(((Guid?)delta, false)));
        Assert.That(await Next(charlie), Is.EqualTo(((Guid?)delta, false)), "Charlie, never in the report, sorts between Bravo and Delta");
        Assert.That(await Next(alpha), Is.EqualTo(((Guid?)delta, false)));
    }

    [Test]
    public async Task NextMissingImage_DeletedAnchor_StartsAtTheFirstRow()
    {
        var (alpha, _, _, _) = await SeedMissingImages();

        Assert.That(await Next(Guid.NewGuid()), Is.EqualTo(((Guid?)alpha, false)));
    }

    [Test]
    public async Task NextMissingImage_TitleTies_AreBrokenById_UnderTheColumnCollation()
    {
        var (alpha, bravo, delta, _) = await SeedMissingImages();
        // "ALPHA" equals "Alpha" under the column's case-insensitive collation, so only the id orders the two.
        await Sql("UPDATE games SET display_title = 'ALPHA' WHERE id = @bravo", new { bravo });
        var (low, high) = string.CompareOrdinal(alpha.ToString("D"), bravo.ToString("D")) < 0 ? (alpha, bravo) : (bravo, alpha);

        var report = Rows(await GetJson("/api/reports/missing-images")).Select(r => r.Id).ToList();
        Assert.That(report.Take(3), Is.EqualTo(new[] { low, high, delta }), "the report itself breaks the tie by id");
        Assert.That(await Next(), Is.EqualTo(((Guid?)low, false)));
        Assert.That(await Next(low), Is.EqualTo(((Guid?)high, false)));
        Assert.That(await Next(high), Is.EqualTo(((Guid?)delta, false)));
    }

    [Test]
    public async Task NextMissingImage_SoleCurrent_And_Empty()
    {
        var (alpha, bravo, delta, echo) = await SeedMissingImages();
        await Sql("""
            UPDATE images SET is_cover = 1 WHERE game_id IN (@bravo, @delta);
            UPDATE games SET has_default_boxart = 0 WHERE id IN (@bravo, @delta, @echo)
            """, new { bravo, delta, echo });

        Assert.That(await Next(alpha), Is.EqualTo(((Guid?)null, true)), "only the current game is left");
        Assert.That(await Next(), Is.EqualTo(((Guid?)alpha, false)));
        Assert.That(await Next(bravo), Is.EqualTo(((Guid?)alpha, false)), "a fixed anchor still finds the remaining game");

        await Sql("UPDATE games SET has_default_boxart = 0 WHERE id = @alpha", new { alpha });
        Assert.That(await Next(), Is.EqualTo(((Guid?)null, false)));
        Assert.That(await Next(alpha), Is.EqualTo(((Guid?)null, false)), "a fixed current game is not the only one left");
    }

    [Test]
    public async Task NextMissingImage_InvalidGuid_Is400_AndAnonymousIs401()
    {
        using var invalid = await _user.GetAsync("/api/reports/missing-images/next?afterGameId=not-a-guid");
        Assert.That((invalid.StatusCode, await AuthClient.CodeAsync(invalid)), Is.EqualTo((HttpStatusCode.BadRequest, "ValidationFailed")));

        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var response = await anonymous.GetAsync("/api/reports/missing-images/next");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    private async Task<Guid> GameId(string displayTitle)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.ExecuteScalarAsync<Guid>("SELECT id FROM games WHERE display_title = @displayTitle", new { displayTitle });
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

    /// <summary>Adds an update release (flags <c>["Update"]</c>) to <paramref name="gameId"/>, copied from its primary.</summary>
    private Task AddUpdateRelease(Guid gameId, int n) => Sql("""
        INSERT INTO roms (game_id, system_id, file_name, original_file_name, base_title, regions, languages, flags, region_key,
                          is_primary, id_method, size_bytes, crc32, md5, sha1, date_added, date_modified)
        SELECT game_id, system_id, CONCAT('Update ', @n, ' ', file_name), original_file_name, base_title, regions, languages,
               '["Update"]', region_key, 0, id_method, 0, crc32, md5, SHA1(CONCAT(sha1, @n)), date_added, date_modified
        FROM roms WHERE game_id = @gameId AND is_primary = 1
        """, new { gameId, n });

    [Test]
    public async Task LibrarySize_CountsUpdateReleases_PerGame_PerSystem_AndInTotal()
    {
        var (alpha, bravo, charlie, echo) = await SeedSizes();
        await AddUpdateRelease(bravo, 1);
        await AddUpdateRelease(bravo, 2);
        await AddUpdateRelease(echo, 1);

        var size = await GetJson("/api/reports/library-size");
        var gb = await GetJson("/api/reports/library-size/gb");

        Assert.That(size.GetProperty("updateCount").GetInt32(), Is.EqualTo(3));
        Assert.That(size.GetProperty("systems").EnumerateArray().Select(s => (s.GetProperty("system").GetString(), s.GetProperty("updateCount").GetInt32())),
            Is.EqualTo(new[] { ("gbc", 1), ("gb", 2) }));
        Assert.That(gb.GetProperty("updateCount").GetInt32(), Is.EqualTo(2));
        Assert.That(gb.GetProperty("games").EnumerateArray().Select(g => (g.GetProperty("id").GetGuid(), g.GetProperty("romCount").GetInt32(),
                g.GetProperty("updateCount").GetInt32())),
            Is.EqualTo(new[] { (bravo, 3, 2), (alpha, 1, 0), (charlie, 1, 0) }));
        Assert.That((size.GetProperty("totalBytes").GetInt64(), gb.GetProperty("bytes").GetInt64()), Is.EqualTo((1500L, 500L)), "sizes unchanged");
    }

    [Test]
    public async Task LibrarySize_NoUpdates_CountsZero()
    {
        await SeedSizes();

        var size = await GetJson("/api/reports/library-size");
        var gb = await GetJson("/api/reports/library-size/gb");

        Assert.That(size.GetProperty("updateCount").GetInt32(), Is.Zero);
        Assert.That(size.GetProperty("systems").EnumerateArray().Select(s => s.GetProperty("updateCount").GetInt32()), Is.All.Zero);
        Assert.That(gb.GetProperty("updateCount").GetInt32(), Is.Zero);
        Assert.That(gb.GetProperty("games").EnumerateArray().Select(g => g.GetProperty("updateCount").GetInt32()), Is.All.Zero);
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

    /// <summary>Mirrors a game whose ROMs carry external pointers; no file is written, so every sidecar is missing.</summary>
    private async Task<Guid> MirrorExternal(string system, string title, long zipBytes, params int[] romSizes)
    {
        var roms = romSizes.Select((size, i) => TestLibrary.Rom($"{title} [v{i}].nsp", [.. Enumerable.Repeat((byte)(title.Length + i), size)], isPrimary: i == 0)
            with { ExternalFileName = $"{title}.{title} [v{i}].nsp" }).ToArray();
        var game = TestLibrary.Game(title, roms) with { System = system };
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, $"{system}/{title[..1]}/{title}.zip", zipBytes, DateTime.UtcNow), default);
        return game.GameId;
    }

    [Test]
    public async Task LibrarySize_AddsRecordedExternalRomSizes_OncePerGame_EvenWhenTheFileIsMissing()
    {
        var embedded = await Ingest("gb", "a.gb", Alpha);
        await Sql("UPDATE games SET zip_size_bytes = 500 WHERE id = @embedded", new { embedded });
        await MirrorExternal(ExternalTestHandler.SystemId, "Orbit", 100, 300, 200);

        var size = await GetJson("/api/reports/library-size");

        Assert.That((size.GetProperty("totalBytes").GetInt64(), size.GetProperty("gameCount").GetInt32()), Is.EqualTo((1100L, 2)));
        var systems = size.GetProperty("systems").EnumerateArray()
            .Select(s => (s.GetProperty("system").GetString(), s.GetProperty("bytes").GetInt64(), s.GetProperty("gameCount").GetInt32())).ToList();
        Assert.That(systems, Is.EqualTo(new[] { ("ext", 600L, 1), ("gb", 500L, 1) }), "the ZIP's 100 counts once, not per release");
    }

    [Test]
    public async Task LibrarySize_IncludesSetFiles_AndRomCountStaysReleases()
    {
        // B21: a release's set files count toward the size; the drill-down's romCount is still one release.
        var rom = TestLibrary.Rom("Disc (Track 1).bin", [.. Enumerable.Repeat((byte)1, 300)]) with
        {
            ExternalFileName = "Disc (Track 1).bin",
            Files = [new InfoJsonRomFile("Disc.cue", "Disc.cue", "Disc.cue", 40, "00000000", new string('0', 32), new string('a', 40)),
                new InfoJsonRomFile("Disc (Track 2).bin", "Disc (Track 2).bin", "Disc (Track 2).bin", 700, "00000000", new string('0', 32), new string('b', 40))],
        };
        var game = TestLibrary.Game("Disc", rom) with { System = "gb" };
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, "gb/D/Disc.zip", 100, DateTime.UtcNow), default);

        var size = await GetJson("/api/reports/library-size");
        var gb = await GetJson("/api/reports/library-size/gb");

        Assert.That(size.GetProperty("totalBytes").GetInt64(), Is.EqualTo(1140L));
        Assert.That(gb.GetProperty("games").EnumerateArray().Select(g => (g.GetProperty("bytes").GetInt64(), g.GetProperty("romCount").GetInt32())),
            Is.EqualTo(new[] { (1140L, 1) }));
    }

    [Test]
    public async Task LibrarySize_System_OrdersByCombinedSize()
    {
        // The report sums by stored pointer, so pointer rows under a registered system exercise the drill-down.
        var embedded = await Ingest("gb", "a.gb", Alpha);
        await Sql("UPDATE games SET zip_size_bytes = 500 WHERE id = @embedded", new { embedded });
        var external = await MirrorExternal("gb", "Orbit", 100, 300, 200);

        var gb = await GetJson("/api/reports/library-size/gb");

        Assert.That(gb.GetProperty("bytes").GetInt64(), Is.EqualTo(1100L));
        var games = gb.GetProperty("games").EnumerateArray()
            .Select(g => (g.GetProperty("id").GetGuid(), g.GetProperty("bytes").GetInt64(), g.GetProperty("romCount").GetInt32())).ToList();
        Assert.That(games, Is.EqualTo(new[] { (external, 600L, 2), (embedded, 500L, 1) }), "the smaller ZIP sorts first by combined size");
    }

    [Test]
    public async Task LibrarySize_RegisteredSystemWithoutGames_Is200Empty_UnknownIs404()
    {
        var gba = await GetJson("/api/reports/library-size/gba");
        Assert.That((gba.GetProperty("bytes").GetInt64(), gba.GetProperty("games").GetArrayLength()), Is.EqualTo((0L, 0)));

        var empty = await GetJson("/api/reports/library-size");
        Assert.That((empty.GetProperty("totalBytes").GetInt64(), empty.GetProperty("systems").GetArrayLength()), Is.EqualTo((0L, 0)));

        using var unknown = await _user.GetAsync("/api/reports/library-size/sg1000");
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
    [TestCase("{\"system\":\"sg1000\",\"baseTitle\":\"Quest\",\"gamesKey\":\"" + Key + "\"}", "system")]
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

    [TestCase("system=sg1000", "system")]
    [TestCase("pageSize=201", "pageSize")]
    public async Task Duplicates_InvalidQuery_Is400(string query, string field)
    {
        using var response = await _user.GetAsync("/api/reports/duplicates?" + query);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }

    [TestCase("system=sg1000", "system")]
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

    // Multiple Releases B16: GET /api/reports/multiple-releases.

    /// <summary>Alpha gets Bravo's release and Charlie's as an update (2 releases, 1 update); Delta gets Echo's (2 releases).</summary>
    private async Task<(Guid Alpha, Guid Delta)> SeedMultipleReleases()
    {
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        var charlie = await Ingest("gb", "c.gb", Charlie);
        var delta = await Ingest("gb", "d.gb", Delta);
        var echo = await Ingest("gbc", "e.gbc", Echo);
        await Sql("UPDATE roms SET game_id = @alpha WHERE game_id IN (@bravo, @charlie)", new { alpha, bravo, charlie });
        await Sql("UPDATE roms SET flags = '[\"Update\"]' WHERE file_name LIKE 'Charlie%'", new { });
        await Sql("UPDATE roms SET game_id = @delta WHERE game_id = @echo", new { delta, echo });
        return (alpha, delta);
    }

    private async Task<Guid[]> MultipleReleaseIds(string query) =>
        (await GetJson("/api/reports/multiple-releases" + query)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToArray();

    [Test]
    public async Task MultipleReleases_Defaults_AndResponseShape()
    {
        var (alpha, delta) = await SeedMultipleReleases();

        var page = await GetJson("/api/reports/multiple-releases");
        var sizes = await GetJson("/api/reports/library-size/gb");

        Assert.That(page.GetProperty("maxReleases").GetInt32(), Is.EqualTo(2));
        Assert.That((page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt64()),
            Is.EqualTo((1, 50, 2L)));
        var first = page.GetProperty("items")[0];
        var alphaSize = sizes.GetProperty("games").EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == alpha);
        var alphaBytes = alphaSize.GetProperty("bytes").GetInt64();
        Assert.That(first.GetProperty("id").GetGuid(), Is.EqualTo(alpha));
        Assert.That(first.GetProperty("displayTitle").GetString(), Is.EqualTo(alphaSize.GetProperty("displayTitle").GetString()));
        Assert.That(first.GetProperty("system").GetString(), Is.EqualTo("gb"));
        Assert.That(first.GetProperty("systemName").GetString(),
            Is.EqualTo(Service<RnRomManager.Core.Systems.ISystemRegistry>().ById("gb")!.DisplayName));
        Assert.That((first.GetProperty("releaseCount").GetInt32(), first.GetProperty("updateCount").GetInt32(), first.GetProperty("bytes").GetInt64()),
            Is.EqualTo((2, 1, alphaBytes)));
        // Releases ties break on title, so Alpha before Delta.
        Assert.That(await MultipleReleaseIds(""), Is.EqualTo(new[] { alpha, delta }));
    }

    [Test]
    public async Task MultipleReleases_DirDefaultsPerKey_AndSortDirAreCaseInsensitive()
    {
        var (alpha, delta) = await SeedMultipleReleases();

        Assert.Multiple(async () =>
        {
            Assert.That(await MultipleReleaseIds("?sort=updates"), Is.EqualTo(new[] { alpha, delta }), "updates defaults to desc");
            Assert.That(await MultipleReleaseIds("?sort=updates&dir=asc"), Is.EqualTo(new[] { delta, alpha }));
            Assert.That(await MultipleReleaseIds("?sort=name"), Is.EqualTo(new[] { alpha, delta }), "name defaults to asc");
            Assert.That(await MultipleReleaseIds("?sort=NAME&dir=DESC"), Is.EqualTo(new[] { delta, alpha }));
            Assert.That(await MultipleReleaseIds("?sort=Size&dir=Asc"), Has.Length.EqualTo(2));
        });
    }

    [Test]
    public async Task MultipleReleases_MinFilters_MaxReleasesIgnoresMin()
    {
        await SeedMultipleReleases();

        var page = await GetJson("/api/reports/multiple-releases?min=3");

        Assert.That((page.GetProperty("total").GetInt64(), page.GetProperty("maxReleases").GetInt32()), Is.EqualTo((0L, 2)));
    }

    [Test]
    public async Task MultipleReleases_DlcIsNotARelease()
    {
        var (alpha, delta) = await SeedMultipleReleases();
        // Delta's second release (Echo's) becomes DLC: Delta drops out, and Alpha (2 releases) stays.
        await Sql("UPDATE roms SET flags = '[\"DLC\"]' WHERE file_name LIKE 'Echo%'", new { });

        var page = await GetJson("/api/reports/multiple-releases");

        Assert.That(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()), Is.EqualTo(new[] { alpha }));
        Assert.That(page.GetProperty("maxReleases").GetInt32(), Is.EqualTo(2));

        await Sql("UPDATE roms SET game_id = @delta WHERE game_id = @alpha", new { alpha, delta });
        page = await GetJson("/api/reports/multiple-releases");
        Assert.That(page.GetProperty("items")[0].GetProperty("releaseCount").GetInt32(), Is.EqualTo(3), "Delta: Delta, Alpha, Bravo; not Charlie (update) or Echo (DLC)");
    }

    /// <summary>Release editing B12: <c>system</c> filters the list and <c>maxReleases</c>.</summary>
    [Test]
    public async Task MultipleReleases_SystemFilter_AlsoFiltersMaxReleases()
    {
        var (alpha, delta) = await SeedMultipleReleases();
        await Sql("UPDATE games SET system_id = 'gbc' WHERE id = @delta", new { delta });
        await Sql("UPDATE roms SET system_id = 'gbc' WHERE game_id = @delta", new { delta });
        await Sql("UPDATE roms SET game_id = @alpha WHERE file_name LIKE 'Delta%'", new { alpha });

        var gb = await GetJson("/api/reports/multiple-releases?system=gb");
        var gbc = await GetJson("/api/reports/multiple-releases?system=gbc&min=2");

        Assert.That((gb.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()), gb.GetProperty("maxReleases").GetInt32()),
            Is.EqualTo((new[] { alpha }, 3)));
        Assert.That((gbc.GetProperty("total").GetInt64(), gbc.GetProperty("maxReleases").GetInt32()), Is.EqualTo((0L, 1)));
    }

    [TestCase("system=nope", "system", "Choose a registered system.")]
    [TestCase("min=1", "min", "Must be 2–10000.")]
    [TestCase("min=10001", "min", "Must be 2–10000.")]
    [TestCase("sort=title", "sort", "Must be name, system, releases, updates or size.")]
    [TestCase("sort=1", "sort", "Must be name, system, releases, updates or size.")]
    [TestCase("dir=up", "dir", "Must be asc or desc.")]
    [TestCase("page=0", "page", "Must be 1 or more.")]
    [TestCase("pageSize=201", "pageSize", "Must be 1–200.")]
    [TestCase("pageSize=0", "pageSize", "Must be 1–200.")]
    public async Task MultipleReleases_InvalidQuery_IsValidationFailed(string query, string field, string message)
    {
        using var response = await _user.GetAsync("/api/reports/multiple-releases?" + query);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await AuthClient.JsonAsync(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(problem.GetProperty("errors").GetProperty(field)[0].GetString(), Is.EqualTo(message));
    }

    [Test]
    public async Task MultipleReleases_NonNumberMin_IsAPlain400()
    {
        using var response = await _user.GetAsync("/api/reports/multiple-releases?min=abc");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Not.Contain("ValidationFailed"));
    }

    // Release editing B14: GET /api/reports/newly-added.

    [Test]
    public async Task NewlyAdded_GroupsBySystem_SystemsByName_NewestFirst()
    {
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        var echo = await Ingest("gbc", "e.gbc", Echo);
        await Sql("UPDATE games SET created_at = '2026-01-01 00:00:00' WHERE id = @alpha", new { alpha });

        var body = await GetJson("/api/reports/newly-added");
        var gbc = await GetJson("/api/reports/newly-added?system=gbc");

        var systems = body.GetProperty("systems").EnumerateArray().ToList();
        Assert.That(systems.Select(s => s.GetProperty("system").GetString()), Is.EqualTo(new[] { "gb", "gbc" }));
        var registry = Service<RnRomManager.Core.Systems.ISystemRegistry>();
        Assert.That(systems[0].GetProperty("systemName").GetString(), Is.EqualTo(registry.ById("gb")!.DisplayName));
        var games = systems[0].GetProperty("games").EnumerateArray().ToList();
        Assert.That(games.Select(g => g.GetProperty("id").GetGuid()), Is.EqualTo(new[] { bravo, alpha }));
        Assert.That(games[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "id", "displayTitle", "addedAt", "releaseCount" }));
        Assert.That((games[1].GetProperty("addedAt").GetString(), games[1].GetProperty("releaseCount").GetInt32()),
            Is.EqualTo(("2026-01-01T00:00:00Z", 1)));
        Assert.That(gbc.GetProperty("systems").EnumerateArray().Select(s => s.GetProperty("games")[0].GetProperty("id").GetGuid()),
            Is.EqualTo(new[] { echo }));
    }

    [Test]
    public async Task NewlyAdded_UnknownSystem_Is400()
    {
        using var response = await _user.GetAsync("/api/reports/newly-added?system=nope");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").GetProperty("system")[0].GetString(),
            Is.EqualTo("Choose a registered system."));
    }

    // Release editing B13: GET /api/reports/hacks.

    [Test]
    public async Task Hacks_ResponseShape_DefaultSort_AndSystemFilter()
    {
        var (alpha, delta) = await SeedMultipleReleases();
        await Sql("UPDATE roms SET flags = '[\"Hack\"]' WHERE file_name LIKE 'Bravo%' OR file_name LIKE 'Echo%' OR file_name LIKE 'Delta%'", new { });

        var page = await GetJson("/api/reports/hacks");
        var none = await GetJson("/api/reports/hacks?system=gbc");

        Assert.That((page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt64()),
            Is.EqualTo((1, 50, 2L)));
        var first = page.GetProperty("items")[0];
        Assert.That(first.EnumerateObject().Select(p => p.Name),
            Is.EqualTo(new[] { "id", "displayTitle", "system", "systemName", "hackCount", "releaseCount", "bytes" }));
        Assert.That((first.GetProperty("id").GetGuid(), first.GetProperty("hackCount").GetInt32(), first.GetProperty("releaseCount").GetInt32()),
            Is.EqualTo((delta, 2, 2)), "hacks descending by default");
        Assert.That(page.GetProperty("items")[1].GetProperty("id").GetGuid(), Is.EqualTo(alpha));
        Assert.That(none.GetProperty("total").GetInt64(), Is.Zero);
    }

    [TestCase("system=nope", "system", "Choose a registered system.")]
    [TestCase("sort=updates", "sort", "Must be name, system, hacks, releases or size.")]
    [TestCase("dir=up", "dir", "Must be asc or desc.")]
    [TestCase("page=0", "page", "Must be 1 or more.")]
    [TestCase("pageSize=201", "pageSize", "Must be 1–200.")]
    public async Task Hacks_InvalidQuery_IsValidationFailed(string query, string field, string message)
    {
        using var response = await _user.GetAsync("/api/reports/hacks?" + query);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").GetProperty(field)[0].GetString(), Is.EqualTo(message));
    }

    // Missing Metadata M2–M3: GET /api/reports/missing-metadata and /next (Admin only).

    [Test]
    public async Task MissingMetadata_UserIs403()
    {
        using var list = await _user.GetAsync("/api/reports/missing-metadata");
        using var next = await _user.GetAsync("/api/reports/missing-metadata/next");

        Assert.That((list.StatusCode, next.StatusCode), Is.EqualTo((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)));
    }

    [Test]
    public async Task MissingMetadata_Shape_AndNext()
    {
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        await Sql("""
            UPDATE games SET release_year = 1990, developers = '["Dev Co"]', publishers = '[]' WHERE id = @alpha;
            UPDATE games SET release_year = 1991, developers = '["Dev Co"]', publishers = '["Pub Co"]' WHERE id = @bravo
            """, new { alpha, bravo });
        using var admin = await Editor();

        using var response = await admin.GetAsync("/api/reports/missing-metadata?missing=PUBLISHER&sort=Year&dir=DESC");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var page = await AuthClient.JsonAsync(response);
        Assert.That(page.GetProperty("total").GetInt64(), Is.EqualTo(1));
        var item = page.GetProperty("items").EnumerateArray().Single();
        Assert.That((item.GetProperty("id").GetGuid(), item.GetProperty("displayTitle").GetString(), item.GetProperty("system").GetString(),
                item.GetProperty("systemName").GetString(), item.GetProperty("releaseYear").GetInt32()),
            Is.EqualTo((alpha, "Alpha", "gb", "Game Boy", 1990)));
        Assert.That(item.GetProperty("developers").EnumerateArray().Select(d => d.GetString()), Is.EqualTo(new[] { "Dev Co" }));
        Assert.That(item.GetProperty("publishers").GetArrayLength(), Is.Zero);

        using var next = await admin.GetAsync($"/api/reports/missing-metadata/next?afterGameId={alpha}");
        var successor = await AuthClient.JsonAsync(next);
        Assert.That((successor.GetProperty("id").ValueKind, successor.GetProperty("currentIsOnly").GetBoolean()),
            Is.EqualTo((JsonValueKind.Null, true)));
    }

    [TestCase("system=sg1000", "system", "Choose a registered system.")]
    [TestCase("missing=title", "missing", "Must be any, year, developer or publisher.")]
    [TestCase("sort=releases", "sort", "Must be name, system or year.")]
    [TestCase("dir=up", "dir", "Must be asc or desc.")]
    [TestCase("pageSize=201", "pageSize", null)]
    [TestCase("page=0", "page", null)]
    public async Task MissingMetadata_InvalidQuery_Is400(string query, string field, string? message)
    {
        using var admin = await Editor();
        using var response = await admin.GetAsync("/api/reports/missing-metadata?" + query);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var errors = (await AuthClient.JsonAsync(response)).GetProperty("errors");
        Assert.That(errors.TryGetProperty(field, out var value), Is.True);
        if (message is not null)
            Assert.That(value[0].GetString(), Is.EqualTo(message));
    }

    [Test]
    public async Task MissingMetadata_LongQuery_AndBadNextId_Are400()
    {
        using var admin = await Editor();
        using var longQ = await admin.GetAsync("/api/reports/missing-metadata?q=" + new string('x', 101));
        using var badId = await admin.GetAsync("/api/reports/missing-metadata/next?afterGameId=not-a-guid");

        Assert.That((await AuthClient.JsonAsync(longQ)).GetProperty("errors").GetProperty("q")[0].GetString(), Is.EqualTo("Must be at most 100 characters."));
        Assert.That((await AuthClient.JsonAsync(badId)).GetProperty("errors").TryGetProperty("afterGameId", out _), Is.True);
    }

    // Missing IGDB IDs R1: GET /api/reports/missing-igdb-ids and /next (Admin only).

    [Test]
    public async Task MissingIgdbIds_UserIs403()
    {
        using var list = await _user.GetAsync("/api/reports/missing-igdb-ids");
        using var next = await _user.GetAsync("/api/reports/missing-igdb-ids/next");

        Assert.That((list.StatusCode, next.StatusCode), Is.EqualTo((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)));
    }

    [Test]
    public async Task MissingIgdbIds_Next_SkipsNotOnIgdb_AndSaysWhenTheCurrentIsTheOnlyOne()
    {
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        await Sql("UPDATE games SET igdb_id = 0 WHERE id = @bravo", new { bravo });
        using var admin = await Editor();

        using var first = await admin.GetAsync("/api/reports/missing-igdb-ids/next");
        Assert.That((await AuthClient.JsonAsync(first)).GetProperty("id").GetGuid(), Is.EqualTo(alpha));

        using var next = await admin.GetAsync($"/api/reports/missing-igdb-ids/next?afterGameId={alpha}");
        var successor = await AuthClient.JsonAsync(next);
        Assert.That((successor.GetProperty("id").ValueKind, successor.GetProperty("currentIsOnly").GetBoolean()),
            Is.EqualTo((JsonValueKind.Null, true)));
    }

    [Test]
    public async Task MissingIgdbIds_Shape_AndStatus()
    {
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        await Sql("UPDATE games SET release_year = 1990 WHERE id = @alpha; UPDATE games SET igdb_id = 0 WHERE id = @bravo", new { alpha, bravo });
        using var admin = await Editor();

        using var response = await admin.GetAsync("/api/reports/missing-igdb-ids?sort=Year&dir=DESC");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var page = await AuthClient.JsonAsync(response);
        Assert.That(page.GetProperty("total").GetInt64(), Is.EqualTo(1));
        var item = page.GetProperty("items").EnumerateArray().Single();
        Assert.That(item.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "id", "displayTitle", "system", "systemName", "releaseYear" }));
        Assert.That((item.GetProperty("id").GetGuid(), item.GetProperty("displayTitle").GetString(), item.GetProperty("system").GetString(),
                item.GetProperty("systemName").GetString(), item.GetProperty("releaseYear").GetInt32()),
            Is.EqualTo((alpha, "Alpha", "gb", "Game Boy", 1990)));

        using var notOn = await admin.GetAsync("/api/reports/missing-igdb-ids?status=NOT-ON-IGDB");
        var notOnItems = (await AuthClient.JsonAsync(notOn)).GetProperty("items");
        Assert.That(notOnItems.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()), Is.EqualTo(new[] { bravo }));
    }

    [TestCase("system=sg1000", "system", "Choose a registered system.")]
    [TestCase("status=linked", "status", "Must be missing or not-on-igdb.")]
    [TestCase("sort=releases", "sort", "Must be name, system or year.")]
    [TestCase("dir=up", "dir", "Must be asc or desc.")]
    [TestCase("pageSize=201", "pageSize", null)]
    [TestCase("page=0", "page", null)]
    public async Task MissingIgdbIds_InvalidQuery_Is400(string query, string field, string? message)
    {
        using var admin = await Editor();
        using var response = await admin.GetAsync("/api/reports/missing-igdb-ids?" + query);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var errors = (await AuthClient.JsonAsync(response)).GetProperty("errors");
        Assert.That(errors.TryGetProperty(field, out var value), Is.True);
        if (message is not null)
            Assert.That(value[0].GetString(), Is.EqualTo(message));
    }

    [Test]
    public async Task MissingIgdbIds_LongQuery_AndBadNextId_Are400()
    {
        using var admin = await Editor();
        using var longQ = await admin.GetAsync("/api/reports/missing-igdb-ids?q=" + new string('x', 101));
        using var badId = await admin.GetAsync("/api/reports/missing-igdb-ids/next?afterGameId=not-a-guid");

        Assert.That((await AuthClient.JsonAsync(longQ)).GetProperty("errors").GetProperty("q")[0].GetString(), Is.EqualTo("Must be at most 100 characters."));
        Assert.That((await AuthClient.JsonAsync(badId)).GetProperty("errors").TryGetProperty("afterGameId", out _), Is.True);
    }

    /// <summary>Image Resolutions: ingested games only have placeholder art, so give some of their image rows real values.</summary>
    private async Task<(Guid Alpha, Guid Bravo, Guid Echo)> SeedImageResolutions()
    {
        var alpha = await Ingest("gb", "a.gb", Alpha);
        var bravo = await Ingest("gb", "b.gb", Bravo);
        await Ingest("gb", "c.gb", Charlie);
        var echo = await Ingest("gbc", "e.gbc", Echo);
        const string real = "UPDATE images SET is_default = 0, type = @type, width = @w, height = @h WHERE game_id = @id";
        await Sql(real, new { id = alpha, type = "BoxArt", w = 100, h = 100 });
        await Sql(real, new { id = bravo, type = "Snap", w = 300, h = 50 });
        await Sql(real, new { id = echo, type = "BoxArt", w = 64, h = 64 });
        return (alpha, bravo, echo);
    }

    private static List<Guid> ImageGameIds(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("gameId").GetGuid())];

    [Test]
    public async Task ImageResolutions_ListsRealImages_SmallestFirst_WithEveryField()
    {
        var (alpha, bravo, echo) = await SeedImageResolutions();

        var page = await GetJson("/api/reports/image-resolutions");

        Assert.That((page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt64()),
            Is.EqualTo((1, 50, 3L)));
        Assert.That(ImageGameIds(page), Is.EqualTo(new[] { echo, alpha, bravo }), "Charlie keeps placeholder art");
        var first = page.GetProperty("items")[0];
        Assert.That(first.GetProperty("imageId").GetInt64(), Is.GreaterThan(0));
        Assert.That(first.GetProperty("sizeBytes").GetInt64(), Is.GreaterThan(0));
        Assert.That((first.GetProperty("displayTitle").GetString(), first.GetProperty("system").GetString(), first.GetProperty("systemName").GetString(),
                first.GetProperty("type").GetString(), first.GetProperty("width").GetInt32(), first.GetProperty("height").GetInt32()),
            Is.EqualTo(("Echo", "gbc", "Game Boy Color", "BoxArt", 64, 64)));
    }

    [Test]
    public async Task ImageResolutions_Filters()
    {
        var (alpha, bravo, echo) = await SeedImageResolutions();

        Assert.That(ImageGameIds(await GetJson("/api/reports/image-resolutions?system=gb")), Is.EqualTo(new[] { alpha, bravo }));
        Assert.That(ImageGameIds(await GetJson("/api/reports/image-resolutions?type=boxart")), Is.EqualTo(new[] { echo, alpha }));
        Assert.That(ImageGameIds(await GetJson("/api/reports/image-resolutions?type=Snap")), Is.EqualTo(new[] { bravo }));
        Assert.That(ImageGameIds(await GetJson("/api/reports/image-resolutions?maxWidth=100")), Is.EqualTo(new[] { echo, alpha }));
        Assert.That(ImageGameIds(await GetJson("/api/reports/image-resolutions?maxHeight=50")), Is.EqualTo(new[] { bravo }));
        Assert.That(ImageGameIds(await GetJson("/api/reports/image-resolutions?maxWidth=64&maxHeight=50")), Is.EqualTo(new[] { echo, bravo }));
        var second = await GetJson("/api/reports/image-resolutions?page=2&pageSize=1");
        Assert.That((second.GetProperty("total").GetInt64(), ImageGameIds(second)), Is.EqualTo((3L, new List<Guid> { alpha })));
    }

    [TestCase("system=nope", "system", "Choose a registered system.")]
    [TestCase("type=Cover", "type", "Must be BoxArt, Snap, TitleScreen or Other.")]
    [TestCase("type=1", "type", "Must be BoxArt, Snap, TitleScreen or Other.")]
    [TestCase("maxWidth=0", "maxWidth", "Must be 1–100000.")]
    [TestCase("maxWidth=100001", "maxWidth", "Must be 1–100000.")]
    [TestCase("maxHeight=0", "maxHeight", "Must be 1–100000.")]
    public async Task ImageResolutions_RejectsInvalidQuery(string query, string field, string message)
    {
        using var response = await _user.GetAsync($"/api/reports/image-resolutions?{query}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").GetProperty(field)[0].GetString(), Is.EqualTo(message));
    }
}
