using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B7–B12: the save routes, as a plain signed-in user (no roles), against MariaDB and real ZIPs.</summary>
[Category("Integration")]
public partial class SaveEndpointsTests
{
    private const string StorageMigrationTitle =
        "This game's ROMs are being moved by the storage job. Try again when it has finished.";
    private const string UnreadableTitle = "This game's saves folder can't be read. An Admin must repair it.";
    private const string IndexFullTitle = "This game has too many saves. Delete some first.";
    private const string LimitTitle = "You already have 25 saves for this release. Delete one first.";

    // The signed-in Site user, so the saves the tests add are theirs (S3).
    private static readonly AuditActor Plain = new(null, "plain", null);

    private ApiFactory _factory = null!;
    private AuthClient _user = null!;
    private int _seed;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => SystemHandlerRegistration.AddSystemHandler<LegacyExternalTestHandler>(services),
        };
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
    }

    [TearDown]
    public void TearDown() => _user.Dispose();

    private sealed record TestGame(Guid Id, string Zip);

    private async Task<TestGame> WriteGame(string title, params string[] fileNames)
    {
        var seeds = fileNames.Select(_ => ++_seed).ToArray();
        var game = TestLibrary.Game(title, [.. fileNames.Select((f, i) => TestLibrary.Rom(f, TestLibrary.RomBytes(seeds[i]), isPrimary: i == 0))]);
        var zipPath = $"gb/T/Game{_seed}.zip";
        var zip = Path.GetFullPath(Path.Combine(_factory.Root, "roms", zipPath));
        await TestLibrary.WriteZipAsync(Service<InfoJsonReader>(), zip, game, seeds);
        var file = new FileInfo(zip);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, zipPath, file.Length, file.LastWriteTimeUtc), default);
        return new(game.GameId, zip);
    }

    private async Task<long> RomId(Guid gameId, string fileName) =>
        (await Service<IRomRepository>().ListByGameAsync(gameId, default)).Single(r => r.FileName == fileName).Id;

    private async Task<GameSave> AddSave(TestGame game, string fileName, string name, byte[] bytes, string extension = ".state")
    {
        var path = Path.Combine(_factory.Root, $"upload-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(path, bytes);
        var result = await Service<LibraryEditor>().AddSaveAsync(game.Id, await RomId(game.Id, fileName), "state", name, null,
            new SaveUpload(path, extension, bytes.Length, Convert.ToHexStringLower(SHA1.HashData(bytes))), Plain, default);
        Assert.That(result.Code, Is.Null, result.Details);
        return result.Save!;
    }

    private static void ReplaceEntry(string zip, string name, byte[] bytes)
    {
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
        archive.GetEntry(name)?.Delete();
        using var entry = archive.CreateEntry(name).Open();
        entry.Write(bytes);
    }

    private static string[] SavesEntries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName).Where(n => n.StartsWith(LibraryZip.SavesFolder, StringComparison.Ordinal))];
    }

    private async Task<JsonElement[]> List(Guid gameId)
    {
        using var response = await _user.GetAsync($"/api/games/{gameId}/saves");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return [.. (await AuthClient.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static async Task AssertConflict(HttpResponseMessage response, string code, string title)
    {
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), await response.Content.ReadAsStringAsync());
        var body = await AuthClient.JsonAsync(response);
        Assert.That((body.GetProperty("code").GetString(), body.GetProperty("title").GetString()), Is.EqualTo((code, title)));
    }

    [Test]
    public async Task List_EmptyWithoutSaves_NewestFirst_AndAnOrphanHasNoRomId()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb", "Tetris (Japan).gb");
        Assert.That(await List(game.Id), Is.Empty);
        var old = await AddSave(game, "Tetris (World).gb", "Old", [1]);
        var orphan = await AddSave(game, "Tetris (Japan).gb", "Orphan", [2, 3]);
        Assert.That((await Service<LibraryEditor>().DeleteReleaseAsync(game.Id, await RomId(game.Id, "Tetris (Japan).gb"), Plain, default)).Code, Is.Null);
        var newest = await AddSave(game, "Tetris (World).gb", "Newest", [4]);

        var items = await List(game.Id);

        Assert.That(items.Select(i => i.GetProperty("id").GetString()), Is.EqualTo(new[] { newest.Id, orphan.Id, old.Id }));
        var first = items[0];
        Assert.That((first.GetProperty("name").GetString(), first.GetProperty("kind").GetString(), first.GetProperty("releaseFileName").GetString(),
                first.GetProperty("romId").GetInt64(), first.GetProperty("sizeBytes").GetInt64(), first.GetProperty("extension").GetString(),
                first.GetProperty("uploadedBy").GetString(), first.GetProperty("updatedBy").GetString()),
            Is.EqualTo(("Newest", "state", "Tetris (World).gb", await RomId(game.Id, "Tetris (World).gb"), 1L, ".state", "plain", "plain")));
        Assert.That(items[1].GetProperty("romId").ValueKind, Is.EqualTo(JsonValueKind.Null), "the release is gone");
        Assert.That(items[1].GetProperty("releaseFileName").GetString(), Is.EqualTo("Tetris (Japan).gb"));
    }

    [Test]
    public async Task Patch_AsAPlainUser_RenamesAndRedescribes_AndANoOpWritesNothing()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var save = await AddSave(game, "Tetris (World).gb", "Before", [1]);

        using var patched = await _user.PatchAsync($"/api/games/{game.Id}/saves/{save.Id}", new { name = " After ", description = "x\ny" });
        Assert.That(patched.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var item = await AuthClient.JsonAsync(patched);
        Assert.That((item.GetProperty("name").GetString(), item.GetProperty("description").GetString(), item.GetProperty("updatedBy").GetString()),
            Is.EqualTo(("After", "x\ny", "plain")));

        var before = File.ReadAllBytes(game.Zip);
        using var same = await _user.PatchAsync($"/api/games/{game.Id}/saves/{save.Id}", new { name = "After" });
        Assert.That(same.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(File.ReadAllBytes(game.Zip), Is.EqualTo(before));
    }

    [TestCase("{}", "name")]
    [TestCase("""{"name":""}""", "name")]
    [TestCase("""{"name":5}""", "name")]
    [TestCase("""{"description":"a\tb"}""", "description")]
    public async Task Patch_InvalidBody_Is400_EvenForAnUnknownGame(string json, string field)
    {
        using var response = await _user.SendContentAsync(HttpMethod.Patch, $"/api/games/{Guid.NewGuid()}/saves/{GameSaves.NewId()}",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }

    [Test]
    public async Task Patch_ErrorOrder_MigrationThenJobThenUnreadableThenSave()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var legacyBytes = TestLibrary.RomBytes(++_seed);
        var legacy = TestLibrary.Game("Old", TestLibrary.Rom("Old (USA).nds", legacyBytes)) with { System = LegacyExternalTestHandler.SystemId };
        var legacyZip = Path.Combine(_factory.Root, "roms", "legacy", "O", "Old.zip");
        LegacyZipWriter.Write(legacyZip, legacy, new Dictionary<string, byte[]> { [LibraryZip.SavesIndexEntry] = "{ no"u8.ToArray() }, legacyBytes);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(legacy, "legacy/O/Old.zip", new FileInfo(legacyZip).Length, DateTime.UtcNow), default);
        ReplaceEntry(game.Zip, LibraryZip.SavesIndexEntry, "{ no"u8.ToArray());
        var unknown = GameSaves.NewId();
        var body = new { name = "X" };

        using (var noGame = await _user.PatchAsync($"/api/games/{Guid.NewGuid()}/saves/{unknown}", body))
            Assert.That(noGame.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var held = Service<JobLock>().TryAcquire())
        {
            using (var migrating = await _user.PatchAsync($"/api/games/{legacy.GameId}/saves/{unknown}", body))
                await AssertConflict(migrating, "StorageMigrationPending", StorageMigrationTitle);
            using var busy = await _user.PatchAsync($"/api/games/{game.Id}/saves/{unknown}", body);
            Assert.That((busy.StatusCode, await AuthClient.CodeAsync(busy)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
        }
        using var unreadable = await _user.PatchAsync($"/api/games/{game.Id}/saves/{unknown}", body);
        await AssertConflict(unreadable, "SavesUnreadable", UnreadableTitle);
    }

    [Test]
    public async Task Patch_ThatWouldGrowTheIndexOverOneMiB_IsSavesIndexFull()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var save = await AddSave(game, "Tetris (World).gb", "S", [1]);
        // Small records (well under 1,000 bytes each) fill the index until describing the save would pass 1 MiB.
        var described = save with { Description = new string('d', GameSaves.MaxDescriptionLength) };
        var filler = new List<GameSave> { save };
        while (GameSaves.Serialize([described, .. filler.Skip(1)]) is not null)
            filler.Add(save with { Id = GameSaves.NewId(), Release = "Gone" });
        Assert.That(GameSaves.Serialize(filler), Is.Not.Null, "test setup: the index fits until the description grows");
        using (var archive = ZipFile.Open(game.Zip, ZipArchiveMode.Update))
        {
            archive.GetEntry(LibraryZip.SavesIndexEntry)!.Delete();
            using (var index = archive.CreateEntry(LibraryZip.SavesIndexEntry).Open())
                index.Write(GameSaves.Serialize(filler)!);
            foreach (var extra in filler.Skip(1))
            {
                using var entry = archive.CreateEntry(extra.Entry).Open();
                entry.Write([1]);
            }
        }

        using var response = await _user.PatchAsync($"/api/games/{game.Id}/saves/{save.Id}",
            new { description = new string('d', GameSaves.MaxDescriptionLength) });

        await AssertConflict(response, "SavesIndexFull", IndexFullTitle);
    }

    [Test]
    public async Task Delete_RemovesTheSave_AndTheLastOneTakesSavesJsonWithIt()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var first = await AddSave(game, "Tetris (World).gb", "A", [1]);
        var second = await AddSave(game, "Tetris (World).gb", "B", [2]);

        using (var one = await _user.SendAsync(HttpMethod.Delete, $"/api/games/{game.Id}/saves/{first.Id}", null, sendXsrf: true))
            Assert.That(one.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(SavesEntries(game.Zip).Order(), Is.EqualTo(new[] { second.Entry, LibraryZip.SavesIndexEntry }.Order()));
        using (var last = await _user.SendAsync(HttpMethod.Delete, $"/api/games/{game.Id}/saves/{second.Id}", null, sendXsrf: true))
            Assert.That(last.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(SavesEntries(game.Zip), Is.Empty);

        using var again = await _user.SendAsync(HttpMethod.Delete, $"/api/games/{game.Id}/saves/{second.Id}", null, sendXsrf: true);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Download_StreamsTheBytes_NamedAfterTheGameAndSave_WithUnsafeCharactersReplaced()
    {
        var game = await WriteGame("Pokémon: Gold?", "Gold (World).gb");
        var save = await AddSave(game, "Gold (World).gb", "Gym 8 / Elite", [5, 6, 7], ".srm");

        using var response = await _user.SendAsync(HttpMethod.Get, $"/api/games/{game.Id}/saves/{save.Id}/download", null,
            sendXsrf: false, completion: HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.That(disposition.FileNameStar, Is.EqualTo("Pokémon_ Gold_ - Gym 8 _ Elite.srm"));
        Assert.That(disposition.FileName, Is.Not.Null, "an ASCII fallback too");
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(3));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 5, 6, 7 }));
    }

    [Test]
    public async Task Download_And_List_Are404_ForAnUnknownGameSaveOrMissingEntry_And409WhenUnreadable()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var save = await AddSave(game, "Tetris (World).gb", "S", [1]);

        foreach (var url in new[] { $"/api/games/{Guid.NewGuid()}/saves", $"/api/games/{Guid.NewGuid()}/saves/{save.Id}/download",
                     $"/api/games/{game.Id}/saves/{GameSaves.NewId()}/download" })
        {
            using var response = await _user.GetAsync(url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), url);
        }

        ReplaceEntry(game.Zip, LibraryZip.SavesIndexEntry, "{ no"u8.ToArray());
        using var list = await _user.GetAsync($"/api/games/{game.Id}/saves");
        await AssertConflict(list, "SavesUnreadable", UnreadableTitle);
        using var download = await _user.GetAsync($"/api/games/{game.Id}/saves/{save.Id}/download");
        await AssertConflict(download, "SavesUnreadable", UnreadableTitle);
    }

    [Test]
    public async Task SignedOut_Is401_AndAWriteWithoutTheAntiforgeryToken_IsRejected()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var save = await AddSave(game, "Tetris (World).gb", "S", [1]);
        using var anonymous = await AuthClient.CreateAsync(_factory);

        using (var list = await anonymous.GetAsync($"/api/games/{game.Id}/saves"))
            Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var patch = await _user.SendAsync(HttpMethod.Patch, $"/api/games/{game.Id}/saves/{save.Id}", new { name = "X" }, sendXsrf: false);
        using var delete = await _user.SendAsync(HttpMethod.Delete, $"/api/games/{game.Id}/saves/{save.Id}", null, sendXsrf: false);

        Assert.That((patch.StatusCode, delete.StatusCode), Is.EqualTo((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest)));
        Assert.That(SavesEntries(game.Zip), Has.Length.EqualTo(2), "nothing changed");
    }

    // --- User Management S2/S3: private saves ---

    private async Task<GameSave> AddSaveAs(TestGame game, string username, string name)
    {
        var path = Path.Combine(_factory.Root, $"upload-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(path, [5]);
        var result = await Service<LibraryEditor>().AddSaveAsync(game.Id, await RomId(game.Id, "Tetris (World).gb"), "state", name, null,
            new SaveUpload(path, ".state", 1, Convert.ToHexStringLower(SHA1.HashData(new byte[] { 5 }))), new AuditActor(null, username, null), default);
        Assert.That(result.Code, Is.Null, result.Details);
        return result.Save!;
    }

    [Test]
    public async Task List_ASiteUserSeesOnlyTheirOwn_AnAdminSeesAll()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var mine = await AddSaveAs(game, "PLAIN", "Mine");
        var bobs = await AddSaveAs(game, "bob", "Bob's");
        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();

        Assert.That((await List(game.Id)).Select(i => i.GetProperty("id").GetString()), Is.EqualTo(new[] { mine.Id }));
        using var all = await admin.GetAsync($"/api/games/{game.Id}/saves");
        Assert.That((await AuthClient.JsonAsync(all)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()),
            Is.EquivalentTo(new[] { mine.Id, bobs.Id }));
    }

    [Test]
    public async Task AnotherUsersSave_Is404ForASiteUser_OnEveryRoute_AndUntouched()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var bobs = await AddSaveAs(game, "bob", "Bob's");
        var before = File.ReadAllBytes(game.Zip);
        var url = $"/api/games/{game.Id}/saves/{bobs.Id}";

        using var download = await _user.GetAsync(url + "/download");
        using var patch = await _user.PatchAsync(url, new { name = "Mine now" });
        using var delete = await _user.SendAsync(HttpMethod.Delete, url, null, sendXsrf: true);
        var file = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "x.state" } };
        using var replace = await _user.SendContentAsync(HttpMethod.Put, url + "/file", file);

        foreach (var response in new[] { download, patch, delete, replace })
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), response.RequestMessage!.Method.ToString());
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("NotFound"));
        }
        Assert.That(File.ReadAllBytes(game.Zip), Is.EqualTo(before));
    }

    [Test]
    public async Task AnAdmin_DownloadsEditsAndDeletesAnotherUsersSave()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var bobs = await AddSaveAs(game, "bob", "Bob's");
        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        var url = $"/api/games/{game.Id}/saves/{bobs.Id}";

        using var download = await admin.GetAsync(url + "/download");
        using var patch = await admin.PatchAsync(url, new { name = "Checked" });
        var patched = await AuthClient.JsonAsync(patch);
        using var delete = await admin.SendAsync(HttpMethod.Delete, url, null, sendXsrf: true);

        Assert.That((download.StatusCode, patch.StatusCode, delete.StatusCode), Is.EqualTo((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NoContent)));
        Assert.That((patched.GetProperty("uploadedBy").GetString(), patched.GetProperty("updatedBy").GetString()), Is.EqualTo(("bob", "boss")));
    }
}
