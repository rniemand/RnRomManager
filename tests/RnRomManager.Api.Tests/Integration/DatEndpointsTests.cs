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
        [IngestSeeder.AmigaDat] = [], [IngestSeeder.ThreeDsDat] = [], [IngestSeeder.TurboGrafxCdDat] = [], [IngestSeeder.NeoGeoPocketDat] = [], [IngestSeeder.NeoGeoPocketColorDat] = [],
        [IngestSeeder.NintendoDsDat] = [],
    };

    [OneTimeSetUp]
    public async Task StartHost()
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
        _factory.Settings["Sources:MameDatUrl"] = "https://dats.test/metadat/mame.xml";
        _factory.Settings["Sources:FbneoDatUrl"] = "https://dats.test/metadat/fbneo.xml";
        _remote = Path.Combine(_factory.Root, "remote");
        FixtureDatWriter.Write(_remote, Games(), version: "2026.11.01", layouts: IngestSeeder.RedumpLayouts);
        // The upstream titledb source is an object keyed by nsuId; refresh turns it into Switch's compact file.
        File.WriteAllText(Path.Combine(_remote, "US.en.json"), "{}", new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_remote, "mame.xml"), IngestSeeder.MinimalMame["MAME"], new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_remote, "fbneo.xml"), IngestSeeder.MinimalMame["FBNeo - Arcade Games"], new System.Text.UTF8Encoding(false));
        // What a previous refresh left behind; loaded at startup (step 9).
        FixtureDatWriter.Write(Path.Combine(_factory.Root, "cache", DatCache.FolderName), Games(), version: "2026.01.01",
            downloadedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), titleDb: IngestSeeder.EmptySwitch, mame: IngestSeeder.MinimalMame, layouts: IngestSeeder.RedumpLayouts);
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
    public void TearDown()
    {
        _admin.Dispose();
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
            }, layouts: IngestSeeder.RedumpLayouts, mame: IngestSeeder.MinimalMame);
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
        Assert.That(names, Has.Count.EqualTo(42), "29 libretro systems, the 9 disc systems, Switch, the test titledb system, MAME and FBNeo");
        Assert.That(names, Is.EqualTo(names.Order(StringComparer.Ordinal)));
        var titleDb = rows.Single(r => r.GetProperty("datName").GetString() == ExternalTitleDbTestHandler.DatNameValue);
        Assert.That(titleDb.GetProperty("version").GetString(), Is.EqualTo(FixtureDatWriter.TitleDbVersion));
        Assert.That(titleDb.GetProperty("downloadedAt").GetDateTimeOffset(), Is.EqualTo(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var mame = rows.Single(r => r.GetProperty("datName").GetString() == "MAME");
        Assert.That(mame.GetProperty("version").GetString(), Is.EqualTo(FixtureDatWriter.MameVersion));

        using var download = await admin.GetAsync("/api/admin/dats/MAME/download");
        Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName, Is.EqualTo("MAME.xml"));
        Assert.That(await download.Content.ReadAsStringAsync(), Is.EqualTo(IngestSeeder.MinimalMame["MAME"]));
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

    [Test]
    public void DefaultFactory_HostsNoDatRefreshScheduler()
    {
        Assert.That(_factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<DatRefreshScheduler>(), Is.Empty);
    }

    [Test]
    public async Task Settings_DefaultToOnAt0400_ThenSaveAndReturnTheNextRun()
    {
        using (var response = await _admin.GetAsync("/api/admin/dats/settings"))
        {
            var body = await AuthClient.JsonAsync(response);
            Assert.That((body.GetProperty("enabled").GetBoolean(), body.GetProperty("time").GetString()), Is.EqualTo((true, "04:00")));
            Assert.That(body.GetProperty("timeZone").GetString(), Is.Not.Empty);
            Assert.That(body.GetProperty("nextRunAt").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.String));
        }

        using (var saved = await _admin.SendAsync(HttpMethod.Put, "/api/admin/dats/settings", new { enabled = true, time = "05:30" }, sendXsrf: true))
        {
            Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await AuthClient.JsonAsync(saved);
            Assert.That(body.GetProperty("time").GetString(), Is.EqualTo("05:30"));
            var next = body.GetProperty("nextRunAt").GetDateTimeOffset();
            Assert.That(TimeZoneInfo.ConvertTime(next, TimeZoneInfo.Local).TimeOfDay, Is.EqualTo(new TimeSpan(5, 30, 0)));
        }

        using (var disabled = await _admin.SendAsync(HttpMethod.Put, "/api/admin/dats/settings", new { enabled = false, time = "05:30" }, sendXsrf: true))
            Assert.That((await AuthClient.JsonAsync(disabled)).GetProperty("nextRunAt").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
        using var reread = await _admin.GetAsync("/api/admin/dats/settings");
        Assert.That((await AuthClient.JsonAsync(reread)).GetProperty("enabled").GetBoolean(), Is.False);
    }

    private static IEnumerable<TestCaseData> InvalidSettings()
    {
        yield return new TestCaseData((object)"text", "").SetName("DatSettings non-object body");
        yield return new TestCaseData(new { time = "04:00" }, "enabled").SetName("DatSettings missing enabled");
        yield return new TestCaseData(new { enabled = "yes", time = "04:00" }, "enabled").SetName("DatSettings non-boolean enabled");
        yield return new TestCaseData(new { enabled = true, time = "24:00" }, "time").SetName("DatSettings invalid time");
        yield return new TestCaseData(new { enabled = true }, "time").SetName("DatSettings missing time");
    }

    [TestCaseSource(nameof(InvalidSettings))]
    public async Task Settings_Invalid_Is400OnTheField_AndSavesNothing(object body, string field)
    {
        using var response = await _admin.SendAsync(HttpMethod.Put, "/api/admin/dats/settings", body, sendXsrf: true);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), Is.True);
        using var reread = await _admin.GetAsync("/api/admin/dats/settings");
        Assert.That((await AuthClient.JsonAsync(reread)).GetProperty("time").GetString(), Is.EqualTo("04:00"));
    }

    [TestCase("POST", "/api/jobs/dat-refresh")]
    [TestCase("GET", "/api/admin/dats")]
    [TestCase("GET", "/api/admin/dats/settings")]
    [TestCase("GET", "/api/admin/dats/Nintendo%20-%20Game%20Boy/download")]
    [TestCase("PUT", "/api/admin/dats/settings")]
    public async Task RoleMatrix_AnonymousIs401_UserIs403(string method, string url)
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        var body = method == "PUT" ? new { } : null;
        using var anonymousResponse = await anonymous.SendAsync(new HttpMethod(method), url, body, sendXsrf: true);
        using var userResponse = await user.SendAsync(new HttpMethod(method), url, body, sendXsrf: true);

        Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Startup_LoadsTheCachedDats_AndAdminDatsListsVersions()
    {
        Assert.That(_factory.Services.GetRequiredService<DatCache>().For("gb")!.Version, Is.EqualTo("2026.01.01"));

        using var response = await _admin.GetAsync("/api/admin/dats");
        var rows = (await AuthClient.JsonAsync(response)).EnumerateArray().ToList();

        Assert.That(rows.Select(r => r.GetProperty("datName").GetString()), Is.EqualTo(new[] { IngestSeeder.Atari2600Dat, IngestSeeder.Atari5200Dat, IngestSeeder.Atari7800Dat, IngestSeeder.JaguarDat, IngestSeeder.LynxDat, IngestSeeder.ColecoVisionDat, IngestSeeder.C64Dat, IngestSeeder.AmigaDat, IngestSeeder.PetDat, IngestSeeder.Vic20Dat, "FBNeo - Arcade Games", "MAME", IngestSeeder.Xbox360Dat, IngestSeeder.TurboGrafx16Dat, IngestSeeder.TurboGrafxCdDat, IngestSeeder.FdsDat, Gb, IngestSeeder.GbaDat, Gbc, IngestSeeder.GameCubeDat, IngestSeeder.ThreeDsDat, IngestSeeder.N64Dat, IngestSeeder.NintendoDsDat, IngestSeeder.NesDat, IngestSeeder.SwitchDat, IngestSeeder.SatellaviewDat, IngestSeeder.SufamiTurboDat, IngestSeeder.SnesDat, IngestSeeder.VirtualBoyDat, IngestSeeder.WiiDat, IngestSeeder.NeoGeoPocketDat, IngestSeeder.NeoGeoPocketColorDat, IngestSeeder.Sega32xDat, IngestSeeder.GameGearDat, IngestSeeder.MasterSystemDat, IngestSeeder.MegaDriveDat, IngestSeeder.SegaCdDat, IngestSeeder.SaturnDat, IngestSeeder.PsxDat, IngestSeeder.PspDat, IngestSeeder.ThreeDoDat }));
        string[] romsets = ["MAME", "FBNeo - Arcade Games"];
        Assert.That(rows.Where(r => !romsets.Contains(r.GetProperty("datName").GetString())).Select(r => r.GetProperty("version").GetString()), Is.All.EqualTo("2026.01.01"));
        Assert.That(rows.Where(r => romsets.Contains(r.GetProperty("datName").GetString())).Select(r => r.GetProperty("version").GetString()), Is.EqualTo(new[] { FixtureDatWriter.MameVersion, FixtureDatWriter.MameVersion }));
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

    private DatManifestEntry ManifestEntry(string datName) =>
        DatManifest.Read(Path.Combine(_factory.Services.GetRequiredService<DatCache>().Directory, DatManifest.FileName))
            .First(e => e.DatName == datName && (DatKind.IsMainDat(e.File) || e.File.StartsWith(TitleDb.Folder + "/", StringComparison.Ordinal)));

    /// <summary>B29: a main DAT and a titledb file, streamed as attachments with their file names.</summary>
    [TestCase(Gb)]
    [TestCase(IngestSeeder.SwitchDat)]
    public async Task DownloadDat_StreamsTheListedFile_AsAnAttachment(string datName)
    {
        var entry = ManifestEntry(datName);
        var expected = await File.ReadAllBytesAsync(Path.Combine(_factory.Services.GetRequiredService<DatCache>().Directory, entry.File));

        using var response = await _admin.GetAsync($"/api/admin/dats/{Uri.EscapeDataString(datName)}/download");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentDisposition!.DispositionType, Is.EqualTo("attachment"));
        Assert.That(response.Content.Headers.ContentDisposition.FileNameStar, Is.EqualTo(Path.GetFileName(entry.File)));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(expected));
    }

    [TestCase("Nope")]
    [TestCase("..")]
    public async Task DownloadDat_UnknownName_Is404(string datName)
    {
        using var response = await _admin.GetAsync($"/api/admin/dats/{Uri.EscapeDataString(datName)}/download");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DownloadDat_MissingFileOrCache_Is404()
    {
        var directory = _factory.Services.GetRequiredService<DatCache>().Directory;
        File.Delete(Path.Combine(directory, ManifestEntry(Gb).File));
        using (var missingFile = await _admin.GetAsync($"/api/admin/dats/{Uri.EscapeDataString(Gb)}/download"))
            Assert.That(missingFile.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        Directory.Delete(directory, recursive: true);
        using var noCache = await _admin.GetAsync($"/api/admin/dats/{Uri.EscapeDataString(Gbc)}/download");
        Assert.That(noCache.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
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
        Assert.That(_server.Requests, Has.Count.EqualTo(140), "5 per No-Intro libretro system with metadat, 1 for Commodore 64, Commodore PET, Commodore VIC-20, Commodore Amiga and each disc system (no metadat), 4 for Nintendo 3DS (no releaseyear), one titledb source, one MAME DAT and one FBNeo DAT");
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
