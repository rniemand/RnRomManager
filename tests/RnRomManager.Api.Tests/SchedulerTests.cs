using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

/// <summary>Spec §4.1 step 10 scheduler: daily quick check (local time, no catch-up, skipped when busy) + §13 log purge (S37).</summary>
public class SchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private FakeTimeProvider _time = null!;
    private IJobRepository _jobs = null!;
    private JobLock _lock = null!;
    private TempDir _logs = null!;
    private ListLogger _log = null!;
    private IAuditLog _audit = null!;
    private List<NoopTestJob> _created = null!;
    private Scheduler? _scheduler;

    [SetUp]
    public void SetUp()
    {
        // 2026-11-05 02:00 UTC; the scheduler uses UTC as "local" here, so QuickCheckTime is in UTC.
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 2, 0, 0, TimeSpan.Zero));
        _jobs = Substitute.For<IJobRepository>();
        _lock = new JobLock();
        _logs = new TempDir();
        _log = new ListLogger();
        _audit = Substitute.For<IAuditLog>();
        _created = [];
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_scheduler is not null)
        {
            await _scheduler.StopAsync(CancellationToken.None);
            _scheduler.Dispose();
        }
        foreach (var job in _created)
            job.Release.TrySetResult();
        _logs.Dispose();
    }

    private Scheduler Create(string quickCheckTime = "03:00")
    {
        var runner = new JobRunner(_jobs, _lock, Substitute.For<RnRomManager.Core.Users.IUserRepository>(), _time, NullLogger<JobRunner>.Instance);
        var retention = new LogRetention(Options.Create(new PathsOptions { Logs = _logs.Path }), Options.Create(new LoggingOptions { RetentionDays = 14 }),
            _time, NullLogger<LogRetention>.Instance);
        _scheduler = new Scheduler(runner, (_, _) =>
        {
            var job = new NoopTestJob();
            _created.Add(job);
            return job;
        }, retention, _audit, Options.Create(new IntegrityOptions { QuickCheckTime = quickCheckTime }), TimeZoneInfo.Utc, _time, _log);
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

    /// <summary>Moves the fake clock forward once the scheduler is waiting (its timer exists).</summary>
    private async Task AdvanceTo(DateTimeOffset at)
    {
        await Task.Delay(50); // let ExecuteAsync reach its Task.Delay
        _time.Advance(at - _time.GetUtcNow());
    }

    [Test]
    public void NextQuickCheck_IsTheNextLocalRun_OrNullWhenDisabled()
    {
        Assert.That(Create("03:00").NextQuickCheckAt(), Is.EqualTo(new DateTimeOffset(2026, 11, 5, 3, 0, 0, TimeSpan.Zero)));
        Assert.That(Create("").NextQuickCheckAt(), Is.Null);
    }

    [Test]
    public async Task AtTheScheduledTime_StartsAQuickCheck_ThenTheNextDay()
    {
        var scheduler = Create();
        await scheduler.StartAsync(CancellationToken.None);

        await AdvanceTo(new DateTimeOffset(2026, 11, 5, 3, 0, 0, TimeSpan.Zero));
        await Eventually(() => _created.Count == 1);
        await _jobs.Received(1).InsertAsync(Arg.Is<Job>(j => j.Trigger == JobTrigger.Schedule && j.Type == JobType.Integrity && j.Mode == JobMode.Quick),
            Arg.Any<CancellationToken>());
        Assert.That(scheduler.NextQuickCheckAt(), Is.EqualTo(new DateTimeOffset(2026, 11, 6, 3, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public async Task WhenBusy_TheRunIsSkipped_WithAnInfoLog()
    {
        using var held = _lock.TryAcquire();
        var scheduler = Create();
        await scheduler.StartAsync(CancellationToken.None);

        await AdvanceTo(new DateTimeOffset(2026, 11, 5, 3, 0, 0, TimeSpan.Zero));
        await Eventually(() => _log.Entries.Any(e => e.Message.Contains("skipped", StringComparison.Ordinal)));

        Assert.That(_log.Entries.Single(e => e.Message.Contains("skipped", StringComparison.Ordinal)).Level, Is.EqualTo(LogLevel.Information));
        await _jobs.DidNotReceiveWithAnyArgs().InsertAsync(default!, default);
    }

    [Test]
    public async Task MissedRun_IsNotCaughtUp()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 5, 3, 30, 0, TimeSpan.Zero)); // the app was down at 03:00
        var scheduler = Create();
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        Assert.That(_created, Is.Empty);
        Assert.That(scheduler.NextQuickCheckAt(), Is.EqualTo(new DateTimeOffset(2026, 11, 6, 3, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public async Task Disabled_NeverStartsAJob()
    {
        var scheduler = Create("");
        await scheduler.StartAsync(CancellationToken.None);

        await AdvanceTo(new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero));
        await Task.Delay(100);

        Assert.That(_created, Is.Empty);
    }

    [Test]
    public async Task LogPurge_AtStartup_AndDailyAt0005Utc()
    {
        var old = _logs.Combine("rnrommanager-2026-10-01.log");
        await File.WriteAllTextAsync(old, "x");
        var scheduler = Create("");
        await scheduler.StartAsync(CancellationToken.None);
        await Eventually(() => !File.Exists(old));

        var later = _logs.Combine("rnrommanager-2026-10-22.log"); // 14 days old today, 15 tomorrow
        await File.WriteAllTextAsync(later, "x");
        await AdvanceTo(new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.Zero));
        await Task.Delay(100);
        Assert.That(File.Exists(later), Is.True, "not before 00:05 UTC");

        await AdvanceTo(new DateTimeOffset(2026, 11, 6, 0, 5, 0, TimeSpan.Zero));
        await Eventually(() => !File.Exists(later));
    }

    private IEnumerable<DateTime> AuditPurges() =>
        _audit.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IAuditLog.PurgeAsync)).Select(c => (DateTime)c.GetArguments()[0]!);

    [Test]
    public async Task AuditPurge_AtStartup_AndDailyAt0005Utc_ThirtyDaysBack()
    {
        var scheduler = Create("");
        await scheduler.StartAsync(CancellationToken.None);
        await Eventually(() => AuditPurges().Count() == 1);
        Assert.That(AuditPurges().Single(), Is.EqualTo(new DateTime(2026, 10, 6, 2, 0, 0, DateTimeKind.Utc)));

        await AdvanceTo(new DateTimeOffset(2026, 11, 6, 0, 5, 0, TimeSpan.Zero));
        await Eventually(() => AuditPurges().Count() == 2);
        Assert.That(AuditPurges().Last(), Is.EqualTo(new DateTime(2026, 10, 7, 0, 5, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task AFailingAuditPurge_IsLoggedAsAWarning_AndTheSchedulerKeepsRunning()
    {
        _audit.PurgeAsync(default, default).ReturnsForAnyArgs<Task<int>>(_ => throw new InvalidOperationException("db down"));
        var scheduler = Create("");
        await scheduler.StartAsync(CancellationToken.None);
        await Eventually(() => _log.Entries.Any(e => e.Message.Contains("Audit purge failed", StringComparison.Ordinal)));
        Assert.That(_log.Entries.First(e => e.Message.Contains("Audit purge failed", StringComparison.Ordinal)).Level, Is.EqualTo(LogLevel.Warning));

        await AdvanceTo(new DateTimeOffset(2026, 11, 6, 0, 5, 0, TimeSpan.Zero));
        await Eventually(() => AuditPurges().Count() == 2);
    }

    private sealed class ListLogger : ILogger<Scheduler>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
