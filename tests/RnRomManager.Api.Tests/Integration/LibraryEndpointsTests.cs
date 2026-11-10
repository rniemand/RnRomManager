using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §23 <c>/api/systems</c>, <c>/api/games*</c> reads, images and downloads (S32), against MariaDB + real ZIPs.</summary>
[Category("Integration")]
public class LibraryEndpointsTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] TetrisWorld = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] TetrisJapan = new SyntheticRomBuilder { Seed = 2, Title = "TETRIS" }.Build();
    private static readonly byte[] Pinball = new SyntheticRomBuilder { Seed = 3, Title = "POKEPINBALL" }.Build();
    private static readonly byte[] Arcade = new SyntheticRomBuilder { Seed = 4, Title = "ARCADE" }.Build();

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
                new FixtureGame("Tetris (World)", "Tetris (World).gb", TetrisWorld) { ReleaseYear = 1989, Developers = ["Nintendo"], Genres = ["Puzzle"] },
                new FixtureGame("Tetris (Japan)", "Tetris (Japan).gb", TetrisJapan),
                new FixtureGame("Pokémon Pinball (Europe)", "Pokémon Pinball (Europe).gb", Pinball) { Publishers = ["Nintendo"] },
                new FixtureGame("3 Choume no Tama (Japan)", "3 Choume no Tama (Japan).gb", Arcade),
            ],
        });
        cache.Load();
    }

    [TearDown]
    public void TearDown()
    {
        _user.Dispose();
        _factory.Dispose();
    }

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
        Assert.That(await Ingest("t2.gb", TetrisJapan), Is.EqualTo(id));
        return id;
    }

    private async Task<JsonElement> GetJson(string url)
    {
        using var response = await _user.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await AuthClient.JsonAsync(response);
    }

    private async Task<string> ZipPath(Guid id) =>
        Path.Combine(_factory.Root, "roms", (await Service<IGameRepository>().GetAsync(id, default))!.ZipPath);

    private static IEnumerable<TestCaseData> Endpoints()
    {
        var id = Guid.NewGuid();
        yield return new TestCaseData("/api/systems").SetName("RoleMatrix GET systems");
        yield return new TestCaseData("/api/games").SetName("RoleMatrix GET games");
        yield return new TestCaseData($"/api/games/{id}").SetName("RoleMatrix GET game");
        yield return new TestCaseData($"/api/games/{id}/images/1").SetName("RoleMatrix GET image");
        yield return new TestCaseData($"/api/games/{id}/download").SetName("RoleMatrix GET game download");
        yield return new TestCaseData($"/api/games/{id}/info-json").SetName("RoleMatrix GET info-json");
        yield return new TestCaseData($"/api/games/{id}/roms/1/download").SetName("RoleMatrix GET rom download");
    }

    [TestCaseSource(nameof(Endpoints))]
    public async Task RoleMatrix_AnonymousIs401_UserIsAllowed(string url)
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);

        using var anonymousResponse = await anonymous.GetAsync(url);
        using var userResponse = await _user.GetAsync(url);

        Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Systems_ListsEveryRegisteredSystem_WithCountsAndSortedRegions()
    {
        await Tetris();

        var systems = await GetJson("/api/systems");

        // S1: ordered by display name.
        Assert.That(systems.EnumerateArray().Select(s => s.GetProperty("id").GetString()),
            Is.EqualTo(new[] { "a2600", "coleco", "gb", "gba", "gbc", "gg", "n64", "nes", "snes" }));
        JsonElement Find(string id) => systems.EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        var gb = Find("gb");
        Assert.That(gb.GetProperty("displayName").GetString(), Is.EqualTo("Game Boy"));
        Assert.That((gb.GetProperty("gameCount").GetInt32(), gb.GetProperty("romCount").GetInt32()), Is.EqualTo((1, 2)));
        Assert.That(gb.GetProperty("regions").EnumerateArray().Select(r => r.GetString()), Is.EqualTo(new[] { "Japan", "World" }));
        var gbc = Find("gbc");
        Assert.That((gbc.GetProperty("gameCount").GetInt32(), gbc.GetProperty("regions").GetArrayLength()), Is.EqualTo((0, 0)));
        Assert.That(systems.EnumerateArray().Select(s => s.GetProperty("displayName").GetString()), Is.EqualTo(new[]
        {
            "Atari 2600", "ColecoVision", "Game Boy", "Game Boy Advance", "Game Boy Color", "Game Gear", "Nintendo 64", "Nintendo Entertainment System",
            "Super Nintendo Entertainment System"
        }));
    }

    [Test]
    public async Task Games_ListsPage_WithBoxArtUrl()
    {
        var id = await Tetris();

        var page = await GetJson("/api/games");

        Assert.That((page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt64()),
            Is.EqualTo((1, 48, 1L)));
        var item = page.GetProperty("items")[0];
        Assert.That(item.GetProperty("id").GetGuid(), Is.EqualTo(id));
        Assert.That((item.GetProperty("system").GetString(), item.GetProperty("displayTitle").GetString(), item.GetProperty("releaseYear").GetInt32()),
            Is.EqualTo(("gb", "Tetris", 1989)));
        Assert.That(item.GetProperty("regions").EnumerateArray().Select(r => r.GetString()), Is.EqualTo(new[] { "Japan", "World" }));
        Assert.That(item.GetProperty("romCount").GetInt32(), Is.EqualTo(2));
        using var art = await _user.GetAsync(item.GetProperty("boxArtUrl").GetString()!);
        Assert.That(art.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Games_PrimarySizeBytes_IsThePrimaryReleaseSize()
    {
        var id = await Tetris();
        // Make the two releases differ so the primary one is the only right answer.
        await using (var connection = Service<IDbConnectionFactory>().Create())
            await connection.ExecuteAsync("UPDATE roms SET size_bytes = CASE WHEN is_primary = 1 THEN 1234 ELSE 9999 END WHERE game_id = @id", new { id });

        var item = (await GetJson("/api/games")).GetProperty("items")[0];

        Assert.That(item.GetProperty("primarySizeBytes").GetInt64(), Is.EqualTo(1234));
    }

    [Test]
    public async Task Games_FiltersFromQueryString()
    {
        await Tetris();
        await Ingest("p.gb", Pinball);
        await Ingest("a.gb", Arcade);

        async Task<string[]> Titles(string query) =>
            (await GetJson("/api/games?" + query)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("displayTitle").GetString()!).ToArray();

        Assert.That(await Titles("letter=%23"), Is.EqualTo(new[] { "3 Choume no Tama" }));
        Assert.That(await Titles("letter=t&system=gb"), Is.EqualTo(new[] { "Tetris" }));
        Assert.That(await Titles("region=Europe"), Is.EqualTo(new[] { "Pokémon Pinball" }));
        Assert.That(await Titles("q=%20pinBALL%20"), Is.EqualTo(new[] { "Pokémon Pinball" }));
        Assert.That((await Titles("sort=year"))[0], Is.EqualTo("Tetris"), "nulls last");
        Assert.That((await Titles("sort=year&dir=desc"))[0], Is.EqualTo("Tetris"), "nulls last");
        Assert.That(await Titles("sort=added"), Is.EqualTo(new[] { "3 Choume no Tama", "Pokémon Pinball", "Tetris" }), "added defaults to desc");
        Assert.That(await Titles("sort=title&dir=desc&pageSize=2&page=1"), Is.EqualTo(new[] { "Tetris", "Pokémon Pinball" }));
        Assert.That(await Titles("page=9"), Is.Empty);
    }

    [TestCase("page=0", "page")]
    [TestCase("pageSize=201", "pageSize")]
    [TestCase("pageSize=0", "pageSize")]
    [TestCase("sort=name", "sort")]
    [TestCase("dir=up", "dir")]
    [TestCase("letter=AB", "letter")]
    [TestCase("letter=1", "letter")]
    [TestCase("system=md", "system")]
    public async Task Games_InvalidQuery_Is400WithFieldError(string query, string field)
    {
        using var response = await _user.GetAsync("/api/games?" + query);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await AuthClient.JsonAsync(response);
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(body.GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }

    [Test]
    public async Task GameDetail_HasGameFields_RomsPrimaryFirst_AndImageUrls()
    {
        var id = await Tetris();

        var game = await GetJson($"/api/games/{id}");

        Assert.That(game.GetProperty("id").GetGuid(), Is.EqualTo(id));
        Assert.That((game.GetProperty("system").GetString(), game.GetProperty("title").GetString(), game.GetProperty("displayTitle").GetString()),
            Is.EqualTo(("gb", "Tetris", "Tetris")));
        Assert.That(game.GetProperty("systemName").GetString(), Is.EqualTo("Game Boy"));
        Assert.That(game.GetProperty("developers").EnumerateArray().Select(d => d.GetString()), Is.EqualTo(new[] { "Nintendo" }));
        Assert.That(game.GetProperty("genres").EnumerateArray().Select(d => d.GetString()), Is.EqualTo(new[] { "Puzzle" }));
        Assert.That(game.GetProperty("hasDefaultBoxArt").GetBoolean(), Is.True);
        Assert.That(game.GetProperty("metadata").TryGetProperty("createdAt", out _), Is.True);
        Assert.That(game.TryGetProperty("schemaVersion", out _), Is.False);

        var roms = game.GetProperty("roms").EnumerateArray().ToList();
        Assert.That(roms.Select(r => r.GetProperty("fileName").GetString()), Is.EqualTo(new[] { "Tetris (World).gb", "Tetris (Japan).gb" }));
        var primary = roms[0];
        Assert.That(primary.GetProperty("isPrimary").GetBoolean(), Is.True);
        Assert.That((primary.GetProperty("identificationMethod").GetString(), primary.GetProperty("datName").GetString()), Is.EqualTo(("Dat", Gb)));
        Assert.That(primary.GetProperty("sha1").GetString(), Is.EqualTo(Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(TetrisWorld))));
        Assert.That(primary.GetProperty("sizeBytes").GetInt64(), Is.EqualTo(TetrisWorld.Length));
        Assert.That(primary.GetProperty("header").GetProperty("title").GetString(), Is.EqualTo("TETRIS"));
        Assert.That(primary.GetProperty("downloadUrl").GetString(), Is.EqualTo($"/api/games/{id}/roms/{primary.GetProperty("id").GetInt64()}/download"));

        var image = game.GetProperty("images")[0];
        Assert.That((image.GetProperty("type").GetString(), image.GetProperty("isDefault").GetBoolean(), image.GetProperty("width").GetInt32()),
            Is.EqualTo(("BoxArt", true, 512)));
        Assert.That(image.GetProperty("url").GetString(), Is.EqualTo($"/api/games/{id}/images/{image.GetProperty("id").GetInt64()}"));
    }

    [Test]
    public async Task Rating_UserCanRate_ReturnsFullDetail_ListAndDetailAndZipShowIt()
    {
        var id = await Tetris();

        using var response = await _user.PatchAsync($"/api/games/{id}/rating", new { rating = 4, favourite = true });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);
        Assert.That((body.GetProperty("rating").GetInt32(), body.GetProperty("favourite").GetBoolean()), Is.EqualTo((4, true)));
        Assert.That(body.GetProperty("roms").GetArrayLength(), Is.EqualTo(2), "the full detail");
        var detail = await GetJson($"/api/games/{id}");
        Assert.That(detail.GetProperty("roms")[0].GetProperty("id").GetInt64(), Is.EqualTo(body.GetProperty("roms")[0].GetProperty("id").GetInt64()),
            "the response carries the rewritten ids");
        var item = (await GetJson("/api/games")).GetProperty("items")[0];
        Assert.That((item.GetProperty("rating").GetInt32(), item.GetProperty("favourite").GetBoolean(), item.GetProperty("systemName").GetString()),
            Is.EqualTo((4, true, Service<ISystemRegistry>().ById("gb")!.DisplayName)));
        Assert.That(item.GetProperty("systemShortName").GetString(), Is.EqualTo("GB"));
        using (var zip = LibraryZip.Open(await ZipPath(id), Service<InfoJsonReader>()))
            Assert.That((zip.InfoJson!.Game!.Rating, zip.InfoJson.Game.Favourite), Is.EqualTo(((int?)4, true)));

        using var cleared = await _user.PatchAsync($"/api/games/{id}/rating", new { rating = (int?)null });
        Assert.That(cleared.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = await AuthClient.JsonAsync(cleared);
        Assert.That((after.GetProperty("rating").ValueKind, after.GetProperty("favourite").GetBoolean()), Is.EqualTo((JsonValueKind.Null, true)));
    }

    [Test]
    public async Task Rating_UnknownIdIs404_BadBodyIs400_JobRunningIs409()
    {
        var id = await Tetris();

        using (var missing = await _user.PatchAsync($"/api/games/{Guid.NewGuid()}/rating", new { rating = 3 }))
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var bad = await _user.PatchAsync($"/api/games/{id}/rating", new { rating = 9 }))
        {
            Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await AuthClient.CodeAsync(bad), Is.EqualTo("ValidationFailed"));
        }
        using (Service<RnRomManager.Core.Jobs.JobLock>().TryAcquire())
        using (var busy = await _user.PatchAsync($"/api/games/{id}/rating", new { rating = 3 }))
        {
            Assert.That(busy.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(busy), Is.EqualTo("JobRunning"));
        }
        Assert.That((await Service<IGameRepository>().GetAsync(id, default))!.Rating, Is.Null);
    }

    [Test]
    public async Task AdminPatch_IgnoresRatingAndFavourite()
    {
        var id = await Tetris();
        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();

        using var response = await admin.PatchAsync($"/api/games/{id}", new { description = "x", rating = 5, favourite = true });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var row = (await Service<IGameRepository>().GetAsync(id, default))!;
        Assert.That((row.Description, row.Rating, row.Favourite), Is.EqualTo(("x", (int?)null, false)));
    }

    [Test]
    public async Task GameDetail_UnknownId_Is404NotFound()
    {
        using var response = await _user.GetAsync($"/api/games/{Guid.NewGuid()}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task Image_IsPngWithEtagAndPrivateCache_IfNoneMatchIs304()
    {
        var id = await Tetris();
        var url = (await GetJson($"/api/games/{id}")).GetProperty("images")[0].GetProperty("url").GetString()!;
        var sha1 = (await Service<IImageRepository>().ListByGameAsync(id, default)).Single().Sha1;

        using var response = await _user.GetAsync(url);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
        Assert.That(response.Headers.ETag?.Tag, Is.EqualTo($"\"{sha1}\""));
        Assert.That((response.Headers.CacheControl?.Private, response.Headers.CacheControl?.MaxAge), Is.EqualTo((true, (TimeSpan?)TimeSpan.FromDays(1))));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Service<ISystemRegistry>().ById("gb")!.DefaultBoxArtPng.ToArray()));

        using var notModified = await _user.SendAsync(HttpMethod.Get, url, null, sendXsrf: false,
            headers => headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{sha1}\"")));
        Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
    }

    [Test]
    public async Task ZipDownload_HasRfc5987FileName_AndRangeSupport()
    {
        var id = await Ingest("p.gb", Pinball);
        var bytes = await File.ReadAllBytesAsync(await ZipPath(id));

        using var response = await _user.GetAsync($"/api/games/{id}/download");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        Assert.That(response.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
        Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar, Is.EqualTo("Pokémon Pinball.zip"));
        Assert.That(response.Headers.AcceptRanges, Does.Contain("bytes"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));

        using var partial = await _user.SendAsync(HttpMethod.Get, $"/api/games/{id}/download", null, sendXsrf: false,
            headers => headers.Range = new RangeHeaderValue(10, 19));
        Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
        Assert.That(await partial.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes[10..20]));
    }

    [Test]
    public async Task RomDownload_StreamsEntryBytes_WithFileName()
    {
        var id = await Ingest("p.gb", Pinball);
        var romUrl = (await GetJson($"/api/games/{id}")).GetProperty("roms")[0].GetProperty("downloadUrl").GetString()!;

        using var response = await _user.GetAsync(romUrl);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar, Is.EqualTo("Pokémon Pinball (Europe).gb"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Pinball));
    }

    [Test]
    public async Task StaleIds_AndMissingZip_Are404()
    {
        var tetris = await Tetris();
        var pinball = await Ingest("p.gb", Pinball);
        var detail = await GetJson($"/api/games/{tetris}");
        var romId = detail.GetProperty("roms")[0].GetProperty("id").GetInt64();
        var imageId = detail.GetProperty("images")[0].GetProperty("id").GetInt64();

        foreach (var url in new[] { $"/api/games/{pinball}/roms/{romId}/download", $"/api/games/{pinball}/images/{imageId}", $"/api/games/{tetris}/images/{imageId + 1000}" })
        {
            using var response = await _user.GetAsync(url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), url);
        }

        File.Delete(await ZipPath(tetris));
        foreach (var url in new[] { $"/api/games/{tetris}/download", $"/api/games/{tetris}/roms/{romId}/download", $"/api/games/{tetris}/images/{imageId}" })
        {
            using var response = await _user.GetAsync(url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), url);
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("NotFound"));
        }
    }

    [Test]
    public async Task InfoJson_IsTheStoredEntry_AsApplicationJson()
    {
        var id = await Tetris();
        byte[] stored;
        using (var zip = ZipFile.OpenRead(await ZipPath(id)))
        await using (var entry = zip.GetEntry(LibraryZip.InfoJsonEntry)!.Open())
        using (var copy = new MemoryStream())
        {
            await entry.CopyToAsync(copy);
            stored = copy.ToArray();
        }

        using var response = await _user.GetAsync($"/api/games/{id}/info-json");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(stored));
    }

    [Test]
    public async Task InfoJson_UnknownGameMissingZipOrEntry_Is404()
    {
        using (var unknown = await _user.GetAsync($"/api/games/{Guid.NewGuid()}/info-json"))
            Assert.That((unknown.StatusCode, await AuthClient.CodeAsync(unknown)), Is.EqualTo((HttpStatusCode.NotFound, "NotFound")));

        var id = await Tetris();
        using (var zip = ZipFile.Open(await ZipPath(id), ZipArchiveMode.Update))
            zip.GetEntry(LibraryZip.InfoJsonEntry)!.Delete();
        using (var noEntry = await _user.GetAsync($"/api/games/{id}/info-json"))
            Assert.That((noEntry.StatusCode, await AuthClient.CodeAsync(noEntry)), Is.EqualTo((HttpStatusCode.NotFound, "NotFound")));

        File.Delete(await ZipPath(id));
        using var noZip = await _user.GetAsync($"/api/games/{id}/info-json");
        Assert.That((noZip.StatusCode, await AuthClient.CodeAsync(noZip)), Is.EqualTo((HttpStatusCode.NotFound, "NotFound")));
    }

    [Test]
    public async Task InfoJson_CorruptZip_Is500IoError()
    {
        var id = await Tetris();
        await File.WriteAllBytesAsync(await ZipPath(id), [1, 2, 3, 4]);

        using var response = await _user.GetAsync($"/api/games/{id}/info-json");

        Assert.That((response.StatusCode, await AuthClient.CodeAsync(response)), Is.EqualTo((HttpStatusCode.InternalServerError, "IoError")));
    }

    [Test]
    public async Task InfoJson_OverMaxBytes_Is500IoError_NotATruncated200()
    {
        var id = await Tetris();
        using (var zip = ZipFile.Open(await ZipPath(id), ZipArchiveMode.Update))
        {
            zip.GetEntry(LibraryZip.InfoJsonEntry)!.Delete();
            await using var entry = zip.CreateEntry(LibraryZip.InfoJsonEntry).Open();
            await entry.WriteAsync(new byte[InfoJsonReader.MaxBytes + 1]);
        }

        using var response = await _user.GetAsync($"/api/games/{id}/info-json");

        Assert.That((response.StatusCode, await AuthClient.CodeAsync(response)), Is.EqualTo((HttpStatusCode.InternalServerError, "IoError")));
    }

    [Test]
    public async Task Games_MatchedOn_NullForTitleMatches_SetForMetadataMatches_AbsentWithoutQ()
    {
        await Tetris();
        await Ingest("p.gb", Pinball);

        async Task<Dictionary<string, JsonElement>> MatchedOn(string query) =>
            (await GetJson("/api/games?" + query)).GetProperty("items").EnumerateArray()
                .ToDictionary(i => i.GetProperty("displayTitle").GetString()!, i => i.GetProperty("matchedOn"));

        var nintendo = await MatchedOn("q=nintendo");
        Assert.That(nintendo.Keys, Is.EquivalentTo(new[] { "Tetris", "Pokémon Pinball" }));
        Assert.That((nintendo["Tetris"].GetProperty("field").GetString(), nintendo["Tetris"].GetProperty("value").GetString()),
            Is.EqualTo(("developer", "Nintendo")));
        Assert.That(nintendo["Pokémon Pinball"].GetProperty("field").GetString(), Is.EqualTo("publisher"));
        Assert.That((await MatchedOn("q=tetris"))["Tetris"].ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That((await MatchedOn("")).Values.Select(v => v.ValueKind), Has.All.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task Images_CoverDrivesBoxArtUrl_AndJpegIsServedAsJpeg()
    {
        var id = await Tetris();
        var reader = Service<InfoJsonReader>();
        var path = await ZipPath(id);
        var game = LibraryZip.Open(path, reader);
        var info = game.InfoJson!.Game!;
        // A minimal baseline JPEG (SOI, SOF0 64x32, SOS); content isn't decoded, only served.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 32, 0, 64, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];
        var snap = info.Images[0] with
        {
            FileName = "snap-01.jpg", Type = ImageType.Snap, Source = InfoJsonImage.SourceUpload, IsDefault = false, IsCover = true,
            SizeBytes = jpeg.Length, Sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(jpeg)), Width = 64, Height = 32,
        };
        var updated = info with { Images = [.. info.Images.Select(i => i with { IsCover = false }), snap] };
        await new LibraryZipWriter(reader).WriteAsync(path, game, new LibraryZipChanges(updated,
            new Dictionary<string, Func<Stream>> { [LibraryZip.ImageEntry(snap.FileName)] = () => new MemoryStream(jpeg) }));
        var file = new FileInfo(path);
        var row = (await Service<IGameRepository>().GetAsync(id, default))!;
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(updated, row.ZipPath, file.Length, file.LastWriteTimeUtc), default);

        var detail = await GetJson($"/api/games/{id}");
        var cover = detail.GetProperty("images").EnumerateArray().Single(i => i.GetProperty("isCover").GetBoolean());
        Assert.That((cover.GetProperty("fileName").GetString(), cover.GetProperty("source").GetString(), cover.GetProperty("type").GetString()),
            Is.EqualTo(("snap-01.jpg", "upload", "Snap")));
        Assert.That(detail.GetProperty("hasDefaultBoxArt").GetBoolean(), Is.False, "the cover isn't the placeholder");
        var item = (await GetJson("/api/games")).GetProperty("items").EnumerateArray().Single();
        Assert.That(item.GetProperty("boxArtUrl").GetString(), Is.EqualTo(cover.GetProperty("url").GetString()));

        using var image = await _user.GetAsync(cover.GetProperty("url").GetString()!);
        Assert.That(image.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(image.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
        Assert.That(await image.Content.ReadAsByteArrayAsync(), Is.EqualTo(jpeg));
        using var png = await _user.GetAsync(detail.GetProperty("images")[0].GetProperty("url").GetString()!);
        Assert.That(png.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
    }
}
