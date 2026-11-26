using RnRomManager.Core.Database;

namespace RnRomManager.Core.Jobs;

public interface IJobItemRepository
{
    Task<long> InsertAsync(JobItem item, CancellationToken cancellationToken);

    /// <summary>In insert order.</summary>
    Task<Page<JobItem>> ListAsync(
        Guid jobId,
        string? outcome,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
