using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Http;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §23 <c>POST /api/jobs/dat-refresh</c>, <c>GET /api/admin/dats</c>, §4.1 step 9 startup load; MariaDB + stub HTTP.</summary>
[Category("Integration")]
public class DatEndpointsTests
{
    private const string Gb = "Nintendo - Game Boy";
    private const string Gbc = "Nintendo - Game Boy Color";

    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private string _remote = null!;
    private StubHttpHandler _server = null!;

    private static Dictionary<string, IReadOnlyList<FixtureGame>> Games() => new()
    {
        [Gb] = [new FixtureGame("Tetris (World) (Rev 1)", "Tetris (World) (Rev 1).gb", new SyntheticRomBuilder { Seed = 1 }.Build())],
        [Gbc] = [new FixtureGame("Pokemon - Gold Version (USA)", "Pokemon - Gold Version (USA).gbc", new SyntheticRomBuilder { Seed = 2 }.Build())],
        [IngestSeeder.GbaDat] = [],
        [IngestSeeder.N64Dat] = [],
        [IngestSeeder.NesDat] = [],
        [IngestSeeder.SnesDat] = [],
        [IngestSeeder.Atari2600Dat] = [],
        [IngestSeeder.ColecoVisionDat] = [],
        [IngestSeeder.GameGearDat] = [],
        [IngestSeeder.MasterSystemDat] = [],
        [IngestSeeder.MegaDriveDat] = [],
        [IngestSeeder.Sega32xDat] = [],
        [IngestSeeder.TurboGrafx16Dat] = [],
    };

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = await MariaDbServer.CreateDatabaseAsync();
        _server = new StubHttpHandler(request =>
        {
            var file = Path.Combine(_remote, Uri.UnescapeDataString(request.RequestUri!.AbsolutePath["/metadat/".Length..]));
            return File.Exists(file) ? StubHttpHandler.Bytes(File.ReadAllBytes(file)) : StubHttpHandler.Status(HttpStatusCode.NotFound);
        });
        _factory = new ApiFactory(connectionString)
        {
            TestServices = services => services.AddHttpClient(HttpClients.Dats).ConfigurePrimaryHttpMessageHandler(() => _server),
        };
        _factory.Settings["Sources:DatBaseUrl"] = "https://dats.test/metadat";
        _remote = Path.Combine(_factory.Root, "remote");
        FixtureDatWriter.Write(_remote, Games(), version: "2026.11.01");
        // What a previous refresh left behind; loaded at startup (step 9).
        FixtureDatWriter.Write(Path.Combine(_factory.Root, "cache", DatCache.FolderName), Games(), version: "2026.01.01",
            downloadedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    /// <summary>Spec T3 on its own host: the test titledb handler is registered and its compact file is cached before startup.</summary>
    [Test]
    public async Task TitleDbEntries_AppearInSearch_WithoutSizeOrCrc()
    {
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => RnRomManager.Core.Systems.SystemHandlerRegistration.AddSystemHandler<ExternalTitleDbTestHandler>(services),
        };
        FixtureDatWriter.Write(Path.Combine(factory.Root, "cache", DatCache.FolderName), Games(),
            titleDb: new Dictionary<string, IReadOnlyList<TitleDbRow>>
            {
                [ExternalTitleDbTestHandler.DatNameValue] = [new("0100000000001000", "The Orbit Expedition", null, null, 2020, [], ["en"], null, null)],
            });
        using var admin = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();

        using var response = await admin.GetAsync($"/api/dat/search?system={ExternalTitleDbTestHandler.SystemId}&q=orbit");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var hit = (await AuthClient.JsonAsync(response)).EnumerateArray().Single();
        Assert.That(hit.GetProperty("gameName").GetString(), Is.EqualTo("Orbit Expedition, The [0100000000001000]"));
        Assert.That(hit.GetProperty("romName").GetString(), Is.EqualTo("The Orbit Expedition [0100000000001000][v0].nsp"));
        Assert.That(hit.GetProperty("sizeBytes").GetInt64(), Is.Zero);
        Assert.That(hit.GetProperty("crc32").GetString(), Is.Empty);
    }

    [TestCase("POST", "/api/jobs/dat-refresh")]
    [TestCase("GET", "/api/admin/dats")]
    public async Task RoleMatrix_AnonymousIs401_UserIs403(string method, string url)
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var anonymousResponse = await anonymous.SendAsync(new HttpMethod(method), url, null, sendXsrf: true);
        using var userResponse = await user.SendAsync(new HttpMethod(method), url, null, sendXsrf: true);

        Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Startup_LoadsTheCachedDats_AndAdminDatsListsVersions()
    {
        Assert.That(_factory.Services.GetRequiredService<DatCache>().For("gb")!.Version, Is.EqualTo("2026.01.01"));

        using var response = await _admin.GetAsync("/api/admin/dats");
        var rows = (await AuthClient.JsonAsync(response)).EnumerateArray().ToList();

        Assert.That(rows.Select(r => r.GetProperty("datName").GetString()), Is.EqualTo(new[] { IngestSeeder.Atari2600Dat, IngestSeeder.ColecoVisionDat, IngestSeeder.TurboGrafx16Dat, Gb, IngestSeeder.GbaDat, Gbc, IngestSeeder.N64Dat, IngestSeeder.NesDat, IngestSeeder.SnesDat, IngestSeeder.Sega32xDat, IngestSeeder.GameGearDat, IngestSeeder.MasterSystemDat, IngestSeeder.MegaDriveDat }));
        Assert.That(rows.Select(r => r.GetProperty("version").GetString()), Is.All.EqualTo("2026.01.01"));
        Assert.That(rows[0].GetProperty("downloadedAt").GetDateTimeOffset(), Is.EqualTo(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
    }

    [Test]
    public async Task AdminDats_WithNoCache_IsEmpty()
    {
        Directory.Delete(_factory.Services.GetRequiredService<DatCache>().Directory, recursive: true);

        using var response = await _admin.GetAsync("/api/admin/dats");

        Assert.That((await AuthClient.JsonAsync(response)).GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task Refresh_DownloadsWithUserAgent_AndSwapsTheSet()
    {
        using var response = await _admin.PostAsync("/api/jobs/dat-refresh");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var jobId = (await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid();

        var job = await WaitForEndAsync(jobId);

        Assert.That(job.Status, Is.EqualTo(JobStatus.Completed));
        Assert.That(_server.Requests, Has.Count.EqualTo(65));
        Assert.That(_server.Requests.First().Headers.UserAgent.ToString(), Does.StartWith("RnRomManager/"));
        using var dats = await _admin.GetAsync("/api/admin/dats");
        Assert.That((await AuthClient.JsonAsync(dats))[0].GetProperty("version").GetString(), Is.EqualTo("2026.11.01"));
        Assert.That(_factory.Services.GetRequiredService<DatCache>().For("gbc")!.Version, Is.EqualTo("2026.11.01"));
    }

    [Test]
    public async Task Refresh_WhenLockHeld_Is409JobRunning()
    {
        using var held = _factory.Services.GetRequiredService<JobLock>().TryAcquire();

        using var response = await _admin.PostAsync("/api/jobs/dat-refresh");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("JobRunning"));
    }

    private async Task<Job> WaitForEndAsync(Guid jobId)
    {
        var jobs = _factory.Services.GetRequiredService<IJobRepository>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var job = await jobs.GetAsync(jobId, CancellationToken.None);
            if (job!.Status != JobStatus.Running || DateTime.UtcNow > deadline)
                return job;
            await Task.Delay(50);
        }
    }
}
