using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §23 <c>POST /api/jobs/integrity</c>, <c>GET /api/integrity/status</c>, <c>GET /api/integrity/issues</c> (S37).</summary>
[Category("Integration")]
public class IntegrityEndpointsTests
{
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private async Task Start(string? quickCheckTime = null)
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        if (quickCheckTime is not null)
            _factory.Settings["Integrity:QuickCheckTime"] = quickCheckTime;
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", UserRole.Admin);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown()
    {
        _admin?.Dispose();
        _factory?.Dispose();
    }

    private async Task<Job> RunIntegrity(string mode)
    {
        using var response = await _admin.PostAsync("/api/jobs/integrity", new { mode });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var id = (await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid();
        var jobs = Service<IJobRepository>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var job = (await jobs.GetAsync(id, default))!;
            if (job.Status != JobStatus.Running || DateTime.UtcNow > deadline)
                return job;
            await Task.Delay(50);
        }
    }

    private static IEnumerable<TestCaseData> Endpoints()
    {
        yield return new TestCaseData("POST", "/api/jobs/integrity").SetName("RoleMatrix POST jobs/integrity");
        yield return new TestCaseData("GET", "/api/integrity/status").SetName("RoleMatrix GET integrity/status");
        yield return new TestCaseData("GET", "/api/integrity/issues").SetName("RoleMatrix GET integrity/issues");
    }

    [TestCaseSource(nameof(Endpoints))]
    public async Task RoleMatrix_AnonymousIs401_UserIs403(string method, string url)
    {
        await Start();
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var anonymousResponse = await anonymous.SendAsync(new HttpMethod(method), url, new { mode = "Quick" }, sendXsrf: true);
        using var userResponse = await user.SendAsync(new HttpMethod(method), url, new { mode = "Quick" }, sendXsrf: true);

        Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task StartQuickAndDeep_ThenStatusShowsTheLatestOfEach()
    {
        await Start();
        var quick = await RunIntegrity("Quick");
        var deep = await RunIntegrity("Deep");
        Assert.That((quick.Status, quick.Mode, quick.Trigger), Is.EqualTo((JobStatus.Completed, (JobMode?)JobMode.Quick, JobTrigger.Manual)));
        Assert.That((deep.Status, deep.Mode), Is.EqualTo((JobStatus.Completed, (JobMode?)JobMode.Deep)));

        using var response = await _admin.GetAsync("/api/integrity/status");

        var status = await AuthClient.JsonAsync(response);
        Assert.That(status.GetProperty("lastQuick").GetProperty("id").GetGuid(), Is.EqualTo(quick.Id));
        Assert.That(status.GetProperty("lastDeep").GetProperty("id").GetGuid(), Is.EqualTo(deep.Id));
        Assert.That(status.GetProperty("lastQuick").GetProperty("summary").GetProperty("zips").GetInt32(), Is.Zero);
        Assert.That(status.GetProperty("nextScheduledAt").ValueKind, Is.EqualTo(JsonValueKind.Null), "schedule disabled");
    }

    [Test]
    public async Task Status_BeforeAnyRun_AndWithASchedule()
    {
        await Start(quickCheckTime: "03:00");

        using var response = await _admin.GetAsync("/api/integrity/status");

        var status = await AuthClient.JsonAsync(response);
        Assert.That((status.GetProperty("lastQuick").ValueKind, status.GetProperty("lastDeep").ValueKind),
            Is.EqualTo((JsonValueKind.Null, JsonValueKind.Null)));
        var next = status.GetProperty("nextScheduledAt").GetDateTimeOffset();
        Assert.That(next, Is.GreaterThan(DateTimeOffset.UtcNow).And.LessThanOrEqualTo(DateTimeOffset.UtcNow.AddDays(1).AddHours(1)));
        Assert.That(next, Is.EqualTo(Service<Scheduler>().NextQuickCheckAt()));
    }

    [Test]
    public async Task Start_InvalidMode400_Busy409()
    {
        await Start();
        using (var invalid = await _admin.PostAsync("/api/jobs/integrity", new { mode = "Full" }))
        {
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await AuthClient.JsonAsync(invalid)).GetProperty("errors").TryGetProperty("mode", out _), Is.True);
        }

        using var held = Service<JobLock>().TryAcquire();
        using var busy = await _admin.PostAsync("/api/jobs/integrity", new { mode = "Quick" });
        Assert.That((busy.StatusCode, await AuthClient.CodeAsync(busy)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
    }

    [Test]
    public async Task Issues_ArePagedAndFiltered()
    {
        await Start();
        await File.WriteAllTextAsync(Path.Combine(_factory.Root, "roms", "a.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_factory.Root, "roms", "b.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_factory.Root, "roms", "gb", "T"));
        await File.WriteAllBytesAsync(Path.Combine(_factory.Root, "roms", "gb", "T", "Broken.zip"), [1, 2, 3]);
        await RunIntegrity("Quick");

        using var all = await _admin.GetAsync("/api/integrity/issues?pageSize=2");
        var page = await AuthClient.JsonAsync(all);
        Assert.That((page.GetProperty("total").GetInt64(), page.GetProperty("items").GetArrayLength()), Is.EqualTo((3L, 2)));

        using var orphans = await _admin.GetAsync($"/api/integrity/issues?status=Open&type={IntegrityIssueType.OrphanFile}");
        var items = (await AuthClient.JsonAsync(orphans)).GetProperty("items").EnumerateArray().ToList();
        Assert.That(items.Select(i => i.GetProperty("zipPath").GetString()), Is.EquivalentTo(new[] { "a.txt", "b.txt" }));
        Assert.That(items.All(i => i.GetProperty("severity").GetString() == "Warn" && i.GetProperty("status").GetString() == "Open"), Is.True);

        using var unreadable = await _admin.GetAsync("/api/integrity/issues?type=ZipUnreadable");
        var issue = (await AuthClient.JsonAsync(unreadable)).GetProperty("items")[0];
        Assert.That((issue.GetProperty("zipPath").GetString(), issue.GetProperty("system").GetString()), Is.EqualTo(("gb/T/Broken.zip", "gb")));

        foreach (var query in new[] { "status=Closed", "type=Nope", "pageSize=201" })
        {
            using var bad = await _admin.GetAsync("/api/integrity/issues?" + query);
            Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), query);
        }
    }
}
