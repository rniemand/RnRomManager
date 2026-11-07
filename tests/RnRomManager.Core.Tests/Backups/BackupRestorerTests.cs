using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Backups;
using RnRomManager.Core.Configuration;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Backups;

public class BackupRestorerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);
    private const string Name = "rnrommanager-20261106-033000.zip";
    private const string Known = "RnRomManager.Data.Migrations.0001_users.sql";

    private TempDir _dir = null!;
    private IBackupStore _store = null!;
    private BackupOptions _options = null!;
    private BackupRestorer _restorer = null!;

    private string Folder => _dir.Combine("backups");
    private string Marker => Path.Combine(Folder, Name + ".restored");

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        Directory.CreateDirectory(Folder);
        _store = Substitute.For<IBackupStore>();
        _store.KnownMigrations.Returns(new HashSet<string> { Known, "RnRomManager.Data.Migrations.0002_games.sql" });
        _options = new BackupOptions { RestoreFrom = Name };
        _restorer = new BackupRestorer(_store, Options.Create(new PathsOptions { Backups = Folder }), Options.Create(_options),
            new FakeTimeProvider(Now), NullLogger<BackupRestorer>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private void WriteBackup(int formatVersion = 1, params string[] migrations)
    {
        using var zip = ZipFile.Open(Path.Combine(Folder, Name), ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        writer.Write(JsonSerializer.Serialize(new
        {
            formatVersion,
            backupId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            createdAt = "2026-11-06T03:30:00.0000000Z",
            appVersion = "1.2.3",
            database = "rnrommanager",
            migrations = migrations.Length == 0 ? [Known] : migrations,
            tables = new[] { new { name = "users", rows = 2 } },
        }));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public async Task NotConfigured_DoesNothing(string? value)
    {
        _options.RestoreFrom = value;

        Assert.That(await _restorer.RestoreIfConfiguredAsync(default), Is.False);
        await _store.DidNotReceiveWithAnyArgs().RestoreAsync(default!, default!, default);
    }

    [TestCase("../rnrommanager-20261106-033000.zip")]
    [TestCase("..")]
    [TestCase("sub/rnrommanager-20261106-033000.zip")]
    [TestCase(@"sub\rnrommanager-20261106-033000.zip")]
    [TestCase("/app/backups/rnrommanager-20261106-033000.zip")]
    public async Task NotABareName_Throws(string value)
    {
        _options.RestoreFrom = value;

        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() => _restorer.RestoreIfConfiguredAsync(default));

        Assert.That(ex!.Message, Does.StartWith("Backup:RestoreFrom must be a file name in Paths:Backups"));
    }

    [Test]
    public async Task MissingFile_Throws()
    {
        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() => _restorer.RestoreIfConfiguredAsync(default));

        Assert.That(ex!.Message, Does.Contain($"'{Name}' doesn't exist in Paths:Backups"));
    }

    [Test]
    public async Task AlreadyRestored_IsSkipped_DataUntouched()
    {
        WriteBackup();
        await File.WriteAllTextAsync(Marker, "2026-11-06T09:00:00.0000000Z");

        Assert.That(await _restorer.RestoreIfConfiguredAsync(default), Is.False);
        await _store.DidNotReceiveWithAnyArgs().RestoreAsync(default!, default!, default);
        Assert.That(await File.ReadAllTextAsync(Marker), Is.EqualTo("2026-11-06T09:00:00.0000000Z"));
    }

    [Test]
    public async Task UnknownMigration_ThrowsBeforeDroppingAnything()
    {
        WriteBackup(1, Known, "RnRomManager.Data.Migrations.0099_future.sql");

        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() => _restorer.RestoreIfConfiguredAsync(default));

        Assert.That(ex!.Message, Does.Contain("is from a newer app version").And.Contain("0099_future.sql"));
        await _store.DidNotReceiveWithAnyArgs().RestoreAsync(default!, default!, default);
        Assert.That(File.Exists(Marker), Is.False);
    }

    [Test]
    public async Task NewerFormatVersion_Throws()
    {
        WriteBackup(2);

        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() => _restorer.RestoreIfConfiguredAsync(default));

        Assert.That(ex!.Message, Does.Contain("is from a newer app version").And.Contain("format version 2"));
        await _store.DidNotReceiveWithAnyArgs().RestoreAsync(default!, default!, default);
    }

    [Test]
    public async Task NotAZip_Throws()
    {
        File.WriteAllText(Path.Combine(Folder, Name), "not a zip");

        await Assert.ThrowsAsync<BackupRestoreException>(() => _restorer.RestoreIfConfiguredAsync(default));
    }

    [Test]
    public async Task NoManifest_Throws()
    {
        using (ZipFile.Open(Path.Combine(Folder, Name), ZipArchiveMode.Create)) { }

        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() => _restorer.RestoreIfConfiguredAsync(default));

        Assert.That(ex!.Message, Is.EqualTo("The backup has no manifest.json."));
    }

    [Test]
    public async Task Success_RestoresWithTheManifest_ThenWritesTheMarker()
    {
        WriteBackup();
        BackupManifest? passed = null;
        await _store.RestoreAsync(Arg.Any<ZipArchive>(), Arg.Do<BackupManifest>(m => passed = m), Arg.Any<CancellationToken>());

        Assert.That(await _restorer.RestoreIfConfiguredAsync(default), Is.True);

        Assert.That(passed!.BackupId, Is.EqualTo(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        Assert.That(passed.CreatedAt, Is.EqualTo(new DateTimeOffset(2026, 11, 6, 3, 30, 0, TimeSpan.Zero)));
        Assert.That(passed.Tables, Is.EqualTo(new[] { new BackupTable("users", 2) }));
        Assert.That(await File.ReadAllTextAsync(Marker), Is.EqualTo("2026-11-07T08:00:00.0000000Z"));
    }

    [Test]
    public async Task StoreFailure_Propagates_AndWritesNoMarker()
    {
        WriteBackup();
        _store.RestoreAsync(Arg.Any<ZipArchive>(), Arg.Any<BackupManifest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BackupRestoreException("Table users: restored 1 rows but the manifest lists 2."));

        await Assert.ThrowsAsync<BackupRestoreException>(() => _restorer.RestoreIfConfiguredAsync(default));
        Assert.That(File.Exists(Marker), Is.False);
    }
}
