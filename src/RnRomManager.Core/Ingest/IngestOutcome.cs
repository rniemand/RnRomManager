namespace RnRomManager.Core.Ingest;

/// <summary><c>job_items.outcome</c> values (spec §7.2).</summary>
public static class IngestOutcome
{
    public const string Ingested = "Ingested";
    public const string Repaired = "Repaired";
    public const string Duplicate = "Duplicate";
    public const string Review = "Review";
    public const string Failed = "Failed";
    public const string Error = "Error";
    public const string Skipped = "Skipped";

    /// <summary>All of them, for <c>summary</c> counts.</summary>
    public static IReadOnlyList<string> All { get; } = [Ingested, Repaired, Duplicate, Review, Failed, Error, Skipped];
}

/// <summary><c>job_items.reason</c> codes (spec §23.1) used so far.</summary>
public static class IngestReason
{
    public const string TooRecent = "TooRecent";
}

/// <summary>A review row still <c>Pending</c>: just what step 5 needs to check its source still exists.</summary>
public sealed record PendingReviewSource(long Id, string SourcePath, string? ArchivePath);

public interface IIngestReviewRepository
{
    Task<ReviewItem?> GetAsync(long id, CancellationToken cancellationToken);

    Task<RnRomManager.Core.Database.Page<ReviewItem>> ListAsync(string status, int page, int pageSize, CancellationToken cancellationToken);

    Task SetResolutionAsync(long id, string status, Guid? gameId, int userId, DateTime now, CancellationToken cancellationToken);

    Task<ReviewItem?> GetBySha1Async(string sha1, CancellationToken cancellationToken);

    Task UpsertAsync(ReviewItem item, CancellationToken cancellationToken);

    Task CompleteSourceAsync(string sourcePath, string? archiveEntry, IngestItemResult result, Guid jobId, DateTime now, CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingReviewSource>> ListPendingSourcesAsync(CancellationToken cancellationToken);

    Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every <c>Pending</c> row of one source: all entries of <paramref name="archivePath"/> when set, else the
    /// loose file at <paramref name="sourcePath"/>. Resolved and dismissed rows stay as history (D2).
    /// </summary>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeletePendingBySourceAsync(string sourcePath, string? archivePath, CancellationToken cancellationToken);
}
