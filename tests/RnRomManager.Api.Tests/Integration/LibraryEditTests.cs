using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §11 library edits (PATCH, delete release, delete game), §23 mutation rules, D28 (S33), against MariaDB + real ZIPs.</summary>
[Category("Integration")]
public class LibraryEditTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] TetrisWorld = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] TetrisJapan = new SyntheticRomBuilder { Seed = 2, Title = "TETRIS" }.Build();
    private static readonly byte[] Zelda = new SyntheticRomBuilder { Seed = 3, Title = "ZELDA" }.Build();

    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
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
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
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
            [IngestSeeder.Vic20Dat] = [], [IngestSeeder.Plus4Dat] = [], [IngestSeeder.SatellaviewDat] = [], [IngestSeeder.SufamiTurboDat] = [], [IngestSeeder.FdsDat] = [],
            [IngestSeeder.AmigaDat] = [], [IngestSeeder.ThreeDsDat] = [], [IngestSeeder.TurboGrafxCdDat] = [], [IngestSeeder.PcfxDat] = [], [IngestSeeder.NeoGeoPocketDat] = [], [IngestSeeder.NeoGeoPocketColorDat] = [], [IngestSeeder.WonderSwanDat] = [], [IngestSeeder.WonderSwanColorDat] = [], [IngestSeeder.SuperGrafxDat] = [], [IngestSeeder.PocketChallengeV2Dat] = [],
            [IngestSeeder.NintendoDsDat] = [],
            [Gb] =
            [
                new FixtureGame("Tetris (World)", "Tetris (World).gb", TetrisWorld) { ReleaseYear = 1989, Developers = ["Nintendo"] },
                new FixtureGame("Tetris (Japan)", "Tetris (Japan).gb", TetrisJapan),
                new FixtureGame("Legend of Zelda, The (USA)", "Legend of Zelda, The (USA).gb", Zelda),
            ],
        }, titleDb: IngestSeeder.EmptySwitch, mame: IngestSeeder.MinimalMame, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
    }

    private string Removed => Path.Combine(_factory.Root, "ingest", "gb", "_removed");

    private async Task<Guid> Ingest(string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", "gb", name);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var file = new IngestFile("gb", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
        var result = await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default);
        return result!.GameId!.Value;
    }

    private async Task<Guid> Tetris()
    {
        var id = await Ingest("t1.gb", TetrisWorld);
        await Ingest("t2.gb", TetrisJapan);
        return id;
    }

    private Task<GameRow?> Row(Guid id) => Service<IGameRepository>().GetAsync(id, default);

    private string Abs(string zipPath) => Path.Combine(_factory.Root, "roms", zipPath);

    private InfoJsonGame ReadZip(string zipPath)
    {
        using var zip = LibraryZip.Open(Abs(zipPath), Service<InfoJsonReader>());
        return zip.InfoJson!.Game!;
    }

    private async Task<long> RomId(Guid gameId, string fileName) =>
        (await Service<IRomRepository>().ListByGameAsync(gameId, default)).Single(r => r.FileName == fileName).Id;

    private static IEnumerable<TestCaseData> Endpoints()
    {
        var id = Guid.NewGuid();
        yield return new TestCaseData("PATCH", $"/api/games/{id}").SetName("RoleMatrix PATCH game");
        yield return new TestCaseData("DELETE", $"/api/games/{id}").SetName("RoleMatrix DELETE game");
        yield return new TestCaseData("DELETE", $"/api/games/{id}/roms/1").SetName("RoleMatrix DELETE rom");
        yield return new TestCaseData("POST", $"/api/games/{id}/roms/1/primary").SetName("RoleMatrix POST rom primary");
    }

    [TestCaseSource(nameof(Endpoints))]
    public async Task RoleMatrix_AnonymousIs401_UserIs403(string method, string url)
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var anonymousResponse = await anonymous.SendAsync(new HttpMethod(method), url, new { }, sendXsrf: true);
        using var userResponse = await user.SendAsync(new HttpMethod(method), url, new { }, sendXsrf: true);

        Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [TestCaseSource(nameof(Endpoints))]
    public async Task Mutations_RequireAntiforgery(string method, string url)
    {
        using var response = await _admin.SendAsync(new HttpMethod(method), url, new { }, sendXsrf: false);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("AntiforgeryInvalid"));
    }

    [Test]
    public async Task Patch_Metadata_RewritesZipAndDb_KeepsPathAndRoms()
    {
        var id = await Tetris();
        var before = (await Row(id))!;

        using var response = await _admin.PatchAsync($"/api/games/{id}", new
        {
            releaseYear = (int?)null, developers = new[] { " Alexey Pajitnov ", "Nintendo" }, genres = new[] { "Puzzle" }, description = "Falling blocks.",
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);
        Assert.That(body.GetProperty("description").GetString(), Is.EqualTo("Falling blocks."));
        Assert.That(body.GetProperty("roms").GetArrayLength(), Is.EqualTo(2));
        var game = ReadZip(before.ZipPath);
        Assert.That((game.Title, game.ReleaseYear, game.Description), Is.EqualTo(("Tetris", (int?)null, "Falling blocks.")));
        Assert.That(game.Developers, Is.EqualTo(new[] { "Alexey Pajitnov", "Nintendo" }));
        Assert.That(game.Genres, Is.EqualTo(new[] { "Puzzle" }));
        Assert.That(game.Metadata.UpdatedAt, Is.GreaterThan(game.Metadata.CreatedAt));
        var after = (await Row(id))!;
        Assert.That((after.ZipPath, after.ReleaseYear, after.Description), Is.EqualTo((before.ZipPath, (int?)null, "Falling blocks.")));
        Assert.That(game.Roms.Select(r => r.Metadata.Sha1), Is.EquivalentTo(ReadZip(before.ZipPath).Roms.Select(r => r.Metadata.Sha1)));
    }

    [Test]
    public async Task Patch_IgdbId_SetsAndClears_ZipAndDb_AbsentLeavesItUnchanged()
    {
        var id = await Tetris();
        var zipPath = (await Row(id))!.ZipPath;

        using var get = await _admin.GetAsync($"/api/games/{id}");
        var detail = await AuthClient.JsonAsync(get);
        Assert.That((detail.GetProperty("igdbId").ValueKind, detail.GetProperty("igdbConfigured").GetBoolean()),
            Is.EqualTo((JsonValueKind.Null, false)), "the test host sets no IGDB keys");

        using var set = await _admin.PatchAsync($"/api/games/{id}", new { igdbId = 1234 });
        Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await AuthClient.JsonAsync(set)).GetProperty("igdbId").GetInt32(), Is.EqualTo(1234));
        Assert.That(((await Row(id))!.IgdbId, ReadZip(zipPath).IgdbId), Is.EqualTo(((int?)1234, (int?)1234)));

        using var other = await _admin.PatchAsync($"/api/games/{id}", new { description = "Falling blocks." });
        Assert.That(((await Row(id))!.IgdbId, ReadZip(zipPath).IgdbId), Is.EqualTo(((int?)1234, (int?)1234)), "absent = unchanged");

        using var clear = await _admin.PatchAsync($"/api/games/{id}", new { igdbId = (int?)null });
        Assert.That(clear.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(((await Row(id))!.IgdbId, ReadZip(zipPath).IgdbId), Is.EqualTo(((int?)null, (int?)null)));
    }

    [Test]
    public async Task Patch_ChangedIgdbId_DropsActions_AndClearsTheIgdbColumns()
    {
        var id = await Tetris();
        var zipPath = (await Row(id))!.ZipPath;
        await GameActionsSeed.SeedAsync(_factory, id);
        Assert.That(ReadZip(zipPath).Actions?.CoverImagePulled, Is.EqualTo(GameActionsSeed.Pulled));

        using var set = await _admin.PatchAsync($"/api/games/{id}", new { igdbId = 1234 });

        Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ReadZip(zipPath).Actions, Is.Null);
        Assert.That(await GameActionsSeed.ReadAsync(_factory, id), Is.EqualTo(((DateTime?)null, (double?)null)));
    }

    [Test]
    public async Task Patch_UnchangedIgdbId_KeepsActions_AndTheIgdbColumns()
    {
        var id = await Tetris();
        var zipPath = (await Row(id))!.ZipPath;
        await GameActionsSeed.SeedAsync(_factory, id);

        await Rate(id, new GamePatch { SetRating = true, Rating = 4 });
        using var same = await _admin.PatchAsync($"/api/games/{id}", new { igdbId = (int?)null, description = "Blocks." });

        Assert.That(same.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ReadZip(zipPath).Actions?.CoverImagePulled, Is.EqualTo(GameActionsSeed.Pulled));
        Assert.That((await GameActionsSeed.ReadAsync(_factory, id))!.Value.Rating, Is.EqualTo(81.5));
    }

    [Test]
    public async Task Patch_ChangedIgdbId_FailingClear_StillSucceeds()
    {
        var id = await Tetris();
        await GameActionsSeed.SeedAsync(_factory, id);
        // Fails only the clear: the mirror never touches igdb_rating.
        await using (var db = Service<RnRomManager.Core.Database.IDbConnectionFactory>().Create())
            await Dapper.SqlMapper.ExecuteAsync(db, """
                CREATE TRIGGER fail_igdb_clear BEFORE UPDATE ON game_actions FOR EACH ROW
                IF OLD.igdb_rating IS NOT NULL AND NEW.igdb_rating IS NULL THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'no'; END IF
                """);
        try
        {
            using var set = await _admin.PatchAsync($"/api/games/{id}", new { igdbId = 1234 });

            Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await Row(id))!.IgdbId, Is.EqualTo(1234));
            Assert.That((await GameActionsSeed.ReadAsync(_factory, id))!.Value.Rating, Is.EqualTo(81.5), "the clear failed");
        }
        finally
        {
            await using var db = Service<RnRomManager.Core.Database.IDbConnectionFactory>().Create();
            await Dapper.SqlMapper.ExecuteAsync(db, "DROP TRIGGER fail_igdb_clear");
        }
    }

    private async Task Rate(Guid id, GamePatch patch) =>
        Assert.That((await Service<LibraryEditor>().PatchAsync(id, patch, null, default)).Code, Is.Null);

    [Test]
    public async Task PatchRatingOnly_KeepsTitleMetadataAndZipPath()
    {
        var id = await Tetris();
        var before = (await Row(id))!;
        var game = ReadZip(before.ZipPath);

        await Rate(id, new GamePatch { SetRating = true, Rating = 4, Favourite = true });

        var after = (await Row(id))!;
        var rated = ReadZip(after.ZipPath);
        Assert.That((after.ZipPath, after.Rating, after.Favourite), Is.EqualTo((before.ZipPath, (int?)4, true)));
        Assert.That((rated.Rating, rated.Favourite, rated.Title, rated.ReleaseYear, rated.Description),
            Is.EqualTo(((int?)4, true, game.Title, game.ReleaseYear, game.Description)));
        Assert.That(rated.Genres, Is.EqualTo(game.Genres));
        Assert.That(rated.Roms.Select(r => r.Metadata.Sha1), Is.EqualTo(game.Roms.Select(r => r.Metadata.Sha1)));
    }

    [Test]
    public async Task PatchRating_NullClears_AbsentFavouriteIsUnchanged()
    {
        var id = await Tetris();
        await Rate(id, new GamePatch { SetRating = true, Rating = 2, Favourite = true });

        await Rate(id, new GamePatch { SetRating = true, Rating = null });

        var game = ReadZip((await Row(id))!.ZipPath);
        Assert.That((game.Rating, game.Favourite), Is.EqualTo(((int?)null, true)));
    }

    [Test]
    public async Task TitleEdit_KeepsRatingAndFavourite()
    {
        var id = await Tetris();
        await Rate(id, new GamePatch { SetRating = true, Rating = 5, Favourite = true });

        using var response = await _admin.PatchAsync($"/api/games/{id}", new { title = "Tetris DX" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var row = (await Row(id))!;
        var game = ReadZip(row.ZipPath);
        Assert.That((row.Title, row.Rating, row.Favourite, game.Rating, game.Favourite), Is.EqualTo(("Tetris DX", (int?)5, true, (int?)5, true)));
    }

    [Test]
    public async Task Patch_TitleRename_WritesNewZip_CommitsThenDeletesOld()
    {
        var id = await Tetris();
        var old = (await Row(id))!.ZipPath;
        Assert.That(Directory.Exists(Abs("gb/Z")), Is.False, "B30: the new bucket folder is created by the rename");

        using var response = await _admin.PatchAsync($"/api/games/{id}", new { title = "Zoo, The" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("displayTitle").GetString(), Is.EqualTo("The Zoo"));
        var row = (await Row(id))!;
        Assert.That((row.ZipPath, row.Title, row.DisplayTitle, row.Bucket), Is.EqualTo(("gb/Z/Zoo, The.zip", "Zoo, The", "The Zoo", "Z")));
        Assert.That(File.Exists(Abs(old)), Is.False);
        var game = ReadZip(row.ZipPath);
        Assert.That((game.GameId, game.Title, game.DisplayTitle, game.Roms.Count), Is.EqualTo((id, "Zoo, The", "The Zoo", 2)));
    }

    [Test]
    public async Task Patch_RenameOntoAnotherGamesName_GetsClashSuffix()
    {
        var tetris = await Tetris();
        var zelda = await Ingest("z.gb", Zelda);

        using var response = await _admin.PatchAsync($"/api/games/{tetris}", new { title = "legend of zelda, the" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Row(tetris))!.ZipPath, Is.EqualTo("gb/L/legend of zelda, the (2).zip"));
        Assert.That((await Row(zelda))!.ZipPath, Is.EqualTo("gb/L/Legend of Zelda, The.zip"));
        Assert.That(File.Exists(Abs("gb/L/Legend of Zelda, The.zip")), Is.True);
    }

    [Test]
    public async Task Patch_CaseOnlyTitleChange_KeepsTheFile()
    {
        var id = await Tetris();

        using var response = await _admin.PatchAsync($"/api/games/{id}", new { title = "TETRIS" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var row = (await Row(id))!;
        Assert.That((row.ZipPath, row.Title), Is.EqualTo(("gb/T/Tetris.zip", "TETRIS")));
        Assert.That(ReadZip(row.ZipPath).Title, Is.EqualTo("TETRIS"));
    }

    [Test]
    public async Task Patch_InvalidBody_Is400_AndNothingChanges()
    {
        var id = await Tetris();
        var before = await File.ReadAllBytesAsync(Abs((await Row(id))!.ZipPath));

        using var response = await _admin.PatchAsync($"/api/games/{id}", new { title = "  ", releaseYear = 1900 });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var errors = (await AuthClient.JsonAsync(response)).GetProperty("errors");
        Assert.That((errors.TryGetProperty("title", out _), errors.TryGetProperty("releaseYear", out _)), Is.EqualTo((true, true)));
        Assert.That(await File.ReadAllBytesAsync(Abs((await Row(id))!.ZipPath)), Is.EqualTo(before));
    }

    [Test]
    public async Task DeleteRelease_MovesRomToRemoved_RebuildsZip_RecomputesPrimary()
    {
        var id = await Tetris();
        var zipPath = (await Row(id))!.ZipPath;
        Directory.CreateDirectory(Removed);
        await File.WriteAllBytesAsync(Path.Combine(Removed, "tetris (world).gb"), [1]);

        using var response = await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{id}/roms/{await RomId(id, "Tetris (World).gb")}", null, sendXsrf: true);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Removed, "Tetris (World) (2).gb")), Is.EqualTo(TetrisWorld), "clash suffix, any case");
        var game = ReadZip(zipPath);
        Assert.That(game.Roms.Select(r => (r.FileName, r.IsPrimary)), Is.EqualTo(new[] { ("Tetris (Japan).gb", true) }));
        using (var zip = LibraryZip.Open(Abs(zipPath), Service<InfoJsonReader>()))
            Assert.That(zip.HasEntry("roms/Tetris (World).gb"), Is.False);
        var roms = await Service<IRomRepository>().ListByGameAsync(id, default);
        Assert.That(roms.Select(r => (r.FileName, r.IsPrimary)), Is.EqualTo(new[] { ("Tetris (Japan).gb", true) }));
    }

    [Test]
    public async Task SetPrimary_RewritesZipAndDb_ReturnsDetail()
    {
        var id = await Tetris();
        var zipPath = (await Row(id))!.ZipPath;
        var japan = await RomId(id, "Tetris (Japan).gb");

        using var response = await _admin.SendAsync(HttpMethod.Post, $"/api/games/{id}/roms/{japan}/primary", null, sendXsrf: true);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var primary = (await AuthClient.JsonAsync(response)).GetProperty("roms").EnumerateArray().Single(r => r.GetProperty("isPrimary").GetBoolean());
        Assert.That(primary.GetProperty("id").GetInt64(), Is.EqualTo(japan));
        Assert.That(ReadZip(zipPath).Roms.Where(r => r.IsPrimary).Select(r => r.FileName), Is.EqualTo(new[] { "Tetris (Japan).gb" }));
        var roms = await Service<IRomRepository>().ListByGameAsync(id, default);
        Assert.That(roms.Where(r => r.IsPrimary).Select(r => r.FileName), Is.EqualTo(new[] { "Tetris (Japan).gb" }));
    }

    [Test]
    public async Task SetPrimary_SticksWhenABetterRankedReleaseIsAdded()
    {
        var id = await Ingest("t2.gb", TetrisJapan);
        await Ingest("t1.gb", TetrisWorld); // World outranks Japan
        var zipPath = (await Row(id))!.ZipPath;
        Assert.That(ReadZip(zipPath).Roms.Single(r => r.IsPrimary).FileName, Is.EqualTo("Tetris (World).gb"));
        var world = await RomId(id, "Tetris (World).gb");
        using (var deleted = await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{id}/roms/{world}", null, sendXsrf: true))
            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using (var set = await _admin.SendAsync(HttpMethod.Post, $"/api/games/{id}/roms/{await RomId(id, "Tetris (Japan).gb")}/primary", null, sendXsrf: true))
            Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That(await Ingest("t1-again.gb", TetrisWorld), Is.EqualTo(id));

        var game = ReadZip(zipPath);
        Assert.That(game.Roms.Select(r => (r.FileName, r.IsPrimary)), Is.EquivalentTo(new[] { ("Tetris (Japan).gb", true), ("Tetris (World).gb", false) }));
        var roms = await Service<IRomRepository>().ListByGameAsync(id, default);
        Assert.That(roms.Single(r => r.IsPrimary).FileName, Is.EqualTo("Tetris (Japan).gb"));
    }

    [Test]
    public async Task DeleteLastRelease_DeletesTheGame()
    {
        var id = await Ingest("z.gb", Zelda);
        var zipPath = (await Row(id))!.ZipPath;

        using var response = await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{id}/roms/{await RomId(id, "Legend of Zelda, The (USA).gb")}", null, sendXsrf: true);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(File.Exists(Abs(zipPath)), Is.False);
        Assert.That(await Row(id), Is.Null);
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Removed, "Legend of Zelda, The (USA).gb")), Is.EqualTo(Zelda));
    }

    [Test]
    public async Task DeleteGame_MovesEveryRomToRemoved_DeletesZipAndRows()
    {
        var id = await Tetris();
        var zipPath = (await Row(id))!.ZipPath;

        using var response = await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{id}", null, sendXsrf: true);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(File.Exists(Abs(zipPath)), Is.False);
        Assert.That(await Row(id), Is.Null);
        Assert.That(await Service<IRomRepository>().ListByGameAsync(id, default), Is.Empty);
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Removed, "Tetris (World).gb")), Is.EqualTo(TetrisWorld));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Removed, "Tetris (Japan).gb")), Is.EqualTo(TetrisJapan));
    }

    [Test]
    public async Task RemovedRoms_AreIgnoredByTheNextIngest()
    {
        var id = await Tetris();
        (await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{id}", null, sendXsrf: true)).Dispose();

        var pending = Service<IngestFileScanner>().Scan();

        Assert.That(pending.SelectMany(p => p.Files), Is.Empty);
    }

    [Test]
    public async Task StaleIds_Are404()
    {
        var tetris = await Tetris();
        var zelda = await Ingest("z.gb", Zelda);
        var tetrisRom = await RomId(tetris, "Tetris (World).gb");

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Patch, $"/api/games/{Guid.NewGuid()}"),
            (HttpMethod.Delete, $"/api/games/{Guid.NewGuid()}"),
            (HttpMethod.Delete, $"/api/games/{zelda}/roms/{tetrisRom}"),
        })
        {
            using var response = await _admin.SendAsync(method, url, new { }, sendXsrf: true);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), url);
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("NotFound"));
        }
        Assert.That(Directory.Exists(Removed) && Directory.EnumerateFiles(Removed).Any(), Is.False);
    }

    [Test]
    public async Task WhileAJobRuns_EveryEditIs409JobRunning()
    {
        var id = await Tetris();
        var romId = await RomId(id, "Tetris (World).gb");
        using var held = Service<JobLock>().TryAcquire("test");

        foreach (var (method, url) in new[] { (HttpMethod.Patch, $"/api/games/{id}"), (HttpMethod.Delete, $"/api/games/{id}/roms/{romId}"), (HttpMethod.Delete, $"/api/games/{id}") })
        {
            using var response = await _admin.SendAsync(method, url, new { title = "Other" }, sendXsrf: true);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), $"{method} {url}");
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("JobRunning"));
        }
        Assert.That((await Row(id))!.Title, Is.EqualTo("Tetris"));
    }

    /// <summary>Release editing B4: the PATCH answers the new file name.</summary>
    [Test]
    public async Task ReleasePatch_Renames_AndAnswersTheName()
    {
        var id = await Tetris();

        using var response = await _admin.PatchAsync($"/api/games/{id}/roms/{await RomId(id, "Tetris (World).gb")}", new { hack = true });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("fileName").GetString(), Is.EqualTo("Tetris (World) (Hack).gb"));
        Assert.That(ReadZip((await Row(id))!.ZipPath).Roms.Select(r => r.FileName), Does.Contain("Tetris (World) (Hack).gb"));
    }

    [TestCase("""{"kind":"game"}""", "kind", "Must be release, update or dlc.")]
    [TestCase("""{"regions":["Mars"]}""", "regions", "Must be known No-Intro regions.")]
    [TestCase("""{"regions":[""]}""", "regions", "Must be known No-Intro regions.")]
    [TestCase("""{"languages":["E n"]}""", "languages", "Must be 1–16 letters or '-' each.")]
    [TestCase("""{"revision":"(Rev 1)"}""", "revision", "Must be at most 32 characters without parentheses.")]
    [TestCase("""{"disc":0}""", "disc", "Must be 1–99.")]
    [TestCase("""{"disc":2}""", "disc", "Disc numbers are for disc systems.")]
    [TestCase("""{"hack":"yes"}""", "hack", "Must be true or false.")]
    [TestCase("""{"version":"v1"}""", "version", "Must be like 1.0.1.")]
    [TestCase("""{"version":"1.0.1"}""", "version", "Only an update has a version.")]
    [TestCase("""{"title":"x"}""", "title", "Unknown field.")]
    [TestCase("""[]""", "", "A JSON object is required.")]
    public async Task ReleasePatch_InvalidField_Is400_AndNothingChanges(string json, string field, string message)
    {
        var id = await Tetris();
        var before = await File.ReadAllBytesAsync(Abs((await Row(id))!.ZipPath));

        using var response = await _admin.SendAsync(HttpMethod.Patch, $"/api/games/{id}/roms/{await RomId(id, "Tetris (World).gb")}",
            System.Text.Json.JsonDocument.Parse(json).RootElement, sendXsrf: true);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var errors = (await AuthClient.JsonAsync(response)).GetProperty("errors");
        Assert.That(errors.GetProperty(field)[0].GetString(), Is.EqualTo(message));
        Assert.That(await File.ReadAllBytesAsync(Abs((await Row(id))!.ZipPath)), Is.EqualTo(before));
    }

    [Test]
    public async Task ReleasePatch_ValidatesOnlyPresentFields_UpdateGetsAVersion_And404s_And409WhileAJobRuns()
    {
        var id = await Tetris();
        var world = await RomId(id, "Tetris (World).gb");

        using (var empty = await _admin.PatchAsync($"/api/games/{id}/roms/{world}", new { }))
            Assert.That((await AuthClient.JsonAsync(empty)).GetProperty("fileName").GetString(), Is.EqualTo("Tetris (World).gb"));
        using (var update = await _admin.PatchAsync($"/api/games/{id}/roms/{world}", new { kind = "update", version = "1.0.1" }))
            Assert.That((await AuthClient.JsonAsync(update)).GetProperty("fileName").GetString(), Is.EqualTo("Tetris (World)[1.0.1].gb"));
        using (var missing = await _admin.PatchAsync($"/api/games/{id}/roms/999999", new { hack = true }))
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (Service<JobLock>().TryAcquire("test"))
        {
            using var busy = await _admin.PatchAsync($"/api/games/{id}/roms/{world}", new { hack = true });
            Assert.That(await AuthClient.CodeAsync(busy), Is.EqualTo("JobRunning"));
        }
    }

    [TestCase(true, "InfoJsonNewerSchema")]
    [TestCase(false, "TargetZipUnreadable")]
    public async Task UnwritableZip_Is409_AndNothingMoves(bool newerSchema, string code)
    {
        var id = await Tetris();
        var path = Abs((await Row(id))!.ZipPath);
        if (newerSchema)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            var entry = zip.GetEntry("info.json")!;
            JsonNode info;
            using (var reader = new StreamReader(entry.Open()))
                info = JsonNode.Parse(await reader.ReadToEndAsync())!;
            info["schemaVersion"] = InfoJsonReader.CurrentVersion + 1;
            entry.Delete();
            await using var writer = new StreamWriter(zip.CreateEntry("info.json").Open());
            await writer.WriteAsync(info.ToJsonString());
        }
        else
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var before = await File.ReadAllBytesAsync(path);
        var romId = await RomId(id, "Tetris (World).gb");

        foreach (var (method, url) in new[] { (HttpMethod.Patch, $"/api/games/{id}"), (HttpMethod.Delete, $"/api/games/{id}/roms/{romId}"), (HttpMethod.Delete, $"/api/games/{id}") })
        {
            using var response = await _admin.SendAsync(method, url, new { title = "Other" }, sendXsrf: true);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), $"{method} {url}");
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo(code));
        }
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(before));
        Assert.That(await Row(id), Is.Not.Null);
        Assert.That(Directory.Exists(Removed) && Directory.EnumerateFiles(Removed).Any(), Is.False);
    }

    [Test]
    public async Task CorruptEntry_IsVerifyFailed_AndTheCopyIsTakenBack()
    {
        var id = await Tetris();
        var path = Abs((await Row(id))!.ZipPath);
        // Same info.json, but the Japan entry's compressed bytes are garbage: copying it fails (D34).
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("roms/Tetris (Japan).gb")!.Delete();
            using var stream = zip.CreateEntry("roms/Tetris (Japan).gb", CompressionLevel.NoCompression).Open();
            stream.Write(new byte[TetrisJapan.Length + 10]);
        }

        using var response = await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{id}/roms/{await RomId(id, "Tetris (World).gb")}", null, sendXsrf: true);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("VerifyFailed"));
        Assert.That(ReadZip((await Row(id))!.ZipPath).Roms.Count, Is.EqualTo(2), "the ZIP is unchanged");
        Assert.That(Directory.EnumerateFiles(Removed), Is.Empty, "the World copy is taken back");
    }

    private async Task<HttpResponseMessage> DeleteWorld(Guid id) =>
        await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{id}/roms/{await RomId(id, "Tetris (World).gb")}", null, sendXsrf: true);

    [Test]
    public async Task DeleteRelease_StreamsTheCopy_WithTheRecordedBytesAndSha1()
    {
        var id = await Tetris();
        var recorded = ReadZip((await Row(id))!.ZipPath).Roms.Single(r => r.FileName == "Tetris (World).gb").Metadata.Sha1;

        using var response = await DeleteWorld(id);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var copy = await File.ReadAllBytesAsync(Path.Combine(Removed, "Tetris (World).gb"));
        Assert.That(copy, Is.EqualTo(TetrisWorld));
        Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(copy)), Is.EqualTo(recorded));
    }

    [Test]
    public async Task DeleteRelease_CorruptCompressedEntry_IsVerifyFailed_LeavesNoCopyAndTheZipUnchanged()
    {
        var id = await Tetris();
        var path = Abs((await Row(id))!.ZipPath);
        CorruptDeflateData(path, "roms/Tetris (World).gb");
        var before = await File.ReadAllBytesAsync(path);

        using var response = await DeleteWorld(id);

        Assert.That((response.StatusCode, await AuthClient.CodeAsync(response)), Is.EqualTo((HttpStatusCode.InternalServerError, "VerifyFailed")));
        Assert.That(Directory.Exists(Removed) && Directory.EnumerateFiles(Removed).Any(), Is.False, "the partial copy is deleted");
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(before));
    }

    [Test]
    public async Task DeleteRelease_EntryShorterThanRecorded_IsCopiedAsItIs()
    {
        var id = await Tetris();
        var path = Abs((await Row(id))!.ZipPath);
        var shorter = TetrisWorld[..^16];
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("roms/Tetris (World).gb")!.Delete();
            using var stream = zip.CreateEntry("roms/Tetris (World).gb").Open();
            stream.Write(shorter);
        }

        using var response = await DeleteWorld(id);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Removed, "Tetris (World).gb")), Is.EqualTo(shorter), "copied with the SHA-1 warning");
    }

    [Test]
    public async Task DeleteRelease_RemovedFolderIsAFile_IsIoError_AndTheZipUnchanged()
    {
        var id = await Tetris();
        var path = Abs((await Row(id))!.ZipPath);
        var before = await File.ReadAllBytesAsync(path);
        // Replace the folder with a regular file of that name: Directory.CreateDirectory then fails on every OS.
        if (Directory.Exists(Removed))
            Directory.Delete(Removed, recursive: true);
        await File.WriteAllBytesAsync(Removed, [1]);

        using var response = await DeleteWorld(id);

        Assert.That((response.StatusCode, await AuthClient.CodeAsync(response)), Is.EqualTo((HttpStatusCode.InternalServerError, "IoError")));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(before));
        Assert.That(File.ReadAllBytes(Removed), Is.EqualTo(new byte[] { 1 }));
    }

    /// <summary>Overwrites the first byte of an entry's Deflate data with 0xFF (a final block of the reserved type 3).</summary>
    private static void CorruptDeflateData(string zipPath, string entryName)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var name = System.Text.Encoding.UTF8.GetBytes(entryName);
        for (var i = 0; i + 30 + name.Length <= bytes.Length; i++)
        {
            if (BitConverter.ToUInt32(bytes, i) != 0x04034B50 || !bytes.AsSpan(i + 30, name.Length).SequenceEqual(name))
                continue;
            Assert.That(BitConverter.ToUInt16(bytes, i + 8), Is.EqualTo(8), "the entry is Deflate");
            var data = i + 30 + BitConverter.ToUInt16(bytes, i + 26) + BitConverter.ToUInt16(bytes, i + 28);
            bytes[data] = 0xFF;
            File.WriteAllBytes(zipPath, bytes);
            return;
        }
        Assert.Fail($"No local header for {entryName}");
    }
}
