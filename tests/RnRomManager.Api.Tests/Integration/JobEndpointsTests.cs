using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §23 <c>/api/jobs*</c> (read + cancel) and §7.4 crash recovery at startup, against MariaDB.</summary>
[Category("Integration")]
public class JobEndpointsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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

    private JobRunner Runner => _factory.Services.GetRequiredService<JobRunner>();

    private async Task<(NoopTestJob Job, StartedJob Started)> StartNoopAsync()
    {
        var job = new NoopTestJob();
        var started = await Runner.StartAsync(job, JobTrigger.Manual, null, CancellationToken.None);
        await job.Started.Task.WaitAsync(Timeout);
        return (job, started!);
    }

    private static IEnumerable<TestCaseData> Endpoints()
    {
        var id = Guid.NewGuid();
        yield return new TestCaseData("GET", "/api/jobs/current").SetName("RoleMatrix GET jobs/current");
        yield return new TestCaseData("GET", "/api/jobs").SetName("RoleMatrix GET jobs");
        yield return new TestCaseData("GET", $"/api/jobs/{id}").SetName("RoleMatrix GET job");
        yield return new TestCaseData("GET", $"/api/jobs/{id}/items").SetName("RoleMatrix GET job items");
        yield return new TestCaseData("POST", $"/api/jobs/{id}/cancel").SetName("RoleMatrix POST cancel");
    }

    [TestCaseSource(nameof(Endpoints))]
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
    public async Task Current_WhenIdle_Is204()
    {
        using var response = await _admin.GetAsync("/api/jobs/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task Current_WhileRunning_ShowsProgress_ThenCancelEndsItCancelled()
    {
        var (_, started) = await StartNoopAsync();

        using (var current = await _admin.GetAsync("/api/jobs/current"))
        {
            var json = await AuthClient.JsonAsync(current);
            Assert.That(json.GetProperty("id").GetGuid(), Is.EqualTo(started.Id));
            Assert.That(json.GetProperty("type").GetString(), Is.EqualTo("Integrity"));
            Assert.That(json.GetProperty("mode").GetString(), Is.EqualTo("Quick"));
            Assert.That(json.GetProperty("status").GetString(), Is.EqualTo("Running"));
            Assert.That(json.GetProperty("total").GetInt32(), Is.EqualTo(3));
            Assert.That(json.GetProperty("currentItem").GetString(), Is.EqualTo("item-1"));
        }

        using (var cancel = await _admin.PostAsync($"/api/jobs/{started.Id}/cancel"))
            Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await started.Completion.WaitAsync(Timeout);

        using var job = await _admin.GetAsync($"/api/jobs/{started.Id}");
        var row = await AuthClient.JsonAsync(job);
        Assert.That(row.GetProperty("status").GetString(), Is.EqualTo("Cancelled"));
        Assert.That(row.GetProperty("triggerSource").GetString(), Is.EqualTo("Manual"));
        Assert.That(row.GetProperty("finishedAt").ValueKind, Is.EqualTo(JsonValueKind.String));
    }

    [Test]
    public async Task Cancel_WhenNotRunning_Is404()
    {
        using var response = await _admin.PostAsync($"/api/jobs/{Guid.NewGuid()}/cancel");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task Completed_JobListedWithSummary_AndItemsPage()
    {
        var (job, started) = await StartNoopAsync();
        job.Release.SetResult();
        await started.Completion.WaitAsync(Timeout);
        var items = _factory.Services.GetRequiredService<IJobItemRepository>();
        await items.InsertAsync(new JobItem
        {
            JobId = started.Id, FileId = Guid.NewGuid(), SourcePath = "/app/ingest/gb/a.gb", Outcome = "Ingested", CreatedAt = DateTime.UtcNow,
        }, CancellationToken.None);

        using var list = await _admin.GetAsync("/api/jobs?type=Integrity&page=1&pageSize=10");
        var page = await AuthClient.JsonAsync(list);
        Assert.That(page.GetProperty("total").GetInt64(), Is.EqualTo(1));
        Assert.That(page.GetProperty("page").GetInt32(), Is.EqualTo(1));
        Assert.That(page.GetProperty("pageSize").GetInt32(), Is.EqualTo(10));
        var listed = page.GetProperty("items")[0];
        Assert.That(listed.GetProperty("status").GetString(), Is.EqualTo("Completed"));
        Assert.That(listed.GetProperty("summary").GetProperty("done").GetInt32(), Is.EqualTo(1));

        using var itemPage = await _admin.GetAsync($"/api/jobs/{started.Id}/items?outcome=Ingested");
        var itemsJson = await AuthClient.JsonAsync(itemPage);
        Assert.That(itemsJson.GetProperty("total").GetInt64(), Is.EqualTo(1));
        Assert.That(itemsJson.GetProperty("items")[0].GetProperty("sourcePath").GetString(), Is.EqualTo("/app/ingest/gb/a.gb"));
    }

    [TestCase("/api/jobs?type=Nope", "type")]
    [TestCase("/api/jobs?page=0", "page")]
    [TestCase("/api/jobs?pageSize=201", "pageSize")]
    public async Task List_BadQuery_Is400(string url, string field)
    {
        using var response = await _admin.GetAsync(url);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }

    [TestCase("")]
    [TestCase("/items")]
    public async Task UnknownJob_Is404(string suffix)
    {
        using var response = await _admin.GetAsync($"/api/jobs/{Guid.NewGuid()}{suffix}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Startup_AbortsJobsLeftRunning()
    {
        var jobs = _factory.Services.GetRequiredService<IJobRepository>();
        var orphan = new Job { Id = Guid.NewGuid(), Type = JobType.Ingest, Trigger = JobTrigger.Manual, Status = JobStatus.Running, StartedAt = DateTime.UtcNow };
        await jobs.InsertAsync(orphan, CancellationToken.None);

        // A second host on the same database = the restart after a crash.
        using var restarted = new ApiFactory(_factory.Settings["ConnectionStrings:Default"]);
        _ = restarted.Services;

        Assert.That((await jobs.GetAsync(orphan.Id, CancellationToken.None))!.Status, Is.EqualTo(JobStatus.Aborted));
    }
}
