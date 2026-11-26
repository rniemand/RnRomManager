using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Configuration;
using RnRomManager.Core.Configuration;

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
            ["Paths:ImageCache"] = Path.Combine(root, "imagecache"),
            ["Backup:RestoreFrom"] = "rnrommanager-20261106-033000.zip",
            ["Emulator:GenesisCorePath"] = Path.Combine(root, "genesis"),
            ["Emulator:PicodriveCorePath"] = Path.Combine(root, "picodrive"),
            ["Emulator:Snes9xCorePath"] = Path.Combine(root, "snes9x"),
            ["Emulator:OperaCorePath"] = Path.Combine(root, "opera"),
            ["Emulator:ViceCorePath"] = Path.Combine(root, "vice"),
            ["Emulator:Mupen64PlusCorePath"] = Path.Combine(root, "n64"),
            ["Emulator:Mame2003PlusCorePath"] = Path.Combine(root, "mame"),
            ["Emulator:FbneoCorePath"] = Path.Combine(root, "fbneo"),
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
        Assert.That(settings.Emulator.PicodriveCorePath, Is.EqualTo(Path.Combine(root, "picodrive")));
        Assert.That(settings.Emulator.Snes9xCorePath, Is.EqualTo(Path.Combine(root, "snes9x")));
        Assert.That(settings.Emulator.OperaCorePath, Is.EqualTo(Path.Combine(root, "opera")));
        Assert.That(settings.Emulator.ViceCorePath, Is.EqualTo(Path.Combine(root, "vice")));
        Assert.That(settings.Emulator.Mupen64PlusCorePath, Is.EqualTo(Path.Combine(root, "n64")));
        Assert.That(settings.Emulator.Mame2003PlusCorePath, Is.EqualTo(Path.Combine(root, "mame")));
        Assert.That(settings.Emulator.FbneoCorePath, Is.EqualTo(Path.Combine(root, "fbneo")));
        Assert.That(json, Does.Contain($"\"emulator\":{{\"genesisCorePath\":{JsonSerializer.Serialize(Path.Combine(root, "genesis"))},"
            + $"\"picodriveCorePath\":{JsonSerializer.Serialize(Path.Combine(root, "picodrive"))},"
            + $"\"snes9xCorePath\":{JsonSerializer.Serialize(Path.Combine(root, "snes9x"))},"
            + $"\"operaCorePath\":{JsonSerializer.Serialize(Path.Combine(root, "opera"))},"
            + $"\"viceCorePath\":{JsonSerializer.Serialize(Path.Combine(root, "vice"))},"
            + $"\"mupen64PlusCorePath\":{JsonSerializer.Serialize(Path.Combine(root, "n64"))},"
            + $"\"mame2003PlusCorePath\":{JsonSerializer.Serialize(Path.Combine(root, "mame"))},"
            + $"\"fbneoCorePath\":{JsonSerializer.Serialize(Path.Combine(root, "fbneo"))}}}"));
        Assert.That(json, Does.Not.Contain("hunter2"));
    }

    [Test]
    public void MaskSources_MasksTheIgdbSecret_KeepsTheRest()
    {
        var masked = EffectiveSettings.MaskSources(new()
        {
            DatBaseUrl = "https://dats.example",
            TitleDbUrl = "https://titledb.example",
            MameDatUrl = "https://mame.example",
            FbneoDatUrl = "https://fbneo.example",
            ThumbnailBaseUrl = "",
            IgdbClientId = "client-id",
            IgdbClientSecret = "s3cret",
        });

        Assert.That(masked.IgdbClientSecret, Is.EqualTo("***"));
        Assert.That(masked.IgdbClientId, Is.EqualTo("client-id"));
        Assert.That(masked.DatBaseUrl, Is.EqualTo("https://dats.example"));
        Assert.That(masked.TitleDbUrl, Is.EqualTo("https://titledb.example"));
        Assert.That(masked.MameDatUrl, Is.EqualTo("https://mame.example"));
        Assert.That(masked.FbneoDatUrl, Is.EqualTo("https://fbneo.example"));
        Assert.That(masked.ThumbnailBaseUrl, Is.Empty);
    }

    [TestCase("")]
    [TestCase("  ")]
    public void MaskSources_UnsetSecret_IsEmpty(string secret) =>
        Assert.That(EffectiveSettings.MaskSources(new() { IgdbClientSecret = secret }).IgdbClientSecret, Is.Empty);

    [Test]
    public void From_MasksTheIgdbSecret_WithoutChangingTheLiveOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "rnrm-effective-igdb");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=db",
            ["Paths:Roms"] = Path.Combine(root, "roms"),
            ["Paths:Ingest"] = Path.Combine(root, "ingest"),
            ["Paths:Logs"] = Path.Combine(root, "logs"),
            ["Paths:Cache"] = Path.Combine(root, "cache"),
            ["Paths:Backups"] = Path.Combine(root, "backups"),
            ["Paths:Collections"] = Path.Combine(root, "collections"),
            ["Paths:Bios"] = Path.Combine(root, "bios"),
            ["Paths:ImageCache"] = Path.Combine(root, "imagecache"),
            ["Sources:IgdbClientId"] = "client-id",
            ["Sources:IgdbClientSecret"] = "s3cret",
        }).Build();
        using var services = new ServiceCollection().AddAppOptions(config).BuildServiceProvider();

        var json = JsonSerializer.Serialize(EffectiveSettings.From(services), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.That(json, Does.Not.Contain("s3cret"));
        Assert.That(json, Does.Contain("\"igdbClientId\":\"client-id\",\"igdbClientSecret\":\"***\""));
        Assert.That(services.GetRequiredService<IOptions<SourcesOptions>>().Value.IgdbClientSecret, Is.EqualTo("s3cret"));
    }
}
