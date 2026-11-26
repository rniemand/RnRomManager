using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>ZIP Compression Z7–Z8: <c>PUT /api/admin/zip-compression/{systemId}</c> and <c>POST …/repack</c>.</summary>
[Category("Integration")]
public class ZipCompressionEndpointsTests
{
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

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
    }

    [TearDown]
    public void TearDown() => _admin.Dispose();

    private Task<HttpResponseMessage> Put(string systemId, object? body) =>
        _admin.SendAsync(HttpMethod.Put, $"/api/admin/zip-compression/{systemId}", body, true);

    private Task<HttpResponseMessage> Repack(string systemId) =>
        _admin.SendAsync(HttpMethod.Post, $"/api/admin/zip-compression/{systemId}/repack", null, true);

    private async Task<string?> Stored(string systemId) =>
        (await Service<ISettingsRepository>().GetAsync([RomStorageSettings.CompressionKey(systemId)], default))
            .GetValueOrDefault(RomStorageSettings.CompressionKey(systemId));

    private async Task<Job> WaitForEnd(Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var job = (await Service<IJobRepository>().GetAsync(id, default))!;
            if (job.Status != JobStatus.Running || DateTime.UtcNow > deadline)
                return job;
            await Task.Delay(50);
        }
    }

    private async Task<List<string>> AuditDetails()
    {
        await Service<AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return [.. await db.QueryAsync<string>("SELECT details FROM audit_events WHERE action = 'ZipCompressionChanged' AND system_id = 'gba'")];
    }

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await AuthClient.JsonAsync(response)).GetProperty("code").GetString();

    [Test]
    public async Task User_Is403()
    {
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var put = await user.SendAsync(HttpMethod.Put, "/api/admin/zip-compression/gba", new { level = "Store" }, true);
        using var post = await user.SendAsync(HttpMethod.Post, "/api/admin/zip-compression/gba/repack", null, true);

        Assert.That((put.StatusCode, post.StatusCode), Is.EqualTo((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)));
    }

    [Test]
    public async Task UnknownSystem_Is404()
    {
        using var put = await Put("nope", new { level = "Store" });
        using var post = await Repack("nope");

        Assert.That((put.StatusCode, post.StatusCode), Is.EqualTo((HttpStatusCode.NotFound, HttpStatusCode.NotFound)));
    }

    [TestCase(null, TestName = "Put_BadLevel_Is400 missing body")]
    [TestCase("med", TestName = "Put_BadLevel_Is400 wrong case")]
    [TestCase("2", TestName = "Put_BadLevel_Is400 number text")]
    [TestCase("Fast", TestName = "Put_BadLevel_Is400 unknown")]
    public async Task Put_BadLevel_Is400(string? level)
    {
        using var response = await Put("gba", level is null ? null : new { level });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var errors = (await AuthClient.JsonAsync(response)).GetProperty("errors");
        Assert.That(errors.GetProperty("level")[0].GetString(), Is.EqualTo("Must be Store, Med or High."));
    }

    [Test]
    public async Task Put_NonStringLevel_Is400()
    {
        using var response = await Put("gba", new { level = 1 });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task BesideZipSystem_IsNotConfigurable()
    {
        using var put = await Put("nds", new { level = "Store" });
        using var post = await Repack("nds");

        Assert.That((put.StatusCode, post.StatusCode), Is.EqualTo((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest)));
        Assert.That((await Code(put), await Code(post)), Is.EqualTo(("NotConfigurable", "NotConfigurable")));
        Assert.That(await Stored("nds"), Is.Null);
    }

    [Test]
    public async Task Put_TheCurrentValue_Is204_AndSavesNothing()
    {
        using var response = await Put("gba", new { level = "Med" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await Stored("gba"), Is.Null);
        Assert.That(await Service<IJobRepository>().GetLatestAsync(JobType.ZipRepack, null, default), Is.Null, "no job started");
    }

    [Test]
    public async Task Put_AChange_Is202_TheJobPersistsTheKey_AndAudits()
    {
        using var response = await Put("gba", new { level = "Store" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var job = await WaitForEnd((await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid());
        Assert.That((job.Type, job.Status, job.Trigger), Is.EqualTo((JobType.ZipRepack, JobStatus.Completed, JobTrigger.Manual)));
        Assert.That(await Stored("gba"), Is.EqualTo("Store"));
        Assert.That(await AuditDetails(), Is.EqualTo(new[] { "Med → Store" }));
    }

    [Test]
    public async Task Put_WhileAJobRuns_Is409_AndSavesNothing()
    {
        using (Service<JobLock>().TryAcquire("test"))
        {
            using var response = await Put("gba", new { level = "Store" });

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await Code(response), Is.EqualTo("JobRunning"));
        }
        Assert.That(await Stored("gba"), Is.Null);
        Assert.That(await AuditDetails(), Is.Empty);
    }

    [Test]
    public async Task Repack_Is202_SavesAndAuditsNothing()
    {
        using var response = await Repack("gba");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var job = await WaitForEnd((await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid());
        Assert.That((job.Type, job.Status), Is.EqualTo((JobType.ZipRepack, JobStatus.Completed)));
        Assert.That(await Stored("gba"), Is.Null);
        Assert.That(await AuditDetails(), Is.Empty);
    }

    [Test]
    public async Task Repack_WhileAJobRuns_Is409()
    {
        using (Service<JobLock>().TryAcquire("test"))
        {
            using var response = await Repack("gba");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await Code(response), Is.EqualTo("JobRunning"));
        }
    }
}
