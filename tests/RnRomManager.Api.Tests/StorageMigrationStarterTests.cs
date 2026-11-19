using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

/// <summary>S4: the starter waits for ApplicationStarted, starts the migration once, and retries a busy lock or a failed check.</summary>
public class StorageMigrationStarterTests
{
    private static readonly GameRow Legacy = new()
    {
        Id = Guid.NewGuid(), SystemId = LegacyExternalTestHandler.SystemId, Title = "Old", DisplayTitle = "Old", ZipPath = "legacy/O/Old.zip",
        Bucket = "O", Developers = [], Publishers = [], Genres = [], ZipSizeBytes = 1, ZipMtimeUtc = DateTime.UnixEpoch,
        InfoSchemaVersion = 6, CreatedAt = DateTime.UnixEpoch, UpdatedAt = DateTime.UnixEpoch,
    };

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
    private IGameRepository _games = null!;
    private RnRomManager.Core.Storage.IRomStorageSettings _romStorage = null!;
    private StorageMigrationStarter _starter = null!;
    private int _created;

    [SetUp]
    public void SetUp()
    {
        _time = new CountingTimeProvider(new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero));
        _appStarted = new CancellationTokenSource();
        _lock = new JobLock();
        _jobs = Substitute.For<IJobRepository>();
        _games = Substitute.For<IGameRepository>();
        _romStorage = Substitute.For<RnRomManager.Core.Storage.IRomStorageSettings>();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(_appStarted.Token);
        var runner = new JobRunner(_jobs, _lock, Substitute.For<IUserRepository>(), _time, NullLogger<JobRunner>.Instance);
        _created = 0;
        _starter = new StorageMigrationStarter(lifetime, runner, () =>
            {
                _created++;
                var job = new NoopTestJob();
                job.Release.SetResult();
                return job;
            }, _games, new SystemRegistry([new LegacyExternalTestHandler(_romStorage), new ExternalTestHandler()]), _time,
            NullLogger<StorageMigrationStarter>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _starter.StopAsync(CancellationToken.None);
        _starter.Dispose();
        _appStarted.Dispose();
    }

    private Task<int> Starts() => Task.FromResult(_jobs.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IJobRepository.InsertAsync)));

    private int Checks => _games.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IGameRepository.ListStorageMovesAsync));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.That(condition(), Is.True, "timed out");
    }

    private void Returns(params GameRow[] rows) =>
        _games.ListStorageMovesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(rows);

    [Test]
    public async Task NeverBlocksStartup_AndChecksOnlyAfterApplicationStarted()
    {
        Returns(Legacy);

        await _starter.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.That(Checks, Is.Zero);

        _appStarted.Cancel();
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(await Starts(), Is.EqualTo(1));
    }

    [Test]
    public async Task NoLegacyGames_StartsNothing()
    {
        Returns();
        _appStarted.Cancel();

        await _starter.StartAsync(CancellationToken.None);
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That((Checks, await Starts(), _created), Is.EqualTo((1, 0, 0)));
    }

    /// <summary>B11: resumes moves in either direction; the system goes to the list of its current storage.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingGames_StartTheJobOnce_ForTheMovableSystemsOnly(bool embed)
    {
        _romStorage.Override(LegacyExternalTestHandler.SystemId).Returns(embed);
        Returns(Legacy);
        _appStarted.Cancel();

        await _starter.StartAsync(CancellationToken.None);
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(await Starts(), Is.EqualTo(1));
        string[] movable = [LegacyExternalTestHandler.SystemId], none = [];
        await _games.Received(1).ListStorageMovesAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(embed ? movable : none)),
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(embed ? none : movable)), Arg.Any<CancellationToken>());
        await _jobs.Received(1).InsertAsync(Arg.Is<Job>(j => j.Trigger == JobTrigger.Startup && j.RequestedBy == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BusyLock_IsRetriedAfterFiveMinutes()
    {
        Returns(Legacy);
        var held = _lock.TryAcquire()!;
        _appStarted.Cancel();
        await _starter.StartAsync(CancellationToken.None);
        await Until(() => _time.Timers == 1);
        Assert.That((Checks, await Starts()), Is.EqualTo((1, 0)));

        held.Dispose();
        _time.Advance(StorageMigrationStarter.RetryInterval - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.That(Checks, Is.EqualTo(1), "not before 5 minutes");
        _time.Advance(TimeSpan.FromSeconds(1));
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That((Checks, await Starts()), Is.EqualTo((2, 1)));
    }

    [Test]
    public async Task AFailedCheck_IsRetried()
    {
        _games.ListStorageMovesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("db down"), _ => Task.FromResult<IReadOnlyList<GameRow>>([Legacy]));
        _appStarted.Cancel();
        await _starter.StartAsync(CancellationToken.None);
        await Until(() => _time.Timers == 1);

        _time.Advance(StorageMigrationStarter.RetryInterval);
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(await Starts(), Is.EqualTo(1));
    }

    [Test]
    public async Task AStartedRun_IsNeverRetried_WhateverItsOutcome()
    {
        Returns(Legacy);
        _appStarted.Cancel();
        await _starter.StartAsync(CancellationToken.None);
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        // The run ended (its items may be Error), and legacy games still remain.
        _time.Advance(StorageMigrationStarter.RetryInterval * 3);
        await Task.Delay(50);

        Assert.That((Checks, await Starts()), Is.EqualTo((1, 1)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ApiFactory_KeepsTheStarter_OnlyWhenAsked(bool run)
    {
        using var factory = new ApiFactory { RunStorageMigrationStarter = run };

        var starters = factory.Services.GetServices<IHostedService>().OfType<StorageMigrationStarter>();

        Assert.That(starters.Count(), Is.EqualTo(run ? 1 : 0));
    }
}
