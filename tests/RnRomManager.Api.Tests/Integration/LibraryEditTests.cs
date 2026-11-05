using System.IO.Compression;
using System.Net;
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

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", UserRole.Admin);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        var cache = Service<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [Gb] =
            [
                new FixtureGame("Tetris (World)", "Tetris (World).gb", TetrisWorld) { ReleaseYear = 1989, Developers = ["Nintendo"] },
                new FixtureGame("Tetris (Japan)", "Tetris (Japan).gb", TetrisJapan),
                new FixtureGame("Legend of Zelda, The (USA)", "Legend of Zelda, The (USA).gb", Zelda),
            ],
        });
        cache.Load();
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _factory.Dispose();
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

    private async Task Rate(Guid id, GamePatch patch) =>
        Assert.That((await Service<LibraryEditor>().PatchAsync(id, patch, default)).Code, Is.Null);

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
        using var held = Service<JobLock>().TryAcquire();

        foreach (var (method, url) in new[] { (HttpMethod.Patch, $"/api/games/{id}"), (HttpMethod.Delete, $"/api/games/{id}/roms/{romId}"), (HttpMethod.Delete, $"/api/games/{id}") })
        {
            using var response = await _admin.SendAsync(method, url, new { title = "Other" }, sendXsrf: true);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), $"{method} {url}");
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("JobRunning"));
        }
        Assert.That((await Row(id))!.Title, Is.EqualTo("Tetris"));
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
}
