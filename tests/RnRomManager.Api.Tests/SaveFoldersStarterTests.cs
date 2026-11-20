using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

/// <summary>Release editing B19: the save folder starter runs the job once unless the setting says it already ran.</summary>
public class SaveFoldersStarterTests
{
    /// <summary>Counts created timers, so a test advances time only once the starter's retry delay exists.</summary>
    private sealed class CountingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _timers;
        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }
    }

    private CountingTimeProvider _time = null!;
    private CancellationTokenSource _appStarted = null!;
    private JobLock _lock = null!;
    private IJobRepository _jobs = null!;
    private ISettingsRepository _settings = null!;
    private SaveFoldersStarter _starter = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new CountingTimeProvider(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));
        _appStarted = new CancellationTokenSource();
        _lock = new JobLock();
        _jobs = Substitute.For<IJobRepository>();
        _settings = Substitute.For<ISettingsRepository>();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(_appStarted.Token);
        var runner = new JobRunner(_jobs, _lock, Substitute.For<IUserRepository>(), _time, NullLogger<JobRunner>.Instance);
        _starter = new SaveFoldersStarter(lifetime, runner, () =>
        {
            var job = new NoopTestJob();
            job.Release.SetResult();
            return job;
        }, _settings, _time, NullLogger<SaveFoldersStarter>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _starter.StopAsync(CancellationToken.None);
        _starter.Dispose();
        _appStarted.Dispose();
    }

    private int Starts => _jobs.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IJobRepository.InsertAsync));

    private void Stored(string? value) =>
        _settings.GetAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(
            value is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [SaveFoldersJob.MigratedSetting] = value });

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.That(condition(), Is.True, "timed out");
    }

    [TestCase(null, 1)]
    [TestCase("false", 1)]
    [TestCase("true", 0)]
    public async Task StartsOnlyWhenNotMigrated_AfterApplicationStarted(string? stored, int starts)
    {
        Stored(stored);
        await _starter.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        await _settings.DidNotReceiveWithAnyArgs().GetAsync(default!, default);

        _appStarted.Cancel();
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(Starts, Is.EqualTo(starts));
        if (starts == 1)
            await _jobs.Received(1).InsertAsync(Arg.Is<Job>(j => j.Trigger == JobTrigger.Startup), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BusyLock_IsRetriedAfterFiveMinutes()
    {
        Stored(null);
        var held = _lock.TryAcquire()!;
        _appStarted.Cancel();
        await _starter.StartAsync(CancellationToken.None);
        await Until(() => _time.Timers == 1);
        Assert.That(Starts, Is.Zero);

        held.Dispose();
        _time.Advance(SaveFoldersStarter.RetryInterval);
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(Starts, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ApiFactory_KeepsTheStarter_OnlyWhenAsked(bool run)
    {
        using var factory = new ApiFactory { RunSaveFoldersStarter = run };

        Assert.That(factory.Services.GetServices<IHostedService>().OfType<SaveFoldersStarter>().Count(), Is.EqualTo(run ? 1 : 0));
    }
}
