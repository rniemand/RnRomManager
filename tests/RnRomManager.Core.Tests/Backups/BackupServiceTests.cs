using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Backups;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Backups;

public class BackupServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 3, 30, 0, TimeSpan.Zero);

    private TempDir _dir = null!;
    private IBackupStore _store = null!;
    private FakeTimeProvider _time = null!;
    private BackupOptions _options = null!;
    private InMemorySettings _settings = null!;
    private BackupService _service = null!;
    private JobLock _jobLock = null!;
    private IMaintenanceWindow _maintenance = null!;
    private int _windowClosed;
    private IDatabaseLifecycle _database = null!;
    private IJobRepository _jobs = null!;
    private IUserRepository _users = null!;
    private IDiscoveryRepository _discovery = null!;
    private DatRefreshSettingsService _datRefresh = null!;
    private IAuditLog _audit = null!;
    private IRomStorageSettings _romStorage = null!;

    private string Folder => _dir.Combine("backups");

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _store = Substitute.For<IBackupStore>();
        _store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.Arg<Stream>().WriteAsync("zip"u8.ToArray());
                return Manifest(call.Arg<Guid>(), call.Arg<DateTimeOffset>());
            });
        _time = new FakeTimeProvider(T0);
        _options = new BackupOptions();
        _settings = new InMemorySettings();
        _store.KnownMigrations.Returns(new HashSet<string> { Known });
        _jobLock = new JobLock();
        _windowClosed = 0;
        var window = Substitute.For<IAsyncDisposable>();
        window.DisposeAsync().Returns(_ =>
        {
            _windowClosed++;
            return ValueTask.CompletedTask;
        });
        _maintenance = Substitute.For<IMaintenanceWindow>();
        _maintenance.TryEnterAsync(Arg.Any<CancellationToken>()).Returns(window);
        _database = Substitute.For<IDatabaseLifecycle>();
        _jobs = Substitute.For<IJobRepository>();
        _jobs.AbortRunningAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Guid>());
        _users = Substitute.For<IUserRepository>();
        _discovery = Substitute.For<IDiscoveryRepository>();
        _datRefresh = new DatRefreshSettingsService(_settings, TimeZoneInfo.Utc, _time, NullLogger<DatRefreshSettingsService>.Instance);
        _audit = Substitute.For<IAuditLog>();
        _romStorage = Substitute.For<IRomStorageSettings>();
        _service = new BackupService(_store, _settings, Options.Create(new PathsOptions { Backups = Folder }), Options.Create(_options),
            TimeZoneInfo.Utc, _time, NullLogger<BackupService>.Instance, _jobLock, _maintenance, _database, _jobs, _users, _discovery,
            _datRefresh, _romStorage, _audit);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static BackupManifest Manifest(Guid id, DateTimeOffset at) =>
        new(1, id, at, "1.0.0", "rnrommanager", [Known], [new BackupTable("users", 1)]);

    private string[] Files() => Directory.GetFiles(Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private void Existing(string name, string content = "old") => File.WriteAllText(Path.Combine(Folder, name), content);

    private void ExistingBackups(int count)
    {
        Directory.CreateDirectory(Folder);
        for (var i = count; i >= 1; i--)
            Existing(BackupNames.For(T0.AddDays(-i)));
    }

    [Test]
    public async Task Create_WritesTheZipUnderItsUtcName_AndReportsIt()
    {
        var file = await _service.CreateAsync(30, default);

        Assert.That(file, Is.EqualTo(new BackupFile("rnrommanager-20261106-033000.zip", 3, T0)));
        Assert.That(Files(), Is.EqualTo(new[] { "rnrommanager-20261106-033000.zip" }));
        Assert.That(File.ReadAllText(Path.Combine(Folder, file.FileName)), Is.EqualTo("zip"));
        Assert.That(_service.LastResult, Is.EqualTo(new BackupResult(T0, true, file.FileName, null)));
        await _store.Received(1).DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), T0, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WritesToATempFileFirst()
    {
        string[]? during = null;
        _store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                during = Files();
                return Manifest(call.Arg<Guid>(), call.Arg<DateTimeOffset>());
            });

        await _service.CreateAsync(30, default);

        Assert.That(during, Is.EqualTo(new[] { "rnrommanager-20261106-033000.zip.tmp" }));
    }

    [Test]
    public async Task Retention_KeepsTheNewestN()
    {
        ExistingBackups(4);

        await _service.CreateAsync(3, default);

        Assert.That(Files(), Is.EqualTo(new[]
        {
            BackupNames.For(T0.AddDays(-2)), BackupNames.For(T0.AddDays(-1)), BackupNames.For(T0),
        }));
    }

    [Test]
    public async Task Retention_NeverDeletesAMarkedBackup_WhichDoesntCount()
    {
        ExistingBackups(3);
        var marked = BackupNames.For(T0.AddDays(-3));
        Existing(BackupNames.MarkerFor(marked), "2026-11-03T00:00:00Z");

        await _service.CreateAsync(2, default);

        Assert.That(Files(), Is.EqualTo(new[]
        {
            marked, BackupNames.MarkerFor(marked), BackupNames.For(T0.AddDays(-1)), BackupNames.For(T0),
        }));
    }

    [Test]
    public async Task Retention_NeverDeletesTheRestoreFromBackup_WhichDoesntCount()
    {
        ExistingBackups(3);
        _options.RestoreFrom = BackupNames.For(T0.AddDays(-3));

        await _service.CreateAsync(1, default);

        Assert.That(Files(), Is.EqualTo(new[] { BackupNames.For(T0.AddDays(-3)), BackupNames.For(T0) }));
    }

    [Test]
    public void Retention_NeverDeletesTheKeptBackup_WhichDoesntCount()
    {
        ExistingBackups(4);
        var oldest = BackupNames.For(T0.AddDays(-4));

        _service.ApplyRetention(2, keep: oldest);

        Assert.That(Files(), Is.EqualTo(new[] { oldest, BackupNames.For(T0.AddDays(-2)), BackupNames.For(T0.AddDays(-1)) }));
    }

    [Test]
    public async Task Retention_IgnoresFilesNotNamedLikeABackup()
    {
        ExistingBackups(2);
        Existing("notes.txt");
        Existing("rnrommanager-old.zip");
        Existing("rnrommanager-20200101-000000.zip.bak");

        await _service.CreateAsync(1, default);

        Assert.That(Files(), Is.EqualTo(new[]
        {
            "notes.txt", "rnrommanager-20200101-000000.zip.bak", BackupNames.For(T0), "rnrommanager-old.zip",
        }));
    }

    [Test]
    public async Task FailedDump_LeavesNoTempFile_DeletesNothing_AndRecordsTheError()
    {
        ExistingBackups(3);
        _store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db gone"));
        var before = Files();

        var ex = await Assert.ThrowsAsync<BackupFailedException>(() => _service.CreateAsync(1, default));

        Assert.That(ex!.InnerException, Is.InstanceOf<InvalidOperationException>());
        Assert.That(Files(), Is.EqualTo(before));
        Assert.That(_service.LastResult, Is.EqualTo(new BackupResult(T0, false, null, "db gone")));
        Assert.That(_service.IsRunning, Is.False);
    }

    [Test]
    public async Task LastResult_FollowsEveryRun()
    {
        await _service.CreateAsync(30, default);
        _time.Advance(TimeSpan.FromMinutes(1));
        _store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));
        await Assert.ThrowsAsync<BackupFailedException>(() => _service.CreateAsync(30, default));

        Assert.That(_service.LastResult, Is.EqualTo(new BackupResult(T0.AddMinutes(1), false, null, "disk full")));
    }

    [Test]
    public async Task StaleTempFiles_AreDeletedBeforeARun()
    {
        Directory.CreateDirectory(Folder);
        Existing("rnrommanager-20261101-033000.zip.tmp");
        Existing("other.tmp");

        await _service.CreateAsync(30, default);

        Assert.That(Files(), Is.EqualTo(new[] { "other.tmp", BackupNames.For(T0) }));
    }

    [Test]
    public async Task SameSecond_GetsANumberedName()
    {
        var first = await _service.CreateAsync(30, default);
        var second = await _service.CreateAsync(30, default);
        var third = await _service.CreateAsync(30, default);

        Assert.That((first.FileName, second.FileName, third.FileName), Is.EqualTo((
            "rnrommanager-20261106-033000.zip", "rnrommanager-20261106-033000 (2).zip", "rnrommanager-20261106-033000 (3).zip")));
        Assert.That(_service.List().Select(b => b.FileName), Is.EqualTo(new[] { third.FileName, second.FileName, first.FileName }));
    }

    [Test]
    public async Task ASecondRunWhileOneIsRunning_Throws()
    {
        var release = new TaskCompletionSource();
        _store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await release.Task;
                return Manifest(call.Arg<Guid>(), call.Arg<DateTimeOffset>());
            });

        var first = _service.CreateAsync(30, default);

        Assert.That(_service.IsRunning, Is.True);
        await Assert.ThrowsAsync<BackupRunningException>(() => _service.CreateAsync(30, default));
        release.SetResult();
        await first;
        Assert.That(_service.IsRunning, Is.False);
    }

    [Test]
    public void List_IsEmptyWithoutTheFolder() => Assert.That(_service.List(), Is.Empty);

    [Test]
    public async Task List_IsNewestFirst_WithSizes()
    {
        ExistingBackups(2);
        await _service.CreateAsync(30, default);

        Assert.That(_service.List(), Is.EqualTo(new[]
        {
            new BackupFile(BackupNames.For(T0), 3, T0),
            new BackupFile(BackupNames.For(T0.AddDays(-1)), 3, T0.AddDays(-1)),
            new BackupFile(BackupNames.For(T0.AddDays(-2)), 3, T0.AddDays(-2)),
        }));
    }

    [Test]
    public async Task Settings_DefaultWhenNothingIsStored()
    {
        Assert.That(await _service.GetSettingsAsync(default), Is.EqualTo(new BackupSettings(true, "03:30", 30)));
    }

    [Test]
    public async Task SaveSettings_StoresThem_AndRaisesTheChangeSignal()
    {
        var raised = 0;
        _service.SettingsChanged += () => raised++;

        await _service.SaveSettingsAsync(new BackupSettings(false, "22:15", 7), default);

        Assert.That(_settings.Values, Is.EqualTo(new Dictionary<string, string>
        {
            ["backup.enabled"] = "false", ["backup.time"] = "22:15", ["backup.retention"] = "7",
        }));
        Assert.That(await _service.GetSettingsAsync(default), Is.EqualTo(new BackupSettings(false, "22:15", 7)));
        Assert.That(raised, Is.EqualTo(1));
    }

    [Test]
    public async Task SaveSettings_Invalid_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveSettingsAsync(new BackupSettings(true, "24:00", 30), default));
        Assert.That(_settings.Values, Is.Empty);
    }

    [Test]
    public void NextRunAt_IsTheNextDailyTime_OrNullWhenDisabled()
    {
        Assert.That(_service.NextRunAt(new BackupSettings(true, "03:30", 30)), Is.EqualTo(T0.AddDays(1)));
        Assert.That(_service.NextRunAt(new BackupSettings(true, "04:00", 30)), Is.EqualTo(T0.AddMinutes(30)));
        Assert.That(_service.NextRunAt(new BackupSettings(false, "04:00", 30)), Is.Null);
        Assert.That(_service.TimeZoneId, Is.EqualTo(TimeZoneInfo.Utc.Id));
    }

    // --- B27 live restore ---

    private static readonly AuditActor Boss = new(1, "boss", null);
    private const string Known = "0001_users.sql";

    /// <summary>A restorable backup in the folder: manifest, schema and the users data entry.</summary>
    private string RestorableBackup(DateTimeOffset at, int formatVersion = 1, string? omit = null)
    {
        Directory.CreateDirectory(Folder);
        var name = BackupNames.For(at);
        using var zip = System.IO.Compression.ZipFile.Open(Path.Combine(Folder, name), System.IO.Compression.ZipArchiveMode.Create);
        foreach (var entry in new[] { BackupManifest.SchemaFileName, BackupManifest.DataEntry("users") }.Where(e => e != omit))
            using (var content = new StreamWriter(zip.CreateEntry(entry).Open()))
                content.Write("x");
        using var writer = new StreamWriter(zip.CreateEntry(BackupManifest.FileName).Open());
        writer.Write(System.Text.Json.JsonSerializer.Serialize(Manifest(Guid.NewGuid(), at) with { FormatVersion = formatVersion },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        return name;
    }

    private Task<BackupRestoreResult> RestoreAsync(string name, CancellationToken ct = default) => _service.RestoreAsync(name, Boss, ct);

    private async Task AssertNothingChanged()
    {
        await _store.DidNotReceiveWithAnyArgs().RestoreAsync(default!, default!, default);
        _database.DidNotReceive().Migrate();
        await _users.DidNotReceiveWithAnyArgs().RotateAllSecurityStampsAsync(default);
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
    }

    [TestCase("../rnrommanager-20261105-033000.zip")]
    [TestCase("rnrommanager-20261105-033000.zip.tmp")]
    [TestCase("rnrommanager-20261105-033000.zip.restored")]
    [TestCase("notes.zip")]
    [TestCase("")]
    public async Task Restore_NameNotInTheList_IsInvalidName_WithoutTakingALock(string name)
    {
        using var held = _jobLock.TryAcquire("test");

        Assert.That((await RestoreAsync(name)).Status, Is.EqualTo(BackupRestoreStatus.InvalidName), "checked before the locks");
        await _maintenance.DidNotReceiveWithAnyArgs().TryEnterAsync(default);
    }

    [Test]
    public async Task Restore_JobLockHeld_IsJobRunning()
    {
        var name = RestorableBackup(T0.AddDays(-1));
        using var held = _jobLock.TryAcquire("test");

        Assert.That((await RestoreAsync(name)).Status, Is.EqualTo(BackupRestoreStatus.JobRunning));
        await AssertNothingChanged();
    }

    [Test]
    public async Task Restore_WhileABackupRuns_IsBackupRunning_AndReleasesTheJobLock()
    {
        var name = RestorableBackup(T0.AddDays(-1));
        var block = new TaskCompletionSource();
        _store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await block.Task;
                return Manifest(call.Arg<Guid>(), call.Arg<DateTimeOffset>());
            });
        var running = _service.CreateAsync(5, default);

        Assert.That((await RestoreAsync(name)).Status, Is.EqualTo(BackupRestoreStatus.BackupRunning));
        using (var free = _jobLock.TryAcquire("test"))
            Assert.That(free, Is.Not.Null, "the job lock is released again");
        block.SetResult();
        await running;
        await AssertNothingChanged();
    }

    [Test]
    public async Task Restore_UnknownFile_IsNotFound()
    {
        Assert.That((await RestoreAsync(BackupNames.For(T0.AddDays(-9)))).Status, Is.EqualTo(BackupRestoreStatus.NotFound));
        await AssertNothingChanged();
    }

    [Test]
    public async Task Restore_TooNewOrUnusableBackup_IsRefusedBeforeTheGate()
    {
        var tooNew = RestorableBackup(T0.AddDays(-2), formatVersion: 2);
        var noSchema = RestorableBackup(T0.AddDays(-1), omit: BackupManifest.SchemaFileName);

        var a = await RestoreAsync(tooNew);
        var b = await RestoreAsync(noSchema);

        Assert.That((a.Status, a.Message), Is.EqualTo((BackupRestoreStatus.TooNew, (string?)$"Backup {tooNew} is from a newer app version (format version 2, app {AppInfo.Version}); it can't be restored.")));
        Assert.That((b.Status, b.Message), Is.EqualTo((BackupRestoreStatus.InvalidBackup, (string?)"The backup has no schema.sql.")));
        await _maintenance.DidNotReceiveWithAnyArgs().TryEnterAsync(default);
        await AssertNothingChanged();
    }

    [Test]
    public async Task Restore_GateRefused_IsBusy_WithNoSafetyBackup()
    {
        var name = RestorableBackup(T0.AddDays(-1));
        _maintenance.TryEnterAsync(Arg.Any<CancellationToken>()).Returns((IAsyncDisposable?)null);

        Assert.That((await RestoreAsync(name)).Status, Is.EqualTo(BackupRestoreStatus.Busy));
        await _store.DidNotReceiveWithAnyArgs().DumpAsync(default!, default, default, default);
        await AssertNothingChanged();
    }

    [Test]
    public async Task Restore_SafetyBackupFails_IsBackupFailed_NothingChanges_AndTheGateCloses()
    {
        var name = RestorableBackup(T0.AddDays(-1));
        _store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));

        Assert.That((await RestoreAsync(name)).Status, Is.EqualTo(BackupRestoreStatus.BackupFailed));
        await AssertNothingChanged();
        Assert.That(_windowClosed, Is.EqualTo(1));
    }

    [Test]
    public async Task Restore_RunsEveryStepInOrder_KeepsTheSource_AndReturnsTheSafetyBackup()
    {
        // The oldest of three with retention 1: the safety backup's retention must neither delete nor count it.
        var name = RestorableBackup(T0.AddDays(-3));
        RestorableBackup(T0.AddDays(-2));
        _settings.Values[BackupSettings.RetentionKey] = "1";
        var backupWoken = 0;
        var datWoken = 0;
        _service.SettingsChanged += () => backupWoken++;
        _datRefresh.SettingsChanged += () => datWoken++;

        var result = await RestoreAsync(name);

        Assert.That(result, Is.EqualTo(new BackupRestoreResult(BackupRestoreStatus.Restored, name, BackupNames.For(T0))));
        Assert.That(Files(), Is.EqualTo(new[] { name, BackupNames.For(T0) }), "source kept, older one beyond retention 1 deleted");
        Received.InOrder(() =>
        {
            _maintenance.TryEnterAsync(Arg.Any<CancellationToken>());
            _store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            _store.RestoreAsync(Arg.Any<System.IO.Compression.ZipArchive>(), Arg.Any<BackupManifest>(), CancellationToken.None);
            _database.Migrate();
            _jobs.AbortRunningAsync(T0.UtcDateTime, CancellationToken.None);
            _users.RotateAllSecurityStampsAsync(CancellationToken.None);
            _discovery.Invalidate();
            _romStorage.LoadAsync(CancellationToken.None);
            _audit.Record(Arg.Is<AuditEvent>(e => e.Action == AuditActions.BackupRestored && e.Actor == Boss
                && e.Details == $"{name}; safety backup {BackupNames.For(T0)}"));
        });
        Assert.That((backupWoken, datWoken), Is.EqualTo((1, 1)), "both schedulers re-read the restored settings");
        Assert.That(_windowClosed, Is.EqualTo(1));
        using (var free = _jobLock.TryAcquire("test"))
            Assert.That(free, Is.Not.Null);
        Assert.That(_service.IsRunning, Is.False);
    }

    [Test]
    public async Task Restore_FailureAfterTheDrop_IsRestoreFailed_NamingTheSafetyBackup()
    {
        var name = RestorableBackup(T0.AddDays(-1));
        _store.RestoreAsync(Arg.Any<System.IO.Compression.ZipArchive>(), Arg.Any<BackupManifest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BackupRestoreException("Table users: restored 1 rows but the manifest lists 2."));

        var result = await RestoreAsync(name);

        Assert.That((result.Status, result.SafetyBackup), Is.EqualTo((BackupRestoreStatus.RestoreFailed, (string?)BackupNames.For(T0))));
        Assert.That(result.Message, Does.Contain(BackupNames.For(T0)));
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
        Assert.That(_windowClosed, Is.EqualTo(1));

        // Later scheduled backups (of the partial database) must not prune the two good copies.
        File.WriteAllText(Path.Combine(Folder, BackupNames.For(T0.AddDays(1))), "");
        _service.ApplyRetention(1);
        Assert.That(File.Exists(Path.Combine(Folder, name)) && File.Exists(Path.Combine(Folder, BackupNames.For(T0))), Is.True);
    }

    [Test]
    public async Task Restore_RequestCancelledAfterTheDrop_StillFinishes()
    {
        var name = RestorableBackup(T0.AddDays(-1));
        using var request = new CancellationTokenSource();
        _store.RestoreAsync(Arg.Any<System.IO.Compression.ZipArchive>(), Arg.Any<BackupManifest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                request.Cancel(); // the tab closed mid-restore
                return Task.CompletedTask;
            });

        var result = await RestoreAsync(name, request.Token);

        Assert.That(result.Status, Is.EqualTo(BackupRestoreStatus.Restored));
        _database.Received(1).Migrate();
        await _users.Received(1).RotateAllSecurityStampsAsync(CancellationToken.None);
        _audit.Received(1).Record(Arg.Any<AuditEvent>());
    }

    private sealed class InMemorySettings : ISettingsRepository
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Values.Where(v => names.Contains(v.Key)).ToDictionary());

        public Task SetAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        {
            foreach (var (name, value) in values)
                Values[name] = value;
            return Task.CompletedTask;
        }
    }
}
