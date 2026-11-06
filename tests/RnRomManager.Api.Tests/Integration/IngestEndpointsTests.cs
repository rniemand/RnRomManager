using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
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

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
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

    private string Drop(string system, string name, TimeSpan age)
    {
        var path = Path.Combine(_factory.Root, "ingest", system, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private void LoadDats()
    {
        var cache = _factory.Services.GetRequiredService<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.GbaDat] = [],
            [IngestSeeder.N64Dat] = [],
            ["Nintendo - Game Boy"] = [],
            ["Nintendo - Game Boy Color"] = [],
        });
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
                new { system = "gb", name = "missing.gb" }, new { system = "nes", name = "other.gb" },
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

        Assert.That(systems.Select(s => s.GetProperty("system").GetString()), Is.EquivalentTo(new[] { "gb", "gbc", "gba", "n64" }));
        var gb = systems.Single(s => s.GetProperty("system").GetString() == "gb").GetProperty("files");
        Assert.That(gb.GetArrayLength(), Is.EqualTo(1));
        Assert.That(gb[0].GetProperty("name").GetString(), Is.EqualTo("Tetris.gb"));
        Assert.That(gb[0].GetProperty("sizeBytes").GetInt64(), Is.EqualTo(4));
        Assert.That(gb[0].TryGetProperty("modifiedAt", out _), Is.True);
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
