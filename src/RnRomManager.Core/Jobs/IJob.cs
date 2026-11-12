using RnRomManager.Core.Audit;

namespace RnRomManager.Core.Jobs;

/// <summary>
/// A background job run by <see cref="JobRunner"/>. Check the token between items (§3.1: files, ZIPs, games,
/// downloads) and return normally or throw <see cref="OperationCanceledException"/>; either way the job ends
/// <c>Cancelled</c> and work already done is kept.
/// </summary>
public interface IJob
{
    JobType Type { get; }
    JobMode? Mode { get; }

    /// <summary>Checked with the lock held, before the <c>jobs</c> row: a §23.1 code refuses the start (409), e.g. <c>DatsMissing</c>.</summary>
    string? Preflight() => null;

    /// <returns>The summary (counts), serialized to <c>jobs.summary</c>; null for none.</returns>
    Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken);
}

/// <summary>The job's <see cref="IJob.Preflight"/> refused the start; no row was written and the lock is free again.</summary>
public sealed class JobRefusedException(string code) : Exception($"Job refused: {code}.")
{
    public string Code { get; } = code;
}

/// <param name="Actor">AU3: who the job's file actions are recorded for; null means a scheduled run (<c>new(null, null, JobId)</c>).</param>
public sealed record JobContext(Guid JobId, JobProgress Progress, AuditActor? Actor = null)
{
    public AuditActor Actor { get; } = Actor ?? new AuditActor(null, null, JobId);
}

/// <summary>Live progress (§3.1), held in memory only and read by <c>GET /api/jobs/current</c>.</summary>
public sealed class JobProgress
{
    private int _total;
    private int _current;
    private string? _currentItem;
    private StepState? _step;
    private long _stepDone;

    public int Total => Volatile.Read(ref _total);
    public int Current => Volatile.Read(ref _current);
    public string? CurrentItem => Volatile.Read(ref _currentItem);

    public void SetTotal(int total) => Volatile.Write(ref _total, total);

    /// <summary>Also ends any step (B3): a new item never shows the previous file's bar.</summary>
    public void SetCurrentItem(string? item)
    {
        EndStep();
        Volatile.Write(ref _currentItem, item);
    }

    /// <summary>One more item finished.</summary>
    public void ItemDone() => Interlocked.Increment(ref _current);

    /// <summary>
    /// A streamed phase (<see cref="JobStepPhases"/>) of the current item begins at 0 bytes. The byte count is reset
    /// before the step is published, and <see cref="StepAt"/> reads the step first, so a new step is never paired
    /// with the previous step's bytes.
    /// </summary>
    public void BeginStep(string phase, long bytesTotal, DateTime startedAtUtc)
    {
        Volatile.Write(ref _stepDone, 0);
        Volatile.Write(ref _step, new StepState(phase, bytesTotal, startedAtUtc));
    }

    /// <param name="bytesDone">Cumulative bytes of the current step.</param>
    public void StepAdvanced(long bytesDone) => Volatile.Write(ref _stepDone, bytesDone);

    public void EndStep() => Volatile.Write(ref _step, null);

    /// <summary>
    /// The running step, or null. <c>BytesPerSecond</c> is the average since the step began, null until a second
    /// has passed and some bytes are done (B4); <c>BytesDone</c> is clamped to the total (a file that grows).
    /// </summary>
    public JobStepSnapshot? StepAt(DateTime nowUtc)
    {
        var step = Volatile.Read(ref _step);
        if (step is null)
            return null;
        var done = Math.Clamp(Volatile.Read(ref _stepDone), 0, Math.Max(0, step.BytesTotal));
        var elapsed = (nowUtc - step.StartedAtUtc).TotalSeconds;
        long? perSecond = elapsed >= 1 && done > 0 ? (long)(done / elapsed) : null;
        return new JobStepSnapshot(step.Phase, done, step.BytesTotal, perSecond);
    }

    private sealed record StepState(string Phase, long BytesTotal, DateTime StartedAtUtc);
}

/// <summary>The streamed phases a job reports per file (spec B2).</summary>
public static class JobStepPhases
{
    public const string Hashing = "Hashing";
    public const string CopyingToLibrary = "CopyingToLibrary";
    public const string Copying = "Copying";
}

/// <summary>One streamed phase of the current item (<c>step</c> in <c>GET /api/jobs/current</c>).</summary>
public sealed record JobStepSnapshot(string Phase, long BytesDone, long BytesTotal, long? BytesPerSecond);

/// <summary>What <c>GET /api/jobs/current</c> returns.</summary>
public sealed record JobSnapshot(
    Guid Id, JobType Type, JobMode? Mode, JobStatus Status, int Current, int Total, string? CurrentItem, DateTime StartedAt,
    JobStepSnapshot? Step = null);
