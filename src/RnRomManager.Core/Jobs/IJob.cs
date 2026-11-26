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

    /// <summary>B4: false for a job that must run to its end; <c>Cancel</c> and shutdown leave it alone.</summary>
    bool Cancellable => true;

    /// <returns>The summary (counts), serialized to <c>jobs.summary</c>; null for none.</returns>
    Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken);
}
