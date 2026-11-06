using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §23 <c>/api/admin/settings</c>, <c>/api/admin/logs</c> (§13) and <c>/api/about</c> (§10.4) (S38).</summary>
[Category("Integration")]
public class AdminEndpointsTests
{
    private const string InitialPassword = "initial-admin-pass-123";

    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = await MariaDbServer.CreateDatabaseAsync();
        _factory = new ApiFactory(connectionString);
        _factory.Settings["Admin:InitialPassword"] = InitialPassword;
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

    private string RealDbPassword => new MySqlConnector.MySqlConnectionStringBuilder(_factory.Settings["ConnectionStrings:Default"]!).Password;

    [Test]
    public async Task Settings_AreTheEffectiveConfig_WithSecretsMasked()
    {
        using var response = await _admin.GetAsync("/api/admin/settings");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var text = await response.Content.ReadAsStringAsync();
        Assert.That(text, Does.Not.Contain(RealDbPassword).And.Not.Contain(InitialPassword));
        var settings = JsonDocument.Parse(text).RootElement;
        Assert.That(settings.GetProperty("connectionString").GetString(), Does.Not.Contain("password").IgnoreCase.And.Contain("Database="));
        Assert.That(settings.GetProperty("adminInitialPassword").GetString(), Is.EqualTo("***"));
        Assert.That(settings.GetProperty("paths").GetProperty("logs").GetString(), Is.EqualTo(Path.Combine(_factory.Root, "logs")));
        Assert.That(settings.GetProperty("integrity").GetProperty("quickCheckTime").GetString(), Is.Empty);
        Assert.That(settings.GetProperty("logging").GetProperty("retentionDays").GetInt32(), Is.EqualTo(14));
    }

    [Test]
    public async Task Logs_ZipHasEveryLogPlusSettingsAndVersion_WhileTodaysFileIsOpen()
    {
        var logs = Path.Combine(_factory.Root, "logs");
        await File.WriteAllTextAsync(Path.Combine(logs, "rnrommanager-2026-11-01.log"), "old day\n");
        // Like NLog's keepFileOpen: a writer holds the file open and lets others only read it.
        var held = Path.Combine(logs, "rnrommanager-held.log");
        await using var writer = new FileStream(held, FileMode.Create, FileAccess.Write, FileShare.Read);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("still being written\n"));
        await writer.FlushAsync();

        using var response = await _admin.GetAsync("/api/admin/logs");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar, Does.StartWith("rnrommanager-logs-").And.EndWith(".zip"));
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.That(names, Does.Contain("rnrommanager-2026-11-01.log").And.Contain("rnrommanager-held.log")
            .And.Contain("settings.json").And.Contain("version.txt"));
        // NLog itself keeps today's file open (keepFileOpen) in this host.
        Assert.That(names, Does.Contain($"rnrommanager-{DateTime.UtcNow:yyyy-MM-dd}.log"), "today's NLog file is included");
        Assert.That(Read("rnrommanager-held.log"), Is.EqualTo("still being written\n"));
        Assert.That(Read("rnrommanager-2026-11-01.log"), Is.EqualTo("old day\n"));
        var settings = Read("settings.json");
        Assert.That(settings, Does.Not.Contain(RealDbPassword).And.Not.Contain(InitialPassword).And.Contain("\"adminInitialPassword\": \"***\""));
        Assert.That(Read("version.txt").Trim(), Is.EqualTo(StartupRunner.Version));
        Assert.That(Directory.EnumerateFiles(Path.Combine(_factory.Root, "cache", "work")), Is.Empty, "the temp ZIP is deleted");
    }

    [Test]
    public async Task About_ForAnyUser_HasVersionAndLicences()
    {
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var response = await user.GetAsync("/api/about");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var about = await AuthClient.JsonAsync(response);
        Assert.That(about.GetProperty("version").GetString(), Is.EqualTo(StartupRunner.Version));
        var licenses = about.GetProperty("licenses").EnumerateArray().ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("license").GetString());
        Assert.That(licenses["libretro-database"], Is.EqualTo("CC-BY-SA-4.0"));
        Assert.That(licenses["RnRomManager"], Is.EqualTo("MIT"));
    }
}
