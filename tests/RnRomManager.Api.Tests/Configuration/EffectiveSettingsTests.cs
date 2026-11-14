using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Configuration;

namespace RnRomManager.Api.Tests.Configuration;

public class EffectiveSettingsTests
{
    [TestCase("Server=db;Database=rnrommanager;User ID=app;Password=hunter2")]
    [TestCase("Server=db;Database=rnrommanager;User ID=app;pwd=hunter2")]
    [TestCase("server=db;database=rnrommanager;uid=app;PASSWORD='hun;ter2'")]
    public void RedactConnectionString_RemovesPassword(string connectionString)
    {
        var redacted = EffectiveSettings.RedactConnectionString(connectionString);

        Assert.That(redacted, Does.Not.Contain("hunter2").And.Not.Contain("hun;ter2").IgnoreCase);
        Assert.That(redacted, Does.Not.Contain("password").IgnoreCase.And.Not.Contain("pwd").IgnoreCase);
        Assert.That(redacted, Does.Contain("Database=rnrommanager"));
    }

    [TestCase("x")]
    [TestCase("not;a=valid;;=connection string;Password=hunter2;bogus")]
    public void RedactConnectionString_Unparseable_NeverEchoesInput(string connectionString) =>
        Assert.That(EffectiveSettings.RedactConnectionString(connectionString), Is.EqualTo("(unparseable)"));

    [Test]
    public void From_IncludesBackupRestoreFrom()
    {
        var root = Path.Combine(Path.GetTempPath(), "rnrm-effective");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=db;Password=hunter2",
            ["Paths:Roms"] = Path.Combine(root, "roms"),
            ["Paths:Ingest"] = Path.Combine(root, "ingest"),
            ["Paths:Logs"] = Path.Combine(root, "logs"),
            ["Paths:Cache"] = Path.Combine(root, "cache"),
            ["Paths:Backups"] = Path.Combine(root, "backups"),
            ["Paths:Collections"] = Path.Combine(root, "collections"),
            ["Paths:Bios"] = Path.Combine(root, "bios"),
            ["Backup:RestoreFrom"] = "rnrommanager-20261106-033000.zip",
            ["Emulator:GenesisCorePath"] = Path.Combine(root, "genesis"),
        }).Build();
        using var services = new ServiceCollection().AddAppOptions(config).BuildServiceProvider();

        var settings = EffectiveSettings.From(services);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.That(settings.Backup.RestoreFrom, Is.EqualTo("rnrommanager-20261106-033000.zip"));
        Assert.That(json, Does.Contain("\"backup\":{\"restoreFrom\":\"rnrommanager-20261106-033000.zip\"}"));
        Assert.That(json, Does.Contain("\"backups\":"));
        Assert.That(settings.Paths.Bios, Is.EqualTo(Path.Combine(root, "bios")));
        Assert.That(json, Does.Contain($"\"bios\":{JsonSerializer.Serialize(Path.Combine(root, "bios"))}"));
        Assert.That(settings.Emulator.GenesisCorePath, Is.EqualTo(Path.Combine(root, "genesis")));
        Assert.That(json, Does.Contain($"\"emulator\":{{\"genesisCorePath\":{JsonSerializer.Serialize(Path.Combine(root, "genesis"))}}}"));
        Assert.That(json, Does.Not.Contain("hunter2"));
    }
}
