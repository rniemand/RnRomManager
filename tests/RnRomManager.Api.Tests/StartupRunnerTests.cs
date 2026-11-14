using NLog;
using NSubstitute;

namespace RnRomManager.Api.Tests;

public class StartupRunnerTests
{
    private ApiFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new ApiFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void ValidConfig_CreatesRootFolders()
    {
        using var client = _factory.CreateClient();

        foreach (var key in new[] { "Paths:Roms", "Paths:Ingest", "Paths:Logs", "Paths:Cache", "Paths:Backups", "Paths:Collections", "Paths:Bios" })
            Assert.That(Directory.Exists(_factory.Settings[key]), Is.True, key);
    }

    [Test]
    public void ValidConfig_LogsVersionAndRedactedConfigToFile()
    {
        _factory.Settings["Admin:InitialPassword"] = "initial-secret-pw";

        using (_factory.CreateClient())
            LogManager.Flush();

        var log = ReadLogs();
        Assert.That(log, Does.Contain("|INFO|RnRomManager.Api.StartupRunner|"));
        Assert.That(log, Does.Contain("RnRomManager 0.0.0-dev starting"));
        Assert.That(log, Does.Contain("Effective config: "));
        Assert.That(log, Does.Contain("Database=rnrommanager_test"));
        Assert.That(log, Does.Not.Contain("hunter2"));
        Assert.That(log, Does.Not.Contain("initial-secret-pw"));
    }

    private string ReadLogs()
    {
        var file = Directory.GetFiles(_factory.Settings["Paths:Logs"]!, "rnrommanager-*.log").Single();
        // NLog keeps the file open (keepFileOpen).
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    [Test]
    public void BadRestoreFrom_StopsStartupBeforeMigrations()
    {
        _factory.Settings["Backup:RestoreFrom"] = "../rnrommanager-20261106-033000.zip";

        Assert.Catch(() => _factory.CreateClient());
        LogManager.Flush();

        Assert.That(ReadLogs(), Does.Contain("|FATAL|RnRomManager.Api.Program|")
            .And.Contain("Startup failed: Backup:RestoreFrom must be a file name in Paths:Backups"));
        _factory.Database.DidNotReceive().Migrate();
    }

    [Test]
    public void NoRestoreFrom_StartsAndMigrates()
    {
        using (_factory.CreateClient()) { }

        _factory.Database.Received(1).Migrate();
    }
}
