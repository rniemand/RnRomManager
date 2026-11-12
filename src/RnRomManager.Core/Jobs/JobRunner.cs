using System.Text.Json;
using Microsoft.Extensions.Logging;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Users;

namespace RnRomManager.Core.Jobs;

/// <summary>
/// Runs one background job at a time (spec §3.1): takes the <see cref="JobLock"/>, writes the <c>jobs</c> row, runs
/// the job off the request with its progress in memory, and records the end status. The lock is always released,
/// even when the final row update fails (the row then stays <c>Running</c> until startup marks it <c>Aborted</c>).
/// </summary>
public sealed class JobRunner(IJobRepository jobs, JobLock jobLock, IUserRepository users, TimeProvider time, ILogger<JobRunner> logger)
{
    private static readonly JsonSerializerOptions SummaryJson = new(JsonSerializerDefaults.Web);

    private RunningJob? _current;

    public JobSnapshot? Current => Volatile.Read(ref _current)?.Snapshot(time.GetUtcNow().UtcDateTime);

    /// <returns>The started job, or null when the lock is busy (<c>409 JobRunning</c>).</returns>
    /// <exception cref="JobRefusedException"><see cref="IJob.Preflight"/> refused; the lock is released.</exception>
    /// <exception cref="Exception">The <c>jobs</c> insert failed; the lock is released.</exception>
    public async Task<StartedJob?> StartAsync(IJob job, JobTrigger trigger, int? requestedBy, CancellationToken cancellationToken)
    {
        var release = jobLock.TryAcquire();
        if (release is null)
            return null;

        Job row;
        try
        {
            if (job.Preflight() is { } refused)
                throw new JobRefusedException(refused);
            row = new Job
            {
                Id = Guid.NewGuid(),
                Type = job.Type,
                Mode = job.Mode,
                Trigger = trigger,
                Status = JobStatus.Running,
                RequestedBy = requestedBy,
                StartedAt = time.GetUtcNow().UtcDateTime,
            };
            await jobs.InsertAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            release.Dispose();
            throw;
        }

        var running = new RunningJob(row);
        Volatile.Write(ref _current, running);
        running.Completion = Task.Run(() => RunAsync(job, running, release), CancellationToken.None);
        return new StartedJob(row.Id, running.Completion);
    }

    /// <returns>False when <paramref name="id"/> isn't the running job (<c>404</c>).</returns>
    public bool Cancel(Guid id)
    {
        var running = Volatile.Read(ref _current);
        if (running?.Row.Id != id)
            return false;
        running.Cancellation.Cancel();
        return true;
    }

    /// <summary>Shutdown (SIGTERM): cancels the running job and waits for it until <paramref name="cancellationToken"/>.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var running = Volatile.Read(ref _current);
        if (running?.Completion is not { } completion)
            return;
        logger.LogInformation("Shutting down: cancelling job {JobId}", running.Row.Id);
        running.Cancellation.Cancel();
        try
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Job {JobId} didn't stop within the shutdown timeout; it becomes Aborted at the next start", running.Row.Id);
        }
    }

    /// <summary>
    /// AU3: the job's actor, built once. A failed or empty user lookup keeps the id, leaves the username null and logs a
    /// warning; the job still runs. Scheduled runs have no user.
    /// </summary>
    private async Task<AuditActor> ActorAsync(Job row)
    {
        if (row.RequestedBy is not { } userId)
            return new AuditActor(null, null, row.Id);
        try
        {
            if (await users.GetByIdAsync(userId, CancellationToken.None).ConfigureAwait(false) is { } user)
                return new AuditActor(userId, user.Username, row.Id);
            logger.LogWarning("Job {JobId}: user {UserId} not found; audit rows carry no username", row.Id, userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Job {JobId}: user {UserId} couldn't be loaded; audit rows carry no username", row.Id, userId);
        }
        return new AuditActor(userId, null, row.Id);
    }

    private async Task RunAsync(IJob job, RunningJob running, IDisposable release)
    {
        var row = running.Row;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = row.Id });
        var status = JobStatus.Completed;
        object? summary = null;
        string? error = null;
        try
        {
            logger.LogInformation("Job {Type} {Mode} started ({Trigger})", row.Type, row.Mode, row.Trigger);
            var actor = await ActorAsync(row).ConfigureAwait(false);
            summary = await job.RunAsync(new JobContext(row.Id, running.Progress, actor), running.Cancellation.Token).ConfigureAwait(false);
            if (running.Cancellation.IsCancellationRequested)
                status = JobStatus.Cancelled;
        }
        catch (OperationCanceledException) when (running.Cancellation.IsCancellationRequested)
        {
            status = JobStatus.Cancelled;
        }
        catch (Exception ex)
        {
            status = JobStatus.Failed;
            error = ex.Message;
            logger.LogError(ex, "Job {Type} failed", row.Type);
        }

        try
        {
            var summaryJson = summary is null ? null : JsonSerializer.Serialize(summary, SummaryJson);
            await jobs.FinishAsync(row.Id, status, time.GetUtcNow().UtcDateTime, summaryJson, error, CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation("Job {Type} ended {Status}: {Summary}", row.Type, status, summaryJson);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {Type} ended {Status} but its row couldn't be updated; it stays Running until the next start", row.Type, status);
        }
        finally
        {
            Volatile.Write(ref _current, null);
            release.Dispose();
        }
    }

    private sealed class RunningJob(Job row)
    {
        public Job Row { get; } = row;
        public JobProgress Progress { get; } = new();

        // Not disposed: Cancel() may race the job's end, and a CTS without timers holds nothing to free.
        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Completion { get; set; }

        public JobSnapshot Snapshot(DateTime nowUtc) => new(
            Row.Id, Row.Type, Row.Mode, JobStatus.Running, Progress.Current, Progress.Total, Progress.CurrentItem, Row.StartedAt,
            Progress.StepAt(nowUtc));
    }
}

/// <param name="Completion">Ends when the job's row is final (tests and shutdown wait on it).</param>
public sealed record StartedJob(Guid Id, Task Completion);
