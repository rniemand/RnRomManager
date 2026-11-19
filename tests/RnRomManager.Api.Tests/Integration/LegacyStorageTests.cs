using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>S3: every write to a legacy game (ROMs still inside the ZIP of a <c>LegacyEmbeddedRoms</c> system) is refused.</summary>
[Category("Integration")]
public class LegacyStorageTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
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
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown() => _admin.Dispose();

    public sealed record Legacy(Guid Id, long RomId, long ImageId, string ZipPath, byte[] Bytes);

    /// <summary>A mirrored legacy game with one release, a note and a cover image, all inside its ZIP.</summary>
    private async Task<Legacy> WriteLegacy(string title)
    {
        var rom = TestLibrary.RomBytes(++_seed);
        var image = new InfoJsonImage
        {
            FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = InfoJsonImage.SourceUpload, IsDefault = false, IsCover = true,
            SizeBytes = Png.Length, Sha1 = Convert.ToHexStringLower(SHA1.HashData(Png)), Width = 1, Height = 1, DateAdded = TestLibrary.When,
        };
        var game = TestLibrary.Game(title, TestLibrary.Rom($"{title} (USA).nds", rom)) with
        {
            System = LegacyExternalTestHandler.SystemId, Images = [image],
        };
        var relative = $"legacy/{title[..1]}/{title}.zip";
        var zip = Path.GetFullPath(Path.Combine(_factory.Root, "roms", relative));
        LegacyZipWriter.Write(zip, game, new Dictionary<string, byte[]>
        {
            [LibraryZip.ImageEntry(image.FileName)] = Png,
            [LibraryZip.NoteEntry($"{title} (USA).nds")] = "note"u8.ToArray(),
        }, rom);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, relative, new FileInfo(zip).Length, DateTime.UtcNow), default);
        using var detail = await _admin.GetAsync($"/api/games/{game.GameId}");
        var json = await AuthClient.JsonAsync(detail);
        return new(game.GameId, json.GetProperty("roms")[0].GetProperty("id").GetInt64(), json.GetProperty("images")[0].GetProperty("id").GetInt64(),
            zip, File.ReadAllBytes(zip));
    }

    /// <summary>A mirrored, already migrated game of the same system (its ZIP is never opened by a refused write).</summary>
    private async Task<Guid> MirrorMigrated(string title)
    {
        var rom = TestLibrary.Rom($"{title} (USA).nds", TestLibrary.RomBytes(++_seed)) with { ExternalFileName = $"{title}.{title} (USA).nds" };
        var game = TestLibrary.Game(title, rom) with { System = LegacyExternalTestHandler.SystemId };
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, $"legacy/{title[..1]}/{title}.zip", 100, DateTime.UtcNow), default);
        return game.GameId;
    }

    private static MultipartFormDataContent ImageForm()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "x.png");
        form.Add(new StringContent("Snap"), "type");
        return form;
    }

    private static async Task AssertRefused(HttpResponseMessage response)
    {
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), await response.Content.ReadAsStringAsync());
        var body = await AuthClient.JsonAsync(response);
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("StorageMigrationPending"));
        Assert.That(body.GetProperty("title").GetString(),
            Is.EqualTo("This game's ROMs are being moved by the storage job. Try again when it has finished."));
    }

    public static IEnumerable<TestCaseData> Writes()
    {
        static TestCaseData Case(string name, Func<AuthClient, Legacy, Guid, Task<HttpResponseMessage>> send) =>
            new TestCaseData(send).SetName("LegacyWriteRefused " + name);

        yield return Case("patch", (c, g, _) => c.PatchAsync($"/api/games/{g.Id}", new { title = "Renamed" }));
        yield return Case("rating", (c, g, _) => c.PatchAsync($"/api/games/{g.Id}/rating", new { rating = 4 }));
        yield return Case("release delete", (c, g, _) => c.SendAsync(HttpMethod.Delete, $"/api/games/{g.Id}/roms/{g.RomId}", null, true));
        yield return Case("game delete", (c, g, _) => c.SendAsync(HttpMethod.Delete, $"/api/games/{g.Id}", null, true));
        yield return Case("move as source", (c, g, other) => c.PostAsync($"/api/games/{g.Id}/roms/{g.RomId}/move", new { targetGameId = other }));
        yield return Case("move to a new game", (c, g, _) => c.PostAsync($"/api/games/{g.Id}/roms/{g.RomId}/move", new { newGameTitle = "Split" }));
        yield return Case("merge as source", (c, g, other) => c.PostAsync($"/api/games/{g.Id}/merge", new { targetGameId = other }));
        yield return Case("image add", (c, g, _) => c.SendContentAsync(HttpMethod.Post, $"/api/games/{g.Id}/images", ImageForm()));
        yield return Case("image delete", (c, g, _) => c.SendAsync(HttpMethod.Delete, $"/api/games/{g.Id}/images/{g.ImageId}", null, true));
        yield return Case("set cover", (c, g, _) => c.PostAsync($"/api/games/{g.Id}/images/{g.ImageId}/cover"));
        yield return Case("media retry", (c, g, _) => c.PostAsync($"/api/games/{g.Id}/media/retry"));
        yield return Case("note save", (c, g, _) => c.SendAsync(HttpMethod.Put, $"/api/games/{g.Id}/roms/{g.RomId}/note", new { text = "new" }, true));
        yield return Case("note delete", (c, g, _) => c.SendAsync(HttpMethod.Delete, $"/api/games/{g.Id}/roms/{g.RomId}/note", null, true));
    }

    [TestCaseSource(nameof(Writes))]
    public async Task LegacyGame_EveryWrite_IsStorageMigrationPending_AndLeavesTheZip(Func<AuthClient, Legacy, Guid, Task<HttpResponseMessage>> send)
    {
        var game = await WriteLegacy("Alpha");
        var other = await MirrorMigrated("Beta");

        using var response = await send(_admin, game, other);

        await AssertRefused(response);
        Assert.That(File.ReadAllBytes(game.ZipPath), Is.EqualTo(game.Bytes));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(game.ZipPath)!), Is.EqualTo(new[] { game.ZipPath }));
    }

    /// <summary>ROM Zip Config B10: with the system set to embed, a game still beside its ZIP is pending and refused.</summary>
    [Test]
    public async Task ExternalGame_UnderEmbeddedSetting_IsStorageMigrationPending()
    {
        var bytes = TestLibrary.RomBytes(++_seed);
        var rom = TestLibrary.Rom("Gamma (USA).nds", bytes) with { ExternalFileName = "Gamma.Gamma (USA).nds" };
        var game = TestLibrary.Game("Gamma", rom) with { System = LegacyExternalTestHandler.SystemId };
        var zip = Path.GetFullPath(Path.Combine(_factory.Root, "roms", "legacy/G/Gamma.zip"));
        await TestLibrary.WriteZipAsync(Service<InfoJsonReader>(), zip, game);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(zip)!, "Gamma.Gamma (USA).nds"), bytes);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, "legacy/G/Gamma.zip", new FileInfo(zip).Length, DateTime.UtcNow), default);
        var before = File.ReadAllBytes(zip);
        await Service<IRomStorageSettings>().SetAsync(LegacyExternalTestHandler.SystemId, true, default);

        using var response = await _admin.PatchAsync($"/api/games/{game.GameId}", new { title = "Renamed" });

        await AssertRefused(response);
        Assert.That(File.ReadAllBytes(zip), Is.EqualTo(before));
    }

    [Test]
    public async Task LegacyGame_AsMoveOrMergeTarget_IsStorageMigrationPending()
    {
        var game = await WriteLegacy("Alpha");
        var source = await MirrorMigrated("Beta");
        using var detail = await _admin.GetAsync($"/api/games/{source}");
        var romId = (await AuthClient.JsonAsync(detail)).GetProperty("roms")[0].GetProperty("id").GetInt64();

        using var move = await _admin.PostAsync($"/api/games/{source}/roms/{romId}/move", new { targetGameId = game.Id });
        using var merge = await _admin.PostAsync($"/api/games/{source}/merge", new { targetGameId = game.Id });

        await AssertRefused(move);
        await AssertRefused(merge);
        Assert.That(File.ReadAllBytes(game.ZipPath), Is.EqualTo(game.Bytes));
    }

    [Test]
    public async Task LegacyGame_BatchMediaRetry_IsSkipped_WithNoWrite()
    {
        var game = await WriteLegacy("Alpha");
        var row = (await Service<IGameRepository>().GetAsync(game.Id, default))!;

        var result = await Service<GameMediaRetry>().RetryAsync(row, new CircuitBreaker(), skipManualOnly: true,
            new AuditActor(null, null, Guid.NewGuid()), default);

        Assert.That(result.Outcome, Is.EqualTo(MediaRetryOutcome.Skipped));
        Assert.That(File.ReadAllBytes(game.ZipPath), Is.EqualTo(game.Bytes));
    }
}
