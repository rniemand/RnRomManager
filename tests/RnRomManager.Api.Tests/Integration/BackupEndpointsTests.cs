using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using NSubstitute;
using RnRomManager.Core.Backups;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B15–B17: <c>/api/admin/backups</c> against MariaDB (real dumps unless a test blocks the store).</summary>
[Category("Integration")]
public class BackupEndpointsTests
{
    private ApiFactory? _factory;
    private AuthClient? _admin;
    private TaskCompletionSource? _block;

    private ApiFactory Factory => _factory!;
    private AuthClient Admin => _admin!;
    private string Folder => Path.Combine(Factory.Root, "backups");

    private async Task StartAsync(bool blockingStore = false)
    {
        _block = blockingStore ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        var connectionString = await MariaDbServer.CreateDatabaseAsync();
        _factory = new ApiFactory(connectionString)
        {
            TestServices = services =>
            {
                if (!blockingStore) return;
                var store = Substitute.For<IBackupStore>();
                store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                    .Returns(async call =>
                    {
                        await _block!.Task;
                        return new BackupManifest(1, call.Arg<Guid>(), call.Arg<DateTimeOffset>(), "x", "db", [], []);
                    });
                services.AddSingleton(store);
            },
        };
        _admin = await AuthClient.CreateAsync(Factory);
        await TestUsers.CreateAsync(Factory, "boss", TestUsers.AllRoles);
        (await Admin.LoginAsync("boss", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown()
    {
        _block?.TrySetResult();
        _admin?.Dispose();
        _factory?.Dispose();
    }

    private async Task<JsonElement> GetStatusAsync()
    {
        using var response = await Admin.GetAsync("/api/admin/backups");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await AuthClient.JsonAsync(response);
    }

    private Task<HttpResponseMessage> PutSettingsAsync(object body) =>
        Admin.SendAsync(HttpMethod.Put, "/api/admin/backups/settings", body, sendXsrf: true);

    [Test]
    public async Task Get_ReturnsTheDefaults_TimeZone_AndNoBackups()
    {
        await StartAsync();

        var status = await GetStatusAsync();

        Assert.That(status.GetProperty("enabled").GetBoolean(), Is.True);
        Assert.That(status.GetProperty("time").GetString(), Is.EqualTo("03:30"));
        Assert.That(status.GetProperty("retention").GetInt32(), Is.EqualTo(30));
        Assert.That(status.GetProperty("timeZone").GetString(), Is.EqualTo(TimeZoneInfo.Local.Id));
        Assert.That(status.GetProperty("nextRunAt").GetDateTimeOffset(), Is.GreaterThan(DateTimeOffset.UtcNow));
        Assert.That(status.GetProperty("running").GetBoolean(), Is.False);
        Assert.That(status.GetProperty("lastResult").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(status.GetProperty("backups").GetArrayLength(), Is.Zero);
    }

    [TestCase("""{"enabled":true,"time":"24:00","retention":30}""", "time")]
    [TestCase("""{"enabled":true,"time":"3:30","retention":30}""", "time")]
    [TestCase("""{"enabled":true,"retention":30}""", "time")]
    [TestCase("""{"enabled":true,"time":"03:30","retention":0}""", "retention")]
    [TestCase("""{"enabled":true,"time":"03:30","retention":366}""", "retention")]
    [TestCase("""{"enabled":true,"time":"03:30","retention":"30"}""", "retention")]
    [TestCase("""{"enabled":true,"time":"03:30"}""", "retention")]
    [TestCase("""{"time":"03:30","retention":30}""", "enabled")]
    public async Task Put_Invalid_Is400WithTheFieldError(string json, string field)
    {
        await StartAsync();

        using var response = await PutSettingsAsync(JsonDocument.Parse(json).RootElement);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var errors = (await AuthClient.JsonAsync(response)).GetProperty("errors");
        Assert.That(errors.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { field }));
        Assert.That((await GetStatusAsync()).GetProperty("time").GetString(), Is.EqualTo("03:30"), "nothing saved");
    }

    [Test]
    public async Task Put_PersistsAndReturnsTheStatus()
    {
        await StartAsync();

        using var response = await PutSettingsAsync(new { enabled = false, time = "22:15", retention = 7 });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);
        Assert.That((body.GetProperty("enabled").GetBoolean(), body.GetProperty("time").GetString(), body.GetProperty("retention").GetInt32()),
            Is.EqualTo((false, "22:15", 7)));
        Assert.That(body.GetProperty("nextRunAt").ValueKind, Is.EqualTo(JsonValueKind.Null));
        var again = await GetStatusAsync();
        Assert.That((again.GetProperty("enabled").GetBoolean(), again.GetProperty("time").GetString(), again.GetProperty("retention").GetInt32()),
            Is.EqualTo((false, "22:15", 7)));
    }

    [Test]
    public async Task Post_WritesABackup_ListsIt_AndSetsTheLastResult()
    {
        await StartAsync();

        using var response = await Admin.PostAsync("/api/admin/backups");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var created = await AuthClient.JsonAsync(response);
        var fileName = created.GetProperty("fileName").GetString()!;
        Assert.That(BackupNames.IsBackup(fileName), Is.True, fileName);
        Assert.That(File.Exists(Path.Combine(Folder, fileName)), Is.True);
        Assert.That(created.GetProperty("sizeBytes").GetInt64(), Is.EqualTo(new FileInfo(Path.Combine(Folder, fileName)).Length));

        var status = await GetStatusAsync();
        var listed = status.GetProperty("backups").EnumerateArray().Single();
        Assert.That(listed.GetProperty("fileName").GetString(), Is.EqualTo(fileName));
        var last = status.GetProperty("lastResult");
        Assert.That((last.GetProperty("ok").GetBoolean(), last.GetProperty("fileName").GetString()), Is.EqualTo((true, fileName)));
    }

    [Test]
    public async Task Post_WhileOneIsRunning_Is409BackupRunning()
    {
        await StartAsync(blockingStore: true);

        var first = Admin.PostAsync("/api/admin/backups");
        await WaitUntilRunningAsync();
        using (var second = await Admin.PostAsync("/api/admin/backups"))
        {
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(second), Is.EqualTo("BackupRunning"));
        }

        Assert.That((await GetStatusAsync()).GetProperty("running").GetBoolean(), Is.True);
        _block!.SetResult();
        using var done = await first;
        Assert.That(done.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    [Test]
    public async Task Post_Failure_Is500BackupFailed_AndRecordsTheError()
    {
        await StartAsync(blockingStore: true);
        _block!.SetException(new InvalidOperationException("disk on fire"));

        using var response = await Admin.PostAsync("/api/admin/backups");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("BackupFailed"));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("disk on fire"));
        var last = (await GetStatusAsync()).GetProperty("lastResult");
        Assert.That((last.GetProperty("ok").GetBoolean(), last.GetProperty("error").GetString()), Is.EqualTo((false, "disk on fire")));
        Assert.That(Directory.GetFiles(Folder), Is.Empty);
    }

    [Test]
    public async Task NonAdmin_Is403_AndWritesNothing()
    {
        await StartAsync();
        using var user = await AuthClient.CreateAsync(Factory);
        await TestUsers.CreateAsync(Factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var get = await user.GetAsync("/api/admin/backups");
        using var post = await user.PostAsync("/api/admin/backups");

        Assert.That((get.StatusCode, post.StatusCode), Is.EqualTo((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)));
        Assert.That(Directory.GetFiles(Folder), Is.Empty);
    }

    // --- A3 download ---

    [Test]
    public async Task Download_StreamsTheZipAsAnAttachment_AndAudits()
    {
        await StartAsync();
        const string name = "rnrommanager-20261127-120000.zip";
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Path.Combine(Folder, name), [80, 75, 5, 6]);

        using var response = await Admin.GetAsync($"/api/admin/backups/{name}/download");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        Assert.That(response.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
        Assert.That(response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), Is.EqualTo(name));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 80, 75, 5, 6 }));
        await Factory.Services.GetRequiredService<AuditWriter>().FlushAsync();
        await using var db = Factory.Services.GetRequiredService<IDbConnectionFactory>().Create();
        Assert.That(await db.QueryAsync<string>("SELECT details FROM audit_events WHERE action = 'BackupDownloaded'"),
            Is.EqualTo(new[] { name }));
    }

    [TestCase("notes.txt")]
    [TestCase("rnrommanager-2026.zip")]
    [TestCase("..%2Frnrommanager-20261127-120000.zip")]
    public async Task Download_ABadName_Is400(string fileName)
    {
        await StartAsync();

        using var response = await Admin.GetAsync($"/api/admin/backups/{fileName}/download");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").GetProperty("fileName")[0].GetString(),
            Is.EqualTo("Choose a backup from the list."));
    }

    [Test]
    public async Task Download_AMissingBackup_Is404_AndAUserIs403()
    {
        await StartAsync();
        const string url = "/api/admin/backups/rnrommanager-20261127-120000.zip/download";
        using var user = await AuthClient.CreateAsync(Factory);
        await TestUsers.CreateAsync(Factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var missing = await Admin.GetAsync(url);
        using var forbidden = await user.GetAsync(url);

        Assert.That((missing.StatusCode, forbidden.StatusCode), Is.EqualTo((HttpStatusCode.NotFound, HttpStatusCode.Forbidden)));
    }

    // --- B27 live restore ---

    private Task<HttpResponseMessage> RestoreAsync(AuthClient client, string fileName) =>
        client.PostAsync($"/api/admin/backups/{Uri.EscapeDataString(fileName)}/restore");

    private async Task<string> BackupNowAsync()
    {
        using var response = await Admin.PostAsync("/api/admin/backups");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        return (await AuthClient.JsonAsync(response)).GetProperty("fileName").GetString()!;
    }

    private async Task<List<string>> ColumnAsync(string sql)
    {
        await using var db = Factory.Services.GetRequiredService<RnRomManager.Core.Database.IDbConnectionFactory>().Create();
        return (await Dapper.SqlMapper.QueryAsync<string>(db, sql)).ToList();
    }

    [Test]
    public async Task Restore_BringsBackTheEarlierRows_EndsEverySession_AndIsAudited()
    {
        await StartAsync();
        // A job that was Running when the backup was taken comes back with it and must be aborted.
        await using (var db = Factory.Services.GetRequiredService<RnRomManager.Core.Database.IDbConnectionFactory>().Create())
            await Dapper.SqlMapper.ExecuteAsync(db, "INSERT INTO jobs (id, type, trigger_source, status, started_at) " +
                "VALUES (UUID(), 'Integrity', 'Manual', 'Running', UTC_TIMESTAMP(6))");
        var earlier = await BackupNowAsync();
        await TestUsers.CreateAsync(Factory, "later");
        using var other = await AuthClient.CreateAsync(Factory);
        (await other.LoginAsync("later", TestUsers.Password)).Dispose();

        using var response = await RestoreAsync(Admin, earlier);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);
        var safety = body.GetProperty("safetyBackup").GetString()!;
        Assert.That(body.GetProperty("restored").GetString(), Is.EqualTo(earlier));
        Assert.That(File.Exists(Path.Combine(Folder, safety)) && File.Exists(Path.Combine(Folder, earlier)), Is.True, "safety backup written, source kept");
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.That(cookies.Any(c => c.StartsWith("rnrm.auth=;", StringComparison.Ordinal)), Is.True, "the auth cookie is cleared");
        Assert.That(cookies.Any(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)), Is.True, "a fresh anonymous antiforgery token");

        Assert.That(await ColumnAsync("SELECT username FROM users ORDER BY username"), Is.EqualTo(new[] { "admin", "boss" }), "the earlier rows are back (the seeded admin included)");
        Assert.That(await ColumnAsync("SELECT status FROM jobs"), Is.EqualTo(new[] { "Aborted" }), "no job is left Running");
        using (var me = await other.GetAsync("/api/auth/me"))
            Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "every other session ends");
        using (var me = await Admin.GetAsync("/api/auth/me"))
            Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "the caller is signed out");

        await Factory.Services.GetRequiredService<AuditWriter>().FlushAsync();
        Assert.That(await ColumnAsync("SELECT CONCAT(action, ' | ', username, ' | ', details) FROM audit_events ORDER BY id DESC LIMIT 1"),
            Is.EqualTo(new[] { $"BackupRestored | boss | {earlier}; safety backup {safety}" }));
    }

    [Test]
    public async Task Restore_Refusals()
    {
        await StartAsync();
        var earlier = await BackupNowAsync();

        using (var missing = await RestoreAsync(Admin, "rnrommanager-20200101-000000.zip"))
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        foreach (var bad in new[] { "../" + earlier, earlier + ".tmp", "notes.zip" })
        {
            using var response = await RestoreAsync(Admin, bad);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), bad);
            Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("fileName", out _), Is.True, bad);
        }

        using (Factory.Services.GetRequiredService<RnRomManager.Core.Jobs.JobLock>().TryAcquire("test"))
        using (var busy = await RestoreAsync(Admin, earlier))
        {
            Assert.That(busy.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(busy), Is.EqualTo("JobRunning"));
        }

        using var user = await AuthClient.CreateAsync(Factory);
        await TestUsers.CreateAsync(Factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();
        using (var forbidden = await RestoreAsync(user, earlier))
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await ColumnAsync("SELECT username FROM users WHERE username = 'plain'"), Is.EqualTo(new[] { "plain" }), "nothing was restored");
    }

    [Test]
    public async Task Restore_WhileABackupRuns_Is409BackupRunning()
    {
        await StartAsync(blockingStore: true);
        File.WriteAllText(Path.Combine(Folder, "rnrommanager-20200101-000000.zip"), "x");
        var first = Admin.PostAsync("/api/admin/backups");
        await WaitUntilRunningAsync();

        using (var response = await RestoreAsync(Admin, "rnrommanager-20200101-000000.zip"))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("BackupRunning"));
        }

        _block!.SetResult();
        (await first).Dispose();
    }

    private async Task WaitUntilRunningAsync()
    {
        var service = Factory.Services.GetRequiredService<BackupService>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!service.IsRunning)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("the backup never started");
            await Task.Delay(10);
        }
    }
}
