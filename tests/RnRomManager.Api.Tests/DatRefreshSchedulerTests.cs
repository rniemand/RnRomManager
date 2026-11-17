using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Api.Tests;

/// <summary>B29: the daily DAT refresh at <c>dats.autoRefresh.time</c> (local), waiting while the job lock is busy.</summary>
public class DatRefreshSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 2, 0, 0, TimeSpan.Zero);

    private FakeTimeProvider _time = null!;
    private ConcurrentDictionary<string, string> _stored = null!;
    private IJobRepository _jobs = null!;
    private JobLock _lock = null!;
    private ListLogger _log = null!;
    private List<DatJob> _created = null!;
    private DatRefreshSettingsService _settings = null!;
    private DatRefreshScheduler? _scheduler;

    [SetUp]
    public void SetUp()
    {
        // UTC is "local" here, so the time is in UTC.
        _time = new FakeTimeProvider(T0);
        _stored = new ConcurrentDictionary<string, string>();
        var repository = Substitute.For<ISettingsRepository>();
        repository.GetAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<string, string>)call.Arg<IReadOnlyCollection<string>>()
                .Where(_stored.ContainsKey).ToDictionary(k => k, k => _stored[k]));
        repository.SetAsync(Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                foreach (var (k, v) in call.Arg<IReadOnlyDictionary<string, string>>()) _stored[k] = v;
                return Task.CompletedTask;
            });
        _settings = new DatRefreshSettingsService(repository, TimeZoneInfo.Utc, _time, NullLogger<DatRefreshSettingsService>.Instance);
        _jobs = Substitute.For<IJobRepository>();
        _lock = new JobLock();
        _log = new ListLogger();
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
    }

    private async Task<DatRefreshScheduler> StartAsync()
    {
        var runner = new JobRunner(_jobs, _lock, Substitute.For<RnRomManager.Core.Users.IUserRepository>(), _time, NullLogger<JobRunner>.Instance);
        _scheduler = new DatRefreshScheduler(_settings, () =>
        {
            var job = new DatJob();
            lock (_created) _created.Add(job);
            return job;
        }, runner, TimeZoneInfo.Utc, _time, _log);
        await _scheduler.StartAsync(CancellationToken.None);
        return _scheduler;
    }

    private bool Logged(string text) => _log.Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal));

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

    /// <summary>Moves the fake clock once the scheduler is waiting (its timer exists).</summary>
    private async Task AdvanceTo(DateTimeOffset at)
    {
        await Task.Delay(50);
        _time.Advance(at - _time.GetUtcNow());
    }

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    [Test]
    public async Task ByDefault_StartsOneScheduledRefreshAt0400_WithNoUser_ThenWaitsForTheNextDay()
    {
        await StartAsync();
        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-05T04:00:00"));

        await AdvanceTo(At(5, 4));
        await Eventually(() => _created.Count == 1);

        await _jobs.Received(1).InsertAsync(Arg.Is<Job>(j => j.Type == JobType.DatRefresh && j.Trigger == JobTrigger.Schedule && j.RequestedBy == null),
            Arg.Any<CancellationToken>());
        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-06T04:00:00"));
    }

    [Test]
    public async Task Disabled_DoesNothing()
    {
        _stored[DatRefreshSettings.EnabledKey] = "false";
        await StartAsync();
        await Eventually(() => Logged("Next scheduled DAT refresh: disabled"));

        await AdvanceTo(At(6, 5));
        await Task.Delay(100);

        Assert.That(_created, Is.Empty);
    }

    [Test]
    public async Task Saving_ReschedulesAtOnce()
    {
        await StartAsync();
        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-05T04:00:00"));

        await _settings.SaveAsync(new DatRefreshSettings(true, "02:10"), default);
        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-05T02:10:00"));
        await AdvanceTo(At(5, 2, 10));

        await Eventually(() => _created.Count == 1);
    }

    [Test]
    public async Task NotifySettingsChanged_RereadsTheStoredSettings()
    {
        await StartAsync();
        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-05T04:00:00"));

        _stored[DatRefreshSettings.EnabledKey] = "false";
        _settings.NotifySettingsChanged();

        await Eventually(() => Logged("Next scheduled DAT refresh: disabled"));
    }

    [Test]
    public async Task BusyLock_RetriesEveryMinute_UntilTheJobStarts()
    {
        var held = _lock.TryAcquire()!;
        await StartAsync();
        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-05T04:00:00"));

        await AdvanceTo(At(5, 4));
        await Eventually(() => Logged("another job is running"));
        await AdvanceTo(At(5, 4, 1));
        await Eventually(() => _log.Entries.Count(e => e.Message.Contains("another job is running", StringComparison.Ordinal)) == 2);
        await _jobs.DidNotReceiveWithAnyArgs().InsertAsync(default!, default);

        held.Dispose();
        await AdvanceTo(At(5, 4, 2));

        await Eventually(() => _jobs.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IJobRepository.InsertAsync)));
        Assert.That(_log.Entries.Where(e => e.Message.Contains("another job is running", StringComparison.Ordinal)).Select(e => e.Level),
            Is.All.EqualTo(LogLevel.Information));
        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-06T04:00:00"));
    }

    [Test]
    public async Task BusyUntilTheNextDueTime_SkipsThatDay_AndTriesTheNextAtOnce()
    {
        var held = _lock.TryAcquire()!;
        await StartAsync();
        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-05T04:00:00"));
        await AdvanceTo(At(5, 4));
        await Eventually(() => Logged("another job is running"));

        // Each retry is a minute apart; jump the clock to the next day's due time one wait at a time.
        while (!Logged("skipped"))
        {
            await Task.Delay(20);
            _time.Advance(TimeSpan.FromMinutes(30));
        }

        var skip = _log.Entries.Single(e => e.Message.Contains("skipped", StringComparison.Ordinal));
        Assert.That(skip.Level, Is.EqualTo(LogLevel.Information));
        Assert.That(skip.Message, Does.Contain("2026-11-05T04:00:00"));
        held.Dispose();
        await Task.Delay(20);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Eventually(() => _jobs.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IJobRepository.InsertAsync)));
        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-07T04:00:00"));
    }

    [Test]
    public async Task MissedRun_IsNotCaughtUp()
    {
        _time.Advance(At(5, 5) - T0);

        await StartAsync();

        await Eventually(() => Logged("Next scheduled DAT refresh: 2026-11-06T04:00:00"));
        Assert.That(_created, Is.Empty);
    }

    /// <summary>A DAT refresh stand-in that finishes at once.</summary>
    private sealed class DatJob : IJob
    {
        public JobType Type => JobType.DatRefresh;
        public JobMode? Mode => null;
        public Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
    }

    private sealed class ListLogger : ILogger<DatRefreshScheduler>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
