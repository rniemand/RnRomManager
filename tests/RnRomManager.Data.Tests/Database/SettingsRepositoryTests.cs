using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Backups;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>B15 <c>app_settings</c> against MariaDB.</summary>
[Category("Integration")]
public class SettingsRepositoryTests
{
    private SettingsRepository _settings = null!;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _settings = new SettingsRepository(factory);
    }

    [Test]
    public async Task MissingRows_AreAbsent_SoTheDefaultsApply()
    {
        var stored = await _settings.GetAsync(BackupSettings.Keys.ToList(), default);

        Assert.That(stored, Is.Empty);
        Assert.That(BackupSettings.FromStored(stored), Is.EqualTo(BackupSettings.Defaults));
    }

    [Test]
    public async Task Set_InsertsThenReplaces_AndGetReturnsOnlyTheAskedNames()
    {
        await _settings.SetAsync(new Dictionary<string, string> { ["backup.time"] = "01:00", ["other"] = "x" }, default);
        await _settings.SetAsync(new BackupSettings(false, "22:15", 7).ToStored(), default);

        var stored = await _settings.GetAsync(BackupSettings.Keys.ToList(), default);

        Assert.That(stored, Is.EqualTo(new Dictionary<string, string>
        {
            ["backup.enabled"] = "false", ["backup.time"] = "22:15", ["backup.retention"] = "7",
        }));
        Assert.That(await _settings.GetAsync(["other"], default), Is.EqualTo(new Dictionary<string, string> { ["other"] = "x" }));
        Assert.That(await _settings.GetAsync([], default), Is.Empty);
    }
}
