using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Deleted ROMs D5 <c>POST /api/ingest/restore</c>, against MariaDB.</summary>
[Category("Integration")]
public class IngestRestoreEndpointsTests
{
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();
    private string Ingest(params string[] relative) => Path.Combine([_factory.Root, "ingest", .. relative]);

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
        Directory.CreateDirectory(Ingest("gb", "_removed"));
    }

    [TearDown]
    public void TearDown() => _admin.Dispose();

    /// <param name="withSwitch">False leaves out the Switch titledb, so the ingest job is refused with DatsMissing.</param>
    private void LoadDats(bool withSwitch = true)
    {
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
            ["Nintendo - Game Boy"] = [],
            ["Nintendo - Game Boy Color"] = [],
        }, titleDb: withSwitch ? IngestSeeder.EmptySwitch : null, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
    }

    /// <summary>A small (too small → <c>_failed</c>) file, old enough for the ingest job's TooRecent rule.</summary>
    private string Drop(params string[] relative)
    {
        var path = Ingest(relative);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        return path;
    }

    private Task<HttpResponseMessage> Restore(params object[] files) => _admin.PostAsync("/api/ingest/restore", new { files });

    private async Task<Job> WaitForEndAsync(Guid jobId)
    {
        var jobs = Service<IJobRepository>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var job = await jobs.GetAsync(jobId, CancellationToken.None);
            if (job!.Status != JobStatus.Running || DateTime.UtcNow > deadline)
                return job;
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task PlainUser_Is403_Anonymous_Is401()
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();
        var body = new { files = new[] { new { system = "gb", name = "a.gb" } } };

        using var anonymousResponse = await anonymous.PostAsync("/api/ingest/restore", body);
        using var userResponse = await user.PostAsync("/api/ingest/restore", body);

        Assert.That((anonymousResponse.StatusCode, userResponse.StatusCode), Is.EqualTo((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)));
    }

    [TestCase("[]")]
    [TestCase("""[{"system":"nope","name":"a.gb"}]""")]
    [TestCase("""[{"system":"gb","name":"../a.gb"}]""")]
    [TestCase("""[{"system":"gb","name":""}]""")]
    [TestCase("""[null]""")]
    [TestCase("tooMany")]
    public async Task InvalidRequest_Is400_AndMovesNothing(string files)
    {
        var keep = Drop("gb", "_removed", "a.gb");
        object body = files == "tooMany"
            ? new { files = Enumerable.Range(0, 501).Select(i => new { system = "gb", name = $"{i}.gb" }).ToArray() }
            : new { files = JsonSerializer.Deserialize<JsonElement>(files) };

        using var response = await _admin.PostAsync("/api/ingest/restore", body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("files", out _), Is.True);
        Assert.That(File.Exists(keep), Is.True);
    }

    [Test]
    public async Task MissingBody_Is400()
    {
        using var response = await _admin.PostAsync("/api/ingest/restore");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task WhileAJobHoldsTheLock_Is409_AndMovesNothing()
    {
        LoadDats();
        var keep = Drop("gb", "_removed", "a.gb");

        using (Service<JobLock>().TryAcquire())
        {
            using var response = await Restore(new { system = "gb", name = "a.gb" });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("JobRunning"));
        }
        Assert.That(File.Exists(keep), Is.True);
    }

    [Test]
    public async Task Restore_MovesUnderAFreeName_AndIngestsOnlyTheRestoredFiles()
    {
        LoadDats();
        Drop("gb", "_removed", "a.gb");
        var pending = Drop("gb", "a.gb"); // the restored file must not replace it, nor be ingested with it
        var pendingBytes = await File.ReadAllBytesAsync(pending);

        using var response = await Restore(new { system = "gb", name = "a.gb" }, new { system = "gb", name = "gone.gb" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);
        Assert.That(body.GetProperty("restored").GetInt32(), Is.EqualTo(1));
        var failed = body.GetProperty("failed").EnumerateArray().Single();
        Assert.That((failed.GetProperty("system").GetString(), failed.GetProperty("name").GetString()), Is.EqualTo(("gb", "gone.gb")));
        var jobId = body.GetProperty("jobId").GetGuid();
        Assert.That((await WaitForEndAsync(jobId)).Status, Is.EqualTo(JobStatus.Completed));

        using var items = await _admin.GetAsync($"/api/jobs/{jobId}/items?pageSize=200");
        Assert.That((await AuthClient.JsonAsync(items)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sourcePath").GetString()),
            Is.EqualTo(new[] { Ingest("gb", "a (2).gb") }));
        Assert.That(File.Exists(Ingest("gb", "_removed", "a.gb")), Is.False);
        Assert.That(await File.ReadAllBytesAsync(pending), Is.EqualTo(pendingBytes), "the pending file is untouched");

        await Service<AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        var row = await db.QuerySingleAsync<(string, string)>("SELECT path, target_path FROM audit_events WHERE action = 'RemovedRestored'");
        Assert.That(row, Is.EqualTo(("ingest/gb/_removed/a.gb", "ingest/gb/a (2).gb")));
    }

    [Test]
    public async Task NothingRestored_HasNoJob()
    {
        LoadDats();

        using var response = await Restore(new { system = "gb", name = "gone.gb" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);
        Assert.That((body.GetProperty("restored").GetInt32(), body.GetProperty("jobId").ValueKind), Is.EqualTo((0, JsonValueKind.Null)));
        Assert.That((await Service<IJobRepository>().ListAsync(null, 1, 10, CancellationToken.None)).Total, Is.Zero);
    }

    [Test]
    public async Task DatsMissing_StillRestores_WithoutAJob_AndTheFileWaitsOnIngestion()
    {
        LoadDats(withSwitch: false);
        Drop("gb", "_removed", "a.gb");

        using var response = await Restore(new { system = "gb", name = "a.gb" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);
        Assert.That((body.GetProperty("restored").GetInt32(), body.GetProperty("jobId").ValueKind), Is.EqualTo((1, JsonValueKind.Null)));
        Assert.That(File.Exists(Ingest("gb", "a.gb")), Is.True);
        using var free = Service<JobLock>().TryAcquire();
        Assert.That(free, Is.Not.Null);
    }
}
