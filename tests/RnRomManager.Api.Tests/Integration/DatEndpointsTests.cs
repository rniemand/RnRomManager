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
        [IngestSeeder.PsxDat] = [],
        [IngestSeeder.PspDat] = [],
        [IngestSeeder.GameCubeDat] = [],
        [IngestSeeder.WiiDat] = [],
        [IngestSeeder.Xbox360Dat] = [],
        [IngestSeeder.SaturnDat] = [],
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
        _factory.Settings["Sources:TitleDbUrl"] = "https://dats.test/metadat/US.en.json";
        _remote = Path.Combine(_factory.Root, "remote");
        FixtureDatWriter.Write(_remote, Games(), version: "2026.11.01", layouts: IngestSeeder.RedumpLayouts);
        // The upstream titledb source is an object keyed by nsuId; refresh turns it into Switch's compact file.
        File.WriteAllText(Path.Combine(_remote, "US.en.json"), "{}", new System.Text.UTF8Encoding(false));
        // What a previous refresh left behind; loaded at startup (step 9).
        FixtureDatWriter.Write(Path.Combine(_factory.Root, "cache", DatCache.FolderName), Games(), version: "2026.01.01",
            downloadedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), titleDb: IngestSeeder.EmptySwitch, layouts: IngestSeeder.RedumpLayouts);

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
        var (host, admin) = await TitleDbHostAsync();
        using var factory = host;
        using var client = admin;

        using var response = await admin.GetAsync($"/api/dat/search?system={ExternalTitleDbTestHandler.SystemId}&q=orbit");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var hit = (await AuthClient.JsonAsync(response)).EnumerateArray().Single();
        Assert.That(hit.GetProperty("gameName").GetString(), Is.EqualTo("Orbit Expedition, The [0100000000001000]"));
        Assert.That(hit.GetProperty("romName").GetString(), Is.EqualTo("The Orbit Expedition [0100000000001000][v0].nsp"));
        Assert.That(hit.GetProperty("sizeBytes").GetInt64(), Is.Zero);
        Assert.That(hit.GetProperty("crc32").GetString(), Is.Empty);
    }

    /// <summary>A host of its own with the test titledb handler registered and its compact file cached before startup (step 18).</summary>
    private static async Task<(ApiFactory Factory, AuthClient Admin)> TitleDbHostAsync()
    {
        var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => RnRomManager.Core.Systems.SystemHandlerRegistration.AddSystemHandler<ExternalTitleDbTestHandler>(services),
        };
        FixtureDatWriter.Write(Path.Combine(factory.Root, "cache", DatCache.FolderName), Games(),
            titleDb: new Dictionary<string, IReadOnlyList<TitleDbRow>>
            {
                [ExternalTitleDbTestHandler.DatNameValue] = [new("0100000000001000", "The Orbit Expedition", null, null, 2020, [], ["en"], null, null)],
                [IngestSeeder.SwitchDat] = [],
            }, layouts: IngestSeeder.RedumpLayouts);
        var admin = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        return (factory, admin);
    }

    [Test]
    public async Task AdminDats_ListsTheTitleDbFile_InDatNameOrder()
    {
        var (host, admin) = await TitleDbHostAsync();
        using var factory = host;
        using var client = admin;

        using var response = await admin.GetAsync("/api/admin/dats");
        var rows = (await AuthClient.JsonAsync(response)).EnumerateArray().ToList();

        var names = rows.Select(r => r.GetProperty("datName").GetString()!).ToList();
        Assert.That(names, Has.Count.EqualTo(21), "13 libretro systems, the 6 disc systems, Switch and the test titledb system");
        Assert.That(names, Is.EqualTo(names.Order(StringComparer.Ordinal)));
        var titleDb = rows.Single(r => r.GetProperty("datName").GetString() == ExternalTitleDbTestHandler.DatNameValue);
        Assert.That(titleDb.GetProperty("version").GetString(), Is.EqualTo(FixtureDatWriter.TitleDbVersion));
        Assert.That(titleDb.GetProperty("downloadedAt").GetDateTimeOffset(), Is.EqualTo(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [TestCase("malformed", TestName = "AdminDats_UnusableLiveCache_IsEmpty malformed manifest")]
    [TestCase("linked", TestName = "AdminDats_UnusableLiveCache_IsEmpty linked live folder")]
    [TestCase("backupOnly", TestName = "AdminDats_UnusableLiveCache_IsEmpty only a backup")]
    public async Task AdminDats_UnusableLiveCache_IsEmpty(string problem)
    {
        var live = _factory.Services.GetRequiredService<DatCache>().Directory;
        var elsewhere = problem == "backupOnly"
            ? DatPublication.PreviousPath(Path.Combine(_factory.Root, "cache"))
            : Path.Combine(_factory.Root, "outside");
        switch (problem)
        {
            case "malformed":
                File.WriteAllText(Path.Combine(live, DatManifest.FileName), "not json");
                break;
            case "linked":
                Directory.Move(live, elsewhere);
                if (!TestLinks.TryLinkDirectory(live, elsewhere))
                    Assert.Ignore("this account can't create a directory link");
                break;
            default:
                Directory.Move(live, elsewhere); // as mid-swap: the old set is in the backup and live is absent
                break;
        }
        try
        {
            using var response = await _admin.GetAsync("/api/admin/dats");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await AuthClient.JsonAsync(response)).GetArrayLength(), Is.Zero);
        }
        finally
        {
            if (problem == "linked")
                TestLinks.RemoveDirectoryLink(live);
        }
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

        Assert.That(rows.Select(r => r.GetProperty("datName").GetString()), Is.EqualTo(new[] { IngestSeeder.Atari2600Dat, IngestSeeder.ColecoVisionDat, IngestSeeder.Xbox360Dat, IngestSeeder.TurboGrafx16Dat, Gb, IngestSeeder.GbaDat, Gbc, IngestSeeder.GameCubeDat, IngestSeeder.N64Dat, IngestSeeder.NesDat, IngestSeeder.SwitchDat, IngestSeeder.SnesDat, IngestSeeder.WiiDat, IngestSeeder.Sega32xDat, IngestSeeder.GameGearDat, IngestSeeder.MasterSystemDat, IngestSeeder.MegaDriveDat, IngestSeeder.SaturnDat, IngestSeeder.PsxDat, IngestSeeder.PspDat }));
        Assert.That(rows.Select(r => r.GetProperty("version").GetString()), Is.All.EqualTo("2026.01.01"));
        Assert.That(rows[0].GetProperty("downloadedAt").GetDateTimeOffset(), Is.EqualTo(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
    }

    [Test]
    public async Task AdminDats_ListsARedumpDat()
    {
        // B1: a redump/ main DAT is a row like a no-intro/ one; the listing reads the manifest as it is.
        const string psx = IngestSeeder.PsxDat;
        Assert.That(File.Exists(Path.Combine(_factory.Services.GetRequiredService<DatCache>().Directory, DatKind.Redump, psx + ".dat")), Is.True);

        using var response = await _admin.GetAsync("/api/admin/dats");
        var rows = (await AuthClient.JsonAsync(response)).EnumerateArray().ToList();

        var row = rows.Single(r => r.GetProperty("datName").GetString() == psx);
        Assert.That(row.GetProperty("version").GetString(), Is.EqualTo("2026.01.01"));
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
        var clock = _factory.Services.GetRequiredService<TimeProvider>();
        var before = clock.GetUtcNow().UtcDateTime.ToString("yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture);
        using var response = await _admin.PostAsync("/api/jobs/dat-refresh");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var jobId = (await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid();

        var job = await WaitForEndAsync(jobId);
        var after = clock.GetUtcNow().UtcDateTime.ToString("yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture);

        Assert.That(job.Status, Is.EqualTo(JobStatus.Completed), job.Error);
        Assert.That(_server.Requests, Has.Count.EqualTo(72), "5 per No-Intro libretro system, 1 per disc system (no metadat) and one titledb source");
        Assert.That(_server.Requests.Count(r => r.RequestUri!.AbsoluteUri == "https://dats.test/metadat/US.en.json"), Is.EqualTo(1));
        // titledb is versioned by the UTC date of its download, apart from the libretro fixture version.
        Assert.That(_factory.Services.GetRequiredService<DatCache>().For("switch")!.Version, Is.AnyOf(before, after));
        Assert.That(_factory.Services.GetRequiredService<DatCache>().For("switch")!.Roms, Is.Empty);
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
