using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

/// <summary>SG-1000 S13: the starter starts the move when there are candidates, and retries a busy lock or a failed check.</summary>
public class Sg1000MoveStarterTests
{
    private static readonly GameRow Candidate = new()
    {
        Id = Guid.NewGuid(), SystemId = "sms", Title = "X", DisplayTitle = "X", ZipPath = "sms/X/X.zip",
        Bucket = "X", Developers = [], Publishers = [], Genres = [], ZipSizeBytes = 1, ZipMtimeUtc = DateTime.UnixEpoch,
        InfoSchemaVersion = 6, CreatedAt = DateTime.UnixEpoch, UpdatedAt = DateTime.UnixEpoch,
    };

    private StorageMigrationStarterCountingTimeProvider _time = null!;
    private CancellationTokenSource _appStarted = null!;
    private JobLock _lock = null!;
    private IJobRepository _jobs = null!;
    private IGameRepository _games = null!;
    private Sg1000MoveStarter _starter = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new StorageMigrationStarterCountingTimeProvider(new DateTimeOffset(2026, 11, 27, 8, 0, 0, TimeSpan.Zero));
        _appStarted = new CancellationTokenSource();
        _lock = new JobLock();
        _jobs = Substitute.For<IJobRepository>();
        _games = Substitute.For<IGameRepository>();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(_appStarted.Token);
        var runner = new JobRunner(_jobs, _lock, Substitute.For<IUserRepository>(), _time, NullLogger<JobRunner>.Instance);
        _starter = new Sg1000MoveStarter(lifetime, runner, () =>
        {
            var job = new NoopTestJob();
            job.Release.SetResult();
            return job;
        }, _games, _time, NullLogger<Sg1000MoveStarter>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _starter.StopAsync(CancellationToken.None);
        _starter.Dispose();
        _appStarted.Dispose();
    }

    private int Starts => _jobs.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IJobRepository.InsertAsync));

    private int Checks => _games.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IGameRepository.ListByOriginalExtensionAsync));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.That(condition(), Is.True, "timed out");
    }

    private void Returns(params GameRow[] rows) =>
        _games.ListByOriginalExtensionAsync("sms", ".sg", Arg.Any<CancellationToken>()).Returns(rows);

    private async Task StartAsync()
    {
        _appStarted.Cancel();
        await _starter.StartAsync(CancellationToken.None);
    }

    [Test]
    public async Task NoCandidates_StartsNothing()
    {
        Returns();
        await StartAsync();
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That((Checks, Starts), Is.EqualTo((1, 0)));
    }

    [Test]
    public async Task Candidates_AndAFreeLock_StartOneStartupRun()
    {
        Returns(Candidate);
        await StartAsync();
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(Starts, Is.EqualTo(1));
        await _jobs.Received(1).InsertAsync(Arg.Is<Job>(j => j.Trigger == JobTrigger.Startup && j.RequestedBy == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BusyLock_IsRetriedAfterTheRetryInterval()
    {
        Returns(Candidate);
        var held = _lock.TryAcquire("test")!;
        await StartAsync();
        await Until(() => _time.Timers == 1);
        Assert.That((Checks, Starts), Is.EqualTo((1, 0)));

        held.Dispose();
        _time.Advance(Sg1000MoveStarter.RetryInterval);
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That((Checks, Starts), Is.EqualTo((2, 1)));
    }

    [Test]
    public async Task AFailedCheck_IsRetried()
    {
        _games.ListByOriginalExtensionAsync("sms", ".sg", Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("db down"), _ => Task.FromResult<IReadOnlyList<GameRow>>([Candidate]));
        await StartAsync();
        await Until(() => _time.Timers == 1);

        _time.Advance(Sg1000MoveStarter.RetryInterval);
        await _starter.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(Starts, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ApiFactory_KeepsTheStarter_OnlyWhenAsked(bool run)
    {
        using var factory = new ApiFactory { RunSg1000MoveStarter = run };

        var starters = factory.Services.GetServices<IHostedService>().OfType<Sg1000MoveStarter>();

        Assert.That(starters.Count(), Is.EqualTo(run ? 1 : 0));
    }
}
