using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Http;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §23 <c>POST /api/jobs/ingest</c>, <c>GET /api/ingest/pending</c>, §7.1 start rules; against MariaDB.</summary>
[Category("Integration")]
public class IngestEndpointsTests
{
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
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

    private string Drop(string system, string name, TimeSpan age)
    {
        var path = Path.Combine(_factory.Root, "ingest", system, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    /// <summary>Every registered system's DAT, all empty (Switch's titledb file is passed separately).</summary>
    private static Dictionary<string, IReadOnlyList<FixtureGame>> Manifest() => new()
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
        [IngestSeeder.AmigaDat] = [], [IngestSeeder.ThreeDsDat] = [], [IngestSeeder.TurboGrafxCdDat] = [], [IngestSeeder.NeoGeoPocketDat] = [], [IngestSeeder.NeoGeoPocketColorDat] = [],
        [IngestSeeder.NintendoDsDat] = [],
        ["Nintendo - Game Boy"] = [],
        ["Nintendo - Game Boy Color"] = [],
    };

    /// <param name="withSwitch">False leaves out the Switch titledb file, so one registered system has no DATs (N4).</param>
    private void LoadDats(bool withSwitch = true)
    {
        var cache = _factory.Services.GetRequiredService<DatCache>();
        FixtureDatWriter.Write(cache.Directory, Manifest(), titleDb: withSwitch ? IngestSeeder.EmptySwitch : null, mame: IngestSeeder.MinimalMame, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
    }

    [TestCase("POST", "/api/jobs/ingest")]
    [TestCase("GET", "/api/ingest/pending")]
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
    public async Task Start_WithoutDats_Is409DatsMissing_NoJobRow_LockFree()
    {
        using var response = await _admin.PostAsync("/api/jobs/ingest");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("DatsMissing"));
        var jobs = await _factory.Services.GetRequiredService<IJobRepository>().ListAsync(null, 1, 10, CancellationToken.None);
        Assert.That(jobs.Total, Is.Zero);
        using var free = _factory.Services.GetRequiredService<JobLock>().TryAcquire();
        Assert.That(free, Is.Not.Null);
    }

    [Test]
    public async Task Start_WithAnEmptySwitchDat_PassesThePreflight()
    {
        LoadDats();

        using var response = await _admin.PostAsync("/api/jobs/ingest");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await WaitForEndAsync((await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid());
    }

    [Test]
    public void Startup_CreatesTheSwitchIngestFolder() =>
        Assert.That(Directory.Exists(Path.Combine(_factory.Root, "ingest", "switch", "_failed")), Is.True);

    /// <summary>N4: a valid selection without any Switch file is still refused while Switch has no DAT.</summary>
    [Test]
    public async Task Selection_WithoutTheSwitchDat_Is409DatsMissing()
    {
        LoadDats(withSwitch: false);
        var name = Path.GetFileName(Drop("gb", "tetris.gb", TimeSpan.FromMinutes(1)));

        using var response = await _admin.PostAsync("/api/jobs/ingest", new { files = new[] { new { system = "gb", name } } });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("DatsMissing"));
        var jobs = await _factory.Services.GetRequiredService<IJobRepository>().ListAsync(null, 1, 10, CancellationToken.None);
        Assert.That(jobs.Total, Is.Zero);
    }

    [Test]
    public async Task WithoutTheSwitchDat_ABadRequestIsStill400_AndABusyLockStillJobRunning()
    {
        LoadDats(withSwitch: false);

        using (var bad = await _admin.PostAsync("/api/jobs/ingest", new { files = Array.Empty<object>() }))
            Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var held = _factory.Services.GetRequiredService<JobLock>().TryAcquire();
        using var busy = await _admin.PostAsync("/api/jobs/ingest", new { files = new[] { new { system = "gb", name = "x.gb" } } });
        Assert.That(await AuthClient.CodeAsync(busy), Is.EqualTo("JobRunning"));
    }

    [Test]
    public async Task Start_WhenLockHeld_Is409JobRunning()
    {
        LoadDats();
        using var held = _factory.Services.GetRequiredService<JobLock>().TryAcquire();

        using var response = await _admin.PostAsync("/api/jobs/ingest");

        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("JobRunning"));
    }

