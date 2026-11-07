using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Backups;
using RnRomManager.Core.Configuration;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

/// <summary>B16: the daily backup at <c>backup.time</c> (local), rescheduled on save, skipped while a manual one runs.</summary>
public class BackupSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 2, 0, 0, TimeSpan.Zero);

    private FakeTimeProvider _time = null!;
    private TempDir _dir = null!;
    private IBackupStore _store = null!;
    private ConcurrentQueue<DateTimeOffset> _dumps = null!;
    private TaskCompletionSource? _block;
    private ListLogger _log = null!;
    private BackupService _service = null!;
    private BackupScheduler? _scheduler;

    [SetUp]
    public void SetUp()
    {
        // UTC is "local" here, so backup.time is in UTC.
        _time = new FakeTimeProvider(T0);
        _dir = new TempDir();
        _dumps = new ConcurrentQueue<DateTimeOffset>();
        _block = null;
        _store = Substitute.For<IBackupStore>();
        _store.DumpAsync(Arg.Any<Stream>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (_block is { } block) await block.Task;
                _dumps.Enqueue(call.Arg<DateTimeOffset>());
                await call.Arg<Stream>().WriteAsync("zip"u8.ToArray());
                return new BackupManifest(1, call.Arg<Guid>(), call.Arg<DateTimeOffset>(), "x", "db", [], []);
            });
        _log = new ListLogger();
        _service = new BackupService(_store, new InMemorySettings(), Options.Create(new PathsOptions { Backups = _dir.Combine("backups") }),
            Options.Create(new BackupOptions()), TimeZoneInfo.Utc, _time, NullLogger<BackupService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _block?.TrySetResult();
        if (_scheduler is not null)
        {
            await _scheduler.StopAsync(CancellationToken.None);
            _scheduler.Dispose();
        }

        _dir.Dispose();
    }

    private async Task<BackupScheduler> StartAsync()
    {
        _scheduler = new BackupScheduler(_service, TimeZoneInfo.Utc, _time, _log);
        await _scheduler.StartAsync(CancellationToken.None);
        return _scheduler;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("condition not met in time");
            await Task.Delay(10);
        }
    }

    /// <summary>Moves the fake clock forward once the scheduler is waiting.</summary>
    private async Task AdvanceTo(DateTimeOffset at)
    {
        await Task.Delay(50);
        _time.Advance(at - _time.GetUtcNow());
    }

    private bool Logged(string text) => _log.Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal));

    [Test]
    public async Task ByDefault_RunsAt0330Local_ThenTheNextDay()
    {
        await StartAsync();
        await Eventually(() => Logged("Next scheduled backup: 2026-11-05T03:30:00"));

        await AdvanceTo(T0.AddMinutes(89));
        await Task.Delay(100);
        Assert.That(_dumps, Is.Empty);

        await AdvanceTo(new DateTimeOffset(2026, 11, 5, 3, 30, 0, TimeSpan.Zero));
        await Eventually(() => _dumps.Count == 1);
        Assert.That(_service.LastResult!.Ok, Is.True);
        Assert.That(_service.List().Single().FileName, Is.EqualTo("rnrommanager-20261105-033000.zip"));
        await Eventually(() => Logged("Next scheduled backup: 2026-11-06T03:30:00"));
    }

    [Test]
    public async Task Disabled_NeverRuns()
    {
        await _service.SaveSettingsAsync(new BackupSettings(false, "03:30", 30), default);
        await StartAsync();
        await Eventually(() => Logged("Next scheduled backup: disabled"));

        await AdvanceTo(T0.AddDays(2));
        await Task.Delay(100);

        Assert.That(_dumps, Is.Empty);
    }

    [Test]
    public async Task SavingSettings_ReschedulesAtOnce()
    {
        await StartAsync();
        await Eventually(() => Logged("Next scheduled backup: 2026-11-05T03:30:00"));

        await _service.SaveSettingsAsync(new BackupSettings(true, "02:10", 30), default);
        await Eventually(() => Logged("Next scheduled backup: 2026-11-05T02:10:00"));
        await AdvanceTo(T0.AddMinutes(10));

        await Eventually(() => _dumps.Count == 1);
        Assert.That(_dumps.Single(), Is.EqualTo(T0.AddMinutes(10)));
    }

    [Test]
    public async Task DisablingWhileWaiting_CancelsTheRun()
    {
        await StartAsync();
        await Eventually(() => Logged("Next scheduled backup: 2026-11-05T03:30:00"));

        await _service.SaveSettingsAsync(new BackupSettings(false, "03:30", 30), default);
        await Eventually(() => Logged("Next scheduled backup: disabled"));
        await AdvanceTo(T0.AddHours(2));
        await Task.Delay(100);

        Assert.That(_dumps, Is.Empty);
    }

    [Test]
    public async Task AManualBackupRunning_SkipsTheScheduledOne_WithAnInfoLog()
    {
        _block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = _service.CreateAsync(30, default);
        await StartAsync();
        await Eventually(() => Logged("Next scheduled backup: 2026-11-05T03:30:00"));

        await AdvanceTo(new DateTimeOffset(2026, 11, 5, 3, 30, 0, TimeSpan.Zero));
        await Eventually(() => Logged("Scheduled backup skipped"));

        Assert.That(_log.Entries.Single(e => e.Message.Contains("Scheduled backup skipped", StringComparison.Ordinal)).Level,
            Is.EqualTo(LogLevel.Information));
        _block.SetResult();
        await manual;
        Assert.That(_dumps, Has.Count.EqualTo(1));
        await Eventually(() => Logged("Next scheduled backup: 2026-11-06T03:30:00"));
    }

    [Test]
    public async Task MissedRun_IsNotCaughtUp()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 5, 4, 0, 0, TimeSpan.Zero));
        await StartAsync();

        await Eventually(() => Logged("Next scheduled backup: 2026-11-06T03:30:00"));
        Assert.That(_dumps, Is.Empty);
    }

    private sealed class ListLogger : ILogger<BackupScheduler>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class InMemorySettings : ISettingsRepository
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(_values.Where(v => names.Contains(v.Key)).ToDictionary());

        public Task SetAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        {
            foreach (var (name, value) in values)
                _values[name] = value;
            return Task.CompletedTask;
        }
    }
}
