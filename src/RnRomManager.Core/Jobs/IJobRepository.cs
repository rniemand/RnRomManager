using RnRomManager.Core.Database;

namespace RnRomManager.Core.Jobs;

public interface IJobRepository
{
    Task InsertAsync(Job job, CancellationToken cancellationToken);

    Task FinishAsync(Guid id, JobStatus status, DateTime finishedAt, string? summary, string? error, CancellationToken cancellationToken);

    Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<Page<Job>> ListAsync(JobType? type, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Marks every <c>Running</c> row <c>Aborted</c> (§7.4).</summary>
    /// <returns>The ids it aborted.</returns>
    Task<IReadOnlyList<Guid>> AbortRunningAsync(DateTime finishedAt, CancellationToken cancellationToken);
}

public interface IJobItemRepository
{
    Task<long> InsertAsync(JobItem item, CancellationToken cancellationToken);

    /// <summary>In insert order.</summary>
    Task<Page<JobItem>> ListAsync(Guid jobId, string? outcome, int page, int pageSize, CancellationToken cancellationToken);
}