    [Test]
    public async Task Start_RunsInTheBackground_TooRecentFilesSkipped()
    {
        LoadDats();
        var recent = Drop("gb", "copying.gb", TimeSpan.Zero);

        using var response = await _admin.PostAsync("/api/jobs/ingest");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var jobId = (await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid();

        var job = await WaitForEndAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Completed));
        Assert.That(job.Type, Is.EqualTo(JobType.Ingest));
        using var items = await _admin.GetAsync($"/api/jobs/{jobId}/items?outcome=Skipped");
        var item = (await AuthClient.JsonAsync(items)).GetProperty("items")[0];
        Assert.That(item.GetProperty("sourcePath").GetString(), Is.EqualTo(recent));
        Assert.That(item.GetProperty("reason").GetString(), Is.EqualTo(IngestReason.TooRecent));
    }

    private async Task<List<string>> ItemSourcePaths(Guid jobId)
    {
        using var response = await _admin.GetAsync($"/api/jobs/{jobId}/items?pageSize=200");
        return (await AuthClient.JsonAsync(response)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("sourcePath").GetString()!).ToList();
    }

    private async Task<Guid> StartWith(object body)
    {
        using var response = await _admin.PostAsync("/api/jobs/ingest", body);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var jobId = (await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid();
        Assert.That((await WaitForEndAsync(jobId)).Status, Is.EqualTo(JobStatus.Completed));
        return jobId;
    }

    [Test]
    public async Task Start_WithFiles_IngestsOnlyThose_IgnoresUnknownAndRepeats_LeavesTheRestAlone()
    {
        LoadDats();
        var chosen = Drop("gb", "chosen.gb", TimeSpan.FromHours(1));
        var other = Drop("gb", "other.gb", TimeSpan.FromHours(1));
        var otherBytes = await File.ReadAllBytesAsync(other);

        var jobId = await StartWith(new
        {
            files = new object[]
            {
                new { system = "gb", name = "chosen.gb" }, new { system = "gb", name = "chosen.gb" },
                new { system = "gb", name = "missing.gb" }, new { system = "sg1000", name = "other.gb" },
            },
        });

        Assert.That(await ItemSourcePaths(jobId), Is.EqualTo(new[] { chosen }));
        Assert.That(File.Exists(chosen), Is.False, "processed (too small → _failed)");
        Assert.That(await File.ReadAllBytesAsync(other), Is.EqualTo(otherBytes), "untouched");
    }

    [Test]
    public async Task Start_WithAnArchiveName_IngestsItsEntries()
    {
        LoadDats();
        var archive = Path.Combine(_factory.Root, "ingest", "gb", "pack.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        await using (var entry = zip.CreateEntry("inner.gb").Open())
            entry.Write([1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddHours(-1));
        var loose = Drop("gb", "loose.gb", TimeSpan.FromHours(1));

        var jobId = await StartWith(new { files = new[] { new { system = "gb", name = "pack.zip" } } });

        var items = await ItemSourcePaths(jobId);
        Assert.That(items, Is.Not.Empty);
        Assert.That(items, Has.None.EqualTo(loose));
        Assert.That(File.Exists(loose), Is.True);
    }

    [TestCase("{\"files\":[]}")]
    [TestCase("{\"files\":[{\"system\":\"gb\",\"name\":\"\"}]}")]
    [TestCase("{\"files\":[{\"system\":null,\"name\":\"a.gb\"}]}")]
    [TestCase("{\"files\":[null]}")]
    public async Task Start_WithAnEmptyOrIncompleteFileList_Is400(string json)
    {
        LoadDats();
        using var response = await _admin.PostAsync("/api/jobs/ingest", System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("ValidationFailed"));
    }

    [Test]
    public async Task Start_WithFilesNull_IngestsEverything()
    {
        LoadDats();
        var a = Drop("gb", "a.gb", TimeSpan.FromHours(1));
        var b = Drop("gbc", "b.gbc", TimeSpan.FromHours(1));

        var jobId = await StartWith(new { files = (object?)null });

        Assert.That(await ItemSourcePaths(jobId), Is.EquivalentTo(new[] { a, b }));
    }

    [Test]
    public async Task Pending_ListsEverySystem_IgnoringJunk()
    {
        Drop("gb", "Tetris.gb", TimeSpan.FromHours(1));
        Drop("gb", "Thumbs.db", TimeSpan.FromHours(1));
        Drop("gb", ".hidden", TimeSpan.FromHours(1));

        using var response = await _admin.GetAsync("/api/ingest/pending");
        var systems = (await AuthClient.JsonAsync(response)).EnumerateArray().ToList();

        Assert.That(systems.Select(s => s.GetProperty("system").GetString()), Is.EqualTo(new[] { "gb", "gbc", "gba", "n64", "nes", "fds", "snes", "satellaview", "sufami", "vb", "a2600", "a5200", "a7800", "lynx", "jaguar", "coleco", "gg", "sms", "md", "s32x", "tg16", "switch", "psx", "psp", "gc", "wii", "x360", "saturn", "nds", "segacd", "3do", "c64", "pet", "vic20", "amiga", "3ds", "tgcd", "dos", "ngp", "ngpc", "mame2003" }));
        var gb = systems.Single(s => s.GetProperty("system").GetString() == "gb").GetProperty("files");
        Assert.That(gb.GetArrayLength(), Is.EqualTo(1));
        Assert.That(gb[0].GetProperty("name").GetString(), Is.EqualTo("Tetris.gb"));
        Assert.That(gb[0].GetProperty("sizeBytes").GetInt64(), Is.EqualTo(4));
        Assert.That(gb[0].TryGetProperty("modifiedAt", out _), Is.True);
    }

    /// <summary>
    /// Upgrade: a cache left by a version without DS lacks DS files (here one metadat file), so every import, even one of
    /// only another system's files, is refused until a refresh loads all five DS files.
    /// </summary>
    [Test]
    public async Task Upgrade_AMissingDsFile_BlocksEveryImport_UntilARefreshLoadsTheFiveDsFiles()
    {
        StubHttpHandler server = null!;
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => services.AddHttpClient(HttpClients.Dats).ConfigurePrimaryHttpMessageHandler(() => server),
        };
        var remote = Path.Combine(factory.Root, "remote");
        server = new StubHttpHandler(request =>
        {
            var file = Path.Combine(remote, Uri.UnescapeDataString(request.RequestUri!.AbsolutePath["/metadat/".Length..]));
            return File.Exists(file) ? StubHttpHandler.Bytes(File.ReadAllBytes(file)) : StubHttpHandler.Status(HttpStatusCode.NotFound);
        });
        factory.Settings["Sources:DatBaseUrl"] = "https://dats.test/metadat";
        factory.Settings["Sources:TitleDbUrl"] = "https://dats.test/metadat/US.en.json";
        factory.Settings["Sources:MameDatUrl"] = "https://dats.test/metadat/mame.xml";
        FixtureDatWriter.Write(remote, Manifest(), layouts: IngestSeeder.RedumpLayouts);
        File.WriteAllText(Path.Combine(remote, "US.en.json"), "{}", new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(remote, "mame.xml"), IngestSeeder.MinimalMame["MAME"], new System.Text.UTF8Encoding(false));
        // What a version without DS left behind, here missing the DS genre metadat file.
        var layouts = new Dictionary<string, FixtureDatLayout>(IngestSeeder.RedumpLayouts)
        {
            [IngestSeeder.NintendoDsDat] = new(DatKind.NoIntro, [DatKind.ReleaseYear, DatKind.Developer, DatKind.Publisher]),
        };
        FixtureDatWriter.Write(Path.Combine(factory.Root, "cache", DatCache.FolderName), Manifest(), titleDb: IngestSeeder.EmptySwitch, mame: IngestSeeder.MinimalMame, layouts: layouts);
        using var admin = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        Assert.That(Directory.Exists(Path.Combine(factory.Root, "ingest", "nds", "_failed")), Is.True, "startup creates the DS folders");
        var path = Path.Combine(factory.Root, "ingest", "gb", "tetris.gb");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var selection = new { files = new[] { new { system = "gb", name = "tetris.gb" } } };

        using (var refused = await admin.PostAsync("/api/jobs/ingest", selection))
        {
            Assert.That((refused.StatusCode, await AuthClient.CodeAsync(refused)), Is.EqualTo((HttpStatusCode.Conflict, "DatsMissing")));
            Assert.That((await factory.Services.GetRequiredService<IJobRepository>().ListAsync(null, 1, 10, CancellationToken.None)).Total, Is.Zero);
        }

        using (var refresh = await admin.PostAsync("/api/jobs/dat-refresh"))
        {
            Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            var job = await WaitForEndAsync(factory, (await AuthClient.JsonAsync(refresh)).GetProperty("jobId").GetGuid());
            Assert.That(job.Status, Is.EqualTo(JobStatus.Completed), job.Error);
        }
        Assert.That(factory.Services.GetRequiredService<DatCache>().For("nds"), Is.Not.Null);

        using var retried = await admin.PostAsync("/api/jobs/ingest", selection);
        Assert.That(retried.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var ingest = await WaitForEndAsync(factory, (await AuthClient.JsonAsync(retried)).GetProperty("jobId").GetGuid());
        Assert.That(ingest.Status, Is.EqualTo(JobStatus.Completed), ingest.Error);
    }

    [Test]
    public async Task LooseNds_WithAValidHeaderCrc_GoesToReviewAsNoDatMatch_WithTheSixHeaderFields()
    {
        LoadDats();
        var path = Path.Combine(_factory.Root, "ingest", "nds", "review me.nds");
        await File.WriteAllBytesAsync(path, new SyntheticNdsRomBuilder { Seed = 5, Title = "REVIEW ME", GameCode = "ARVE", RomVersion = 1 }.Build());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));

        await StartWith(new { files = new[] { new { system = "nds", name = "review me.nds" } } });

        using var list = await _admin.GetAsync("/api/review?system=nds");
        var item = (await AuthClient.JsonAsync(list)).GetProperty("items").EnumerateArray().Single();
        Assert.That((item.GetProperty("system").GetString(), item.GetProperty("reason").GetString(), item.GetProperty("headerTitle").GetString()),
            Is.EqualTo(("nds", "NoDatMatch", "REVIEW ME")));
        using var detail = await _admin.GetAsync($"/api/review/{item.GetProperty("id").GetInt64()}");
        var header = (await AuthClient.JsonAsync(detail)).GetProperty("header");
        Assert.That(header.EnumerateObject().Select(p => p.Name),
            Is.EquivalentTo(new[] { "title", "gameCode", "makerCode", "unitCode", "romVersion", "headerChecksumValid" }));
        Assert.That((header.GetProperty("title").GetString(), header.GetProperty("gameCode").GetString(), header.GetProperty("makerCode").GetString()),
            Is.EqualTo(("REVIEW ME", "ARVE", "01")));
        Assert.That((header.GetProperty("unitCode").GetInt32(), header.GetProperty("romVersion").GetInt32(), header.GetProperty("headerChecksumValid").GetBoolean()),
            Is.EqualTo((0, 1, true)));
    }


    private Task<Job> WaitForEndAsync(Guid jobId) => WaitForEndAsync(_factory, jobId);

    private static async Task<Job> WaitForEndAsync(ApiFactory factory, Guid jobId)
    {
        var jobs = factory.Services.GetRequiredService<IJobRepository>();
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
