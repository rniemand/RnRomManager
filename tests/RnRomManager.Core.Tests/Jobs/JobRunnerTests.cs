using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Microsoft.Extensions.Logging;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Jobs;

/// <summary>Spec §3.1: lock, status transitions, cancel, failure, final-update failure, shutdown.</summary>
public class JobRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private IJobRepository _jobs = null!;
    private JobLock _lock = null!;
    private JobRunner _runner = null!;
    private IUserRepository _users = null!;
    private ILogger<JobRunner> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _jobs = Substitute.For<IJobRepository>();
        _lock = new JobLock();
        _users = Substitute.For<IUserRepository>();
        _users.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(new User { Id = 7, Username = "boss", PasswordHash = "x", SecurityStamp = "s", CreatedAt = default, UpdatedAt = default });
        _logger = Substitute.For<ILogger<JobRunner>>();
        _runner = new JobRunner(_jobs, _lock, _users, TimeProvider.System, _logger);
    }

    private async Task<(NoopTestJob Job, StartedJob Started)> StartAsync(NoopTestJob? job = null)
    {
        job ??= new NoopTestJob();
        var started = await _runner.StartAsync(job, JobTrigger.Manual, requestedBy: 7, CancellationToken.None);
        Assert.That(started, Is.Not.Null);
        await job.Started.Task.WaitAsync(Timeout);
        return (job, started!);
    }

    private Task FinishedWith(Guid id, JobStatus status, string? summary = null, string? error = null) =>
        _jobs.Received(1).FinishAsync(id, status, Arg.Any<DateTime>(), summary, error, Arg.Any<CancellationToken>());

    private int Warnings() =>
        _logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);

    [Test]
    public async Task Actor_HasTheJobIdAndTheRequestersUsername()
    {
        var (job, started) = await StartAsync();
        job.Release.TrySetResult();
        await started.Completion;

        Assert.That(job.Context!.Actor, Is.EqualTo(new AuditActor(7, "boss", started.Id)));
    }

    [Test]
    public async Task Actor_ForAScheduledRun_HasNoUser()
    {
        var job = new NoopTestJob();
        var started = await _runner.StartAsync(job, JobTrigger.Schedule, requestedBy: null, CancellationToken.None);
        await job.Started.Task.WaitAsync(Timeout);
        job.Release.TrySetResult();
        await started!.Completion;

        Assert.That(job.Context!.Actor, Is.EqualTo(new AuditActor(null, null, started.Id)));
        await _users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Actor_WhenTheUserLookupThrowsOrFindsNobody_KeepsTheId_WarnsAndRuns(bool throws)
    {
        if (throws)
            _users.GetByIdAsync(7, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));
        else
            _users.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns((User?)null);

        var (job, started) = await StartAsync();
        job.Release.TrySetResult();
        await started.Completion;

        Assert.That(job.Context!.Actor, Is.EqualTo(new AuditActor(7, null, started.Id)));
        Assert.That(Warnings(), Is.EqualTo(1));
        await FinishedWith(started.Id, JobStatus.Completed, "{\"done\":1}");
    }

    [Test]
    public void JobContext_WithoutAnActor_IsTheJobAsSystem()
    {
        var id = Guid.NewGuid();
        Assert.That(new JobContext(id, new JobProgress()).Actor, Is.EqualTo(new AuditActor(null, null, id)));
    }

    [Test]
    public async Task Start_InsertsRunningRow_ThenCompletesWithSummary()
    {
        var (job, started) = await StartAsync();

        await _jobs.Received(1).InsertAsync(Arg.Is<Job>(j => j.Id == started.Id && j.Type == JobType.Integrity && j.Mode == JobMode.Quick
            && j.Trigger == JobTrigger.Manual && j.Status == JobStatus.Running && j.RequestedBy == 7), Arg.Any<CancellationToken>());
        job.Release.SetResult();
        await started.Completion.WaitAsync(Timeout);

        await FinishedWith(started.Id, JobStatus.Completed, """{"done":1}""");
        Assert.That(_runner.Current, Is.Null);
        Assert.That(_lock.TryAcquire(), Is.Not.Null, "lock released");
    }

    [Test]
    public async Task Current_ShowsLiveProgress()
    {
        var (job, started) = await StartAsync();

        var current = _runner.Current;
        Assert.That(current, Is.EqualTo(current! with
        {
            Id = started.Id, Type = JobType.Integrity, Mode = JobMode.Quick, Status = JobStatus.Running,
            Current = 0, Total = 3, CurrentItem = "item-1",
        }));
        job.Release.SetResult();
        await started.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task LockBusy_ReturnsNull_NoRow()
    {
        var (job, started) = await StartAsync();

        Assert.That(await _runner.StartAsync(new NoopTestJob(), JobTrigger.Manual, null, CancellationToken.None), Is.Null);
        await _jobs.Received(1).InsertAsync(Arg.Any<Job>(), Arg.Any<CancellationToken>());
        job.Release.SetResult();
        await started.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task LockHeldByALibraryEdit_ReturnsNull()
    {
        using var edit = _lock.TryAcquire();

        Assert.That(await _runner.StartAsync(new NoopTestJob(), JobTrigger.Manual, null, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Cancel_EndsCancelled()
    {
        var (_, started) = await StartAsync();

        Assert.That(_runner.Cancel(started.Id), Is.True);
        await started.Completion.WaitAsync(Timeout);

        await FinishedWith(started.Id, JobStatus.Cancelled);
        Assert.That(_runner.Current, Is.Null);
    }

    [Test]
    public async Task Cancel_OtherId_IsFalse()
    {
        var (job, started) = await StartAsync();

        Assert.That(_runner.Cancel(Guid.NewGuid()), Is.False);
        job.Release.SetResult();
        await started.Completion.WaitAsync(Timeout);
        await FinishedWith(started.Id, JobStatus.Completed, """{"done":1}""");
    }

    [Test]
    public void Cancel_WhenIdle_IsFalse() => Assert.That(_runner.Cancel(Guid.NewGuid()), Is.False);

    [Test]
    public async Task Exception_EndsFailedWithError()
    {
        var (job, started) = await StartAsync(new NoopTestJob { Throw = new InvalidOperationException("DAT download failed") });

        job.Release.SetResult();
        await started.Completion.WaitAsync(Timeout);

        await FinishedWith(started.Id, JobStatus.Failed, error: "DAT download failed");
        Assert.That(_lock.TryAcquire(), Is.Not.Null);
    }

    [Test]
    public async Task FinalUpdateFails_LockStillReleased()
    {
        _jobs.FinishAsync(default, default, default, default, default, default).ReturnsForAnyArgs(Task.FromException(new TimeoutException("DB down")));
        var (job, started) = await StartAsync();

        job.Release.SetResult();
        await started.Completion.WaitAsync(Timeout);

        Assert.That(_runner.Current, Is.Null);
        Assert.That(_lock.TryAcquire(), Is.Not.Null);
    }

    [Test]
    public async Task InsertFails_LockReleased()
    {
        _jobs.InsertAsync(default!, default).ThrowsAsyncForAnyArgs(new TimeoutException("DB down"));

        await Assert.ThrowsAsync<TimeoutException>(() => _runner.StartAsync(new NoopTestJob(), JobTrigger.Manual, null, CancellationToken.None));
        Assert.That(_lock.TryAcquire(), Is.Not.Null);
        Assert.That(_runner.Current, Is.Null);
    }

    [Test]
    public async Task Stop_CancelsTheRunningJob()
    {
        var (_, started) = await StartAsync();

        await _runner.StopAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.That(started.Completion.IsCompleted, Is.True);
        await FinishedWith(started.Id, JobStatus.Cancelled);
    }

    [Test]
    public async Task Stop_GivesUpAtTheTimeout_JobStaysRunning()
    {
        var (job, started) = await StartAsync(new NoopTestJob { IgnoreCancel = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await _runner.StopAsync(timeout.Token).WaitAsync(Timeout);

        Assert.That(started.Completion.IsCompleted, Is.False);
        await _jobs.DidNotReceiveWithAnyArgs().FinishAsync(default, default, default, default, default, default);
        job.Release.SetResult();
        await started.Completion.WaitAsync(Timeout);
    }

    [Test]
    public Task Stop_WhenIdle_ReturnsAtOnce() => _runner.StopAsync(CancellationToken.None).WaitAsync(Timeout);
}
