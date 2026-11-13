using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>I5–I7: <c>POST /api/games/{id}/images</c>, <c>DELETE …/images/{imageId}</c>, <c>POST …/images/{imageId}/cover</c>.</summary>
[Category("Integration")]
public class ImageEndpointsTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] Tetris = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();

    private ApiFactory _factory = null!;
    private AuthClient _manager = null!;
    private Guid _game;
    private bool _lockBeforeGameCheck;
    private IDisposable? _held;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            // B14: the factory runs after the editor released the lock; grabbing it here plays another job starting first.
            TestServices = s => s.AddSingleton<IntegrityJobFactory>(sp => (mode, zipPath) =>
            {
                if (_lockBeforeGameCheck && mode == JobMode.Game)
                    _held = sp.GetRequiredService<JobLock>().TryAcquire();
                var job = ActivatorUtilities.CreateInstance<IntegrityJob>(sp, mode);
                job.ZipPath = zipPath;
                return job;
            }),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        _lockBeforeGameCheck = false;
        await _factory.ResetAsync();
        _manager = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "manager", Roles.Of(Roles.RomManager));
        (await _manager.LoginAsync("manager", TestUsers.Password)).Dispose();
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
            [Gb] = [new FixtureGame("Tetris (World)", "Tetris (World).gb", Tetris)],
        }, titleDb: IngestSeeder.EmptySwitch, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
        var path = Path.Combine(_factory.Root, "ingest", "gb", "t.gb");
        await File.WriteAllBytesAsync(path, Tetris);
        var file = new IngestFile("gb", path, "t.gb", Tetris.Length, DateTime.UtcNow.AddMinutes(-5));
        _game = (await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default))!.GameId!.Value;
    }

    [TearDown]
    public void TearDown()
    {
        _held?.Dispose();
        _manager.Dispose();
    }

    private static byte[] Png(uint width, uint height)
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 0, 0, 0, 0, 0,
            8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 32, 0, 64, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];

    private static MultipartFormDataContent Form(byte[]? file, string? type, string fileName = "anything.bin")
    {
        var form = new MultipartFormDataContent();
        if (file is not null)
        {
            var content = new ByteArrayContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", fileName);
        }
        if (type is not null)
            form.Add(new StringContent(type), "type");
        return form;
    }

    private Task<HttpResponseMessage> Upload(AuthClient client, byte[]? file, string? type, bool sendXsrf = true) =>
        client.SendContentAsync(HttpMethod.Post, $"/api/games/{_game}/images", Form(file, type), sendXsrf);

    private static async Task<JsonElement[]> Images(HttpResponseMessage response) =>
        (await AuthClient.JsonAsync(response)).GetProperty("images").EnumerateArray().ToArray();

    [Test]
    public async Task UploadPng_ReplacesThePlaceholder_ReturnsTheDetail()
    {
        using var response = await Upload(_manager, Png(300, 400), "BoxArt");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var image = (await Images(response)).Single();
        Assert.That((image.GetProperty("fileName").GetString(), image.GetProperty("source").GetString(), image.GetProperty("isCover").GetBoolean(),
            image.GetProperty("isDefault").GetBoolean(), image.GetProperty("width").GetInt32()), Is.EqualTo(("boxart-01.png", "upload", true, false, 300)));
    }

    /// <summary>A Deep run (Quick doesn't open an unchanged ZIP), so the placeholder game has its open <c>DefaultBoxArt</c> issue.</summary>
    private async Task<Guid> DeepCheckAsync()
    {
        var started = await Service<JobRunner>().StartAsync(Service<IntegrityJobFactory>()(JobMode.Deep), JobTrigger.Manual, null, default);
        await started!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(await OpenTypesAsync(), Is.EqualTo(new[] { IntegrityIssueType.DefaultBoxArt }));
        return started.Id;
    }

    private async Task<string[]> OpenTypesAsync() =>
        [.. (await Service<IIntegrityIssueRepository>().ListAllOpenAsync(default)).Select(i => i.Type)];

    private async Task<List<(string Status, int? RequestedBy)>> GameChecksAsync()
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return [.. (await db.QueryAsync<(string, int?)>("SELECT status, requested_by FROM jobs WHERE type = 'Integrity' AND mode = 'Game'"))];
    }

    /// <summary>Waits for the background game check the upload started.</summary>
    private async Task<(string Status, int? RequestedBy)> GameCheckDoneAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var checks = await GameChecksAsync();
            if (checks.Count == 1 && checks[0].Status != "Running")
                return checks[0];
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the game check didn't finish");
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task FirstRealBoxArt_StartsOneGameCheck_WhichResolvesDefaultBoxArt_B14()
    {
        var deepId = await DeepCheckAsync();

        using var response = await Upload(_manager, Png(300, 400), "BoxArt");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var (status, requestedBy) = await GameCheckDoneAsync();
        var manager = (await Service<RnRomManager.Core.Users.IUserRepository>().GetByUsernameAsync("manager", default))!;
        Assert.That((status, requestedBy), Is.EqualTo(("Completed", (int?)manager.Id)));
        Assert.That(await OpenTypesAsync(), Is.Empty);
        var deep = await Service<IJobRepository>().GetLatestAsync(JobType.Integrity, JobMode.Deep, default);
        Assert.That(deep!.Id, Is.EqualTo(deepId), "a game check never counts as the last deep check");
    }

    [Test]
    public async Task SnapOrSecondBoxArt_StartsNoGameCheck_B14()
    {
        (await Upload(_manager, Png(300, 400), "BoxArt")).Dispose(); // the first real box art: one check
        await GameCheckDoneAsync();

        using var snap = await Upload(_manager, Jpeg, "Snap");
        using var second = await Upload(_manager, Png(301, 400), "BoxArt");

        Assert.That((snap.StatusCode, second.StatusCode), Is.EqualTo((HttpStatusCode.OK, HttpStatusCode.OK)));
        Assert.That(await GameChecksAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task BusyLock_SkipsTheGameCheck_AndStillReturnsTheDetail_B14()
    {
        await DeepCheckAsync();
        _lockBeforeGameCheck = true;

        using var response = await Upload(_manager, Png(300, 400), "BoxArt");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Images(response)).Single().GetProperty("source").GetString(), Is.EqualTo("upload"));
        Assert.That(_held, Is.Not.Null, "the lock was free, and taken just before the check");
        Assert.That(await GameChecksAsync(), Is.Empty);
        Assert.That(await OpenTypesAsync(), Is.EqualTo(new[] { IntegrityIssueType.DefaultBoxArt }), "cleared by the next Quick or Deep run");
    }

    [Test]
    public async Task UploadJpegSnap_IsStoredAsJpg_AndServedAsJpeg()
    {
        using var response = await Upload(_manager, Jpeg, "Snap");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var snap = (await Images(response)).Single(i => i.GetProperty("type").GetString() == "Snap");
        Assert.That((snap.GetProperty("fileName").GetString(), snap.GetProperty("isCover").GetBoolean()), Is.EqualTo(("snap-01.jpg", false)));
        using var served = await _manager.GetAsync(snap.GetProperty("url").GetString()!);
        Assert.That(served.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
    }

    [Test]
    public async Task Upload_WithoutXsrf_Is400AntiforgeryInvalid()
    {
        using var response = await Upload(_manager, Png(1, 1), "BoxArt", sendXsrf: false);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("AntiforgeryInvalid"));
    }

    [Test]
    public async Task Upload_OverTheRequestLimit_Is413()
    {
        var huge = new byte[ImageFormats.MaxBytes + 128 * 1024];
        Png(1, 1).CopyTo(huge, 0);

        using var response = await Upload(_manager, huge, "BoxArt");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task Upload_JustOver8MiB_IsAFileError()
    {
        var big = new byte[ImageFormats.MaxBytes + 1];
        Png(1, 1).CopyTo(big, 0);

        using var response = await Upload(_manager, big, "BoxArt");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").GetProperty("file")[0].GetString(), Does.Contain("8 MiB"));
    }

    [TestCase("GIF", "BoxArt", "file", TestName = "Wrong magic bytes → file")]
    [TestCase("PNG", "TitleScreen", "type", TestName = "Unsupported type → type")]
    [TestCase("PNG", "boxart", "type", TestName = "Type is case-sensitive → type")]
    [TestCase("PNG", null, "type", TestName = "Missing type → type")]
    [TestCase(null, "Snap", "file", TestName = "Missing file → file")]
    public async Task Upload_Invalid_Is400OnTheField(string? file, string? type, string field)
    {
        var bytes = file switch { "GIF" => "GIF89a......"u8.ToArray(), "PNG" => Png(1, 1), _ => null };

        using var response = await Upload(_manager, bytes, type);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await AuthClient.JsonAsync(response);
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(body.GetProperty("errors").TryGetProperty(field, out _), Is.True, body.ToString());
    }

    [Test]
    public async Task Upload_SameImageTwice_Is409DuplicateImage()
    {
        (await Upload(_manager, Png(2, 2), "Snap")).Dispose();

        using var again = await Upload(_manager, Png(2, 2), "Snap");

        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(again), Is.EqualTo("DuplicateImage"));
    }

    [Test]
    public async Task UserWithoutRomManager_Is403()
    {
        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "admin-only", Roles.Of(Roles.Admin));
        (await admin.LoginAsync("admin-only", TestUsers.Password)).Dispose();

        using var response = await Upload(admin, Png(1, 1), "BoxArt");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task SetCover_ThenDelete_TheCoverFallsBackToTheBoxArt()
    {
        using var uploaded = await Upload(_manager, Png(160, 144), "Snap");
        var snapUrl = (await Images(uploaded)).Single(i => i.GetProperty("type").GetString() == "Snap").GetProperty("url").GetString()!;
        var snapId = snapUrl[(snapUrl.LastIndexOf('/') + 1)..];

        using var cover = await _manager.PostAsync($"/api/games/{_game}/images/{snapId}/cover");
        Assert.That(cover.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var afterCover = await Images(cover);
        Assert.That(afterCover.Single(i => i.GetProperty("isCover").GetBoolean()).GetProperty("type").GetString(), Is.EqualTo("Snap"));

        // Every write re-mirrors the image rows, so ids come from the latest detail.
        var snapUrlAfter = afterCover.Single(i => i.GetProperty("type").GetString() == "Snap").GetProperty("url").GetString()!;
        snapId = snapUrlAfter[(snapUrlAfter.LastIndexOf('/') + 1)..];
        using var deleted = await _manager.SendAsync(HttpMethod.Delete, $"/api/games/{_game}/images/{snapId}", null, true);
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var left = (await Images(deleted)).Single();
        Assert.That((left.GetProperty("type").GetString(), left.GetProperty("isCover").GetBoolean(), left.GetProperty("isDefault").GetBoolean()),
            Is.EqualTo(("BoxArt", true, true)));
    }

    [Test]
    public async Task Delete_ThePlaceholder_Is409DefaultImage_UnknownIs404()
    {
        var detail = await AuthClient.JsonAsync(await _manager.GetAsync($"/api/games/{_game}"));
        var placeholderId = detail.GetProperty("images")[0].GetProperty("id").GetInt64();

        using var placeholder = await _manager.SendAsync(HttpMethod.Delete, $"/api/games/{_game}/images/{placeholderId}", null, true);
        using var unknown = await _manager.SendAsync(HttpMethod.Delete, $"/api/games/{_game}/images/999999", null, true);

        Assert.That(await AuthClient.CodeAsync(placeholder), Is.EqualTo("DefaultImage"));
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
