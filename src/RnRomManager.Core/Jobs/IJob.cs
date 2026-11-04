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

    /// <returns>The summary (counts), serialized to <c>jobs.summary</c>; null for none.</returns>
    Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken);
}

public sealed record JobContext(Guid JobId, JobProgress Progress);

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
