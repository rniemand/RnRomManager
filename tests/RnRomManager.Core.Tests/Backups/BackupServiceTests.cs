using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Backups;
using RnRomManager.Core.Configuration;
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
        _service = new BackupService(_store, _settings, Options.Create(new PathsOptions { Backups = Folder }), Options.Create(_options),
            TimeZoneInfo.Utc, _time, NullLogger<BackupService>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static BackupManifest Manifest(Guid id, DateTimeOffset at) =>
        new(1, id, at, "1.0.0", "rnrommanager", ["0001_users.sql"], [new BackupTable("users", 1)]);

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
