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

    private void WriteBackup(int formatVersion = 1, params string[] migrations) => WriteBackup(formatVersion, null, migrations);

    /// <summary>A backup with its manifest, schema and the users data entry, less <paramref name="omit"/>.</summary>
    private void WriteBackup(int formatVersion, string? omit, params string[] migrations)
    {
        using var zip = ZipFile.Open(Path.Combine(Folder, Name), ZipArchiveMode.Create);
        foreach (var entry in new[] { BackupManifest.SchemaFileName, BackupManifest.DataEntry("users") }.Where(e => e != omit))
            using (var content = new StreamWriter(zip.CreateEntry(entry).Open()))
                content.Write("x");
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

    [TestCase("schema.sql")]
    [TestCase("data/users.jsonl")]
    public async Task MissingSchemaOrDataEntry_ThrowsBeforeDroppingAnything(string entry)
    {
        WriteBackup(1, entry);

        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() => _restorer.RestoreIfConfiguredAsync(default));

        Assert.That(ex!.Message, Is.EqualTo($"The backup has no {entry}."));
        await _store.DidNotReceiveWithAnyArgs().RestoreAsync(default!, default!, default);
    }

    [TestCase("rnrommanager-20261106-033000.zip", true)]
    [TestCase("rnrommanager-20261106-033000 (2).zip", true)]
    [TestCase("../x.zip", false)]
    [TestCase("a/b.zip", false)]
    [TestCase(@"a\b.zip", false)]
    [TestCase("..", false)]
    [TestCase("/abs.zip", false)]
    public void IsBareName_IsRule1(string name, bool bare)
    {
        Assert.That(BackupCheck.IsBareName(name), Is.EqualTo(bare));
    }

    private BackupInspection Inspect()
    {
        using var stream = File.OpenRead(Path.Combine(Folder, Name));
        var inspection = BackupCheck.Open(stream, Name, _store.KnownMigrations);
        inspection.Zip?.Dispose();
        return inspection;
    }

    [Test]
    public void Check_ValidBackup_ReturnsTheOpenZipAndManifest()
    {
        WriteBackup();

        var inspection = Inspect();

        Assert.That((inspection.Failure, inspection.Message, inspection.Zip is null), Is.EqualTo(((BackupCheckFailure?)null, (string?)null, false)));
        Assert.That(inspection.Manifest!.Tables, Is.EqualTo(new[] { new BackupTable("users", 2) }));
    }

    [Test]
    public void Check_TooNew_IsTooNew()
    {
        WriteBackup(1, Known, "RnRomManager.Data.Migrations.0099_future.sql");
        Assert.That(Inspect().Failure, Is.EqualTo(BackupCheckFailure.TooNew));

        WriteBackupFresh(2);
        Assert.That(Inspect().Failure, Is.EqualTo(BackupCheckFailure.TooNew));
    }

    private void WriteBackupFresh(int formatVersion)
    {
        File.Delete(Path.Combine(Folder, Name));
        WriteBackup(formatVersion);
    }

    [Test]
    public void Check_NotAZip_NoManifest_BadManifest_OrMissingEntry_IsInvalid()
    {
        File.WriteAllText(Path.Combine(Folder, Name), "not a zip");
        var notZip = Inspect();
        Assert.That((notZip.Failure, notZip.Zip), Is.EqualTo(((BackupCheckFailure?)BackupCheckFailure.Invalid, (ZipArchive?)null)));
        Assert.That(notZip.Message, Does.StartWith($"Backup {Name} isn't a readable ZIP"));

        File.Delete(Path.Combine(Folder, Name));
        using (ZipFile.Open(Path.Combine(Folder, Name), ZipArchiveMode.Create)) { }
        Assert.That((Inspect().Failure, Inspect().Message), Is.EqualTo(((BackupCheckFailure?)BackupCheckFailure.Invalid, "The backup has no manifest.json.")));

        File.Delete(Path.Combine(Folder, Name));
        using (var zip = ZipFile.Open(Path.Combine(Folder, Name), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("{ not json");
        Assert.That(Inspect().Failure, Is.EqualTo(BackupCheckFailure.Invalid));

        File.Delete(Path.Combine(Folder, Name));
        WriteBackup(1, "schema.sql");
        Assert.That((Inspect().Failure, Inspect().Message), Is.EqualTo(((BackupCheckFailure?)BackupCheckFailure.Invalid, "The backup has no schema.sql.")));
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
