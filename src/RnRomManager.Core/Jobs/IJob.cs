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

    public int Total => Volatile.Read(ref _total);
    public int Current => Volatile.Read(ref _current);
    public string? CurrentItem => Volatile.Read(ref _currentItem);

    public void SetTotal(int total) => Volatile.Write(ref _total, total);

    public void SetCurrentItem(string? item) => Volatile.Write(ref _currentItem, item);

    /// <summary>One more item finished.</summary>
    public void ItemDone() => Interlocked.Increment(ref _current);
}

/// <summary>What <c>GET /api/jobs/current</c> returns.</summary>
public sealed record JobSnapshot(
    Guid Id, JobType Type, JobMode? Mode, JobStatus Status, int Current, int Total, string? CurrentItem, DateTime StartedAt);
