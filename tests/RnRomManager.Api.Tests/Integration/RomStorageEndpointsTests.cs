using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>ROM Zip Config B6, B12: <c>GET /api/admin/rom-storage</c> and <c>PUT /api/admin/rom-storage/{systemId}</c>.</summary>
[Category("Integration")]
public class RomStorageEndpointsTests
{
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => SystemHandlerRegistration.AddSystemHandler<LegacyExternalTestHandler>(services),
        };
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
        _admin.SendAsync(HttpMethod.Put, $"/api/admin/rom-storage/{systemId}", body, true);

    private async Task<JsonElement> Row(string systemId)
    {
        using var response = await _admin.GetAsync("/api/admin/rom-storage");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await AuthClient.JsonAsync(response)).EnumerateArray().Single(r => r.GetProperty("id").GetString() == systemId);
    }

    private async Task<string?> Stored(string systemId) =>
        (await Service<ISettingsRepository>().GetAsync([RomStorageSettings.Key(systemId)], default)).GetValueOrDefault(RomStorageSettings.Key(systemId));

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

    private async Task<int> AuditRows()
    {
        await Service<AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_events WHERE action = 'RomStorageChanged'");
    }

    [Test]
    public async Task User_Is403()
    {
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var get = await user.GetAsync("/api/admin/rom-storage");
        using var put = await user.SendAsync(HttpMethod.Put, "/api/admin/rom-storage/nds", new { embedRoms = true }, true);

        Assert.That((get.StatusCode, put.StatusCode), Is.EqualTo((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)));
    }

    [Test]
    public async Task Get_ListsEverySystem_WithDefaultEffectiveConfigurableAndPendingGames()
    {
        var game = TestLibrary.Game("Old", TestLibrary.Rom("Old (USA).nds", TestLibrary.RomBytes(7))) with { System = LegacyExternalTestHandler.SystemId };
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, "legacy/O/Old.zip", 100, DateTime.UtcNow), default);

        using var response = await _admin.GetAsync("/api/admin/rom-storage");
        var rows = (await AuthClient.JsonAsync(response)).EnumerateArray().ToList();

        Assert.That(rows.Select(r => r.GetProperty("id").GetString()),
            Is.EquivalentTo(Service<ISystemRegistry>().All.Select(h => h.Id)));
        Assert.That(rows.Select(r => r.GetProperty("displayName").GetString()), Is.Ordered.Using((IComparer<string?>)StringComparer.OrdinalIgnoreCase));
        static (bool, bool, bool, int) Values(JsonElement r) => (r.GetProperty("defaultEmbedRoms").GetBoolean(), r.GetProperty("embedRoms").GetBoolean(),
            r.GetProperty("configurable").GetBoolean(), r.GetProperty("pendingGames").GetInt32());
        Assert.That(Values(rows.Single(r => r.GetProperty("id").GetString() == "nds")), Is.EqualTo((false, false, true, 0)));
        Assert.That(Values(rows.Single(r => r.GetProperty("id").GetString() == "gba")), Is.EqualTo((true, true, false, 0)));
        Assert.That(Values(rows.Single(r => r.GetProperty("id").GetString() == "switch")), Is.EqualTo((false, false, false, 0)));
        Assert.That(Values(rows.Single(r => r.GetProperty("id").GetString() == LegacyExternalTestHandler.SystemId)), Is.EqualTo((false, false, true, 1)));
        static (string?, bool) Compression(JsonElement r) => (r.GetProperty("zipCompression").GetString(), r.GetProperty("compressionConfigurable").GetBoolean());
        Assert.That(Compression(rows.Single(r => r.GetProperty("id").GetString() == "gba")), Is.EqualTo(("Med", true)), "ZIP Compression Z6");
        Assert.That(Compression(rows.Single(r => r.GetProperty("id").GetString() == "nds")), Is.EqualTo(("Med", false)));
        Assert.That(Compression(rows.Single(r => r.GetProperty("id").GetString() == "switch")), Is.EqualTo(("Med", false)));
        int GameCount(string id) => rows.Single(r => r.GetProperty("id").GetString() == id).GetProperty("gameCount").GetInt32();
        Assert.That((GameCount(LegacyExternalTestHandler.SystemId), GameCount("gba")), Is.EqualTo((1, 0)), "Z1: 0 when a system has no games");
    }

    [Test]
    public async Task Put_UnknownSystem_Is404()
    {
        using var response = await Put("nope", new { embedRoms = true });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [TestCase(null, TestName = "Put_BadBody_Is400 missing body")]
    [TestCase("yes", TestName = "Put_BadBody_Is400 non-boolean")]
    public async Task Put_BadBody_Is400(string? value)
    {
        using var response = await Put("nds", value is null ? null : new { embedRoms = value });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("embedRoms", out _), Is.True);
    }

    [Test]
    public async Task Put_NotConfigurable_Is400()
    {
        using var response = await Put("gba", new { embedRoms = false });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("code").GetString(), Is.EqualTo("NotConfigurable"));
    }

    [Test]
    public async Task Put_TheEffectiveValue_Is204_AndSavesNothing()
    {
        using var response = await Put("nds", new { embedRoms = false });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await Stored("nds"), Is.Null);
        Assert.That(await Service<IJobRepository>().GetLatestAsync(JobType.StorageMigration, null, default), Is.Null, "no job started");
    }

    [Test]
    public async Task Put_AChange_Is202_TheJobPersistsTheKey_AndAudits()
    {
        using var response = await Put("nds", new { embedRoms = true });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var job = await WaitForEnd((await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid());
        Assert.That((job.Type, job.Status, job.Trigger), Is.EqualTo((JobType.StorageMigration, JobStatus.Completed, JobTrigger.Manual)));
        Assert.That(await Stored("nds"), Is.EqualTo("true"));
        Assert.That((await Row("nds")).GetProperty("embedRoms").GetBoolean(), Is.True);
        Assert.That(await AuditRows(), Is.EqualTo(1));
    }

    [Test]
    public async Task Put_WhileAJobRuns_Is409_AndSavesNothing()
    {
        using (Service<JobLock>().TryAcquire())
        {
            using var response = await Put("nds", new { embedRoms = true });

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That((await AuthClient.JsonAsync(response)).GetProperty("code").GetString(), Is.EqualTo("JobRunning"));
        }
        Assert.That(await Stored("nds"), Is.Null);
        Assert.That((await Row("nds")).GetProperty("embedRoms").GetBoolean(), Is.False);
        Assert.That(await AuditRows(), Is.Zero);
    }
}
