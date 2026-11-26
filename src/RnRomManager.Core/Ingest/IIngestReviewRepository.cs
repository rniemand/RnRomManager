namespace RnRomManager.Core.Ingest;

public interface IIngestReviewRepository
{
    Task<ReviewItem?> GetAsync(long id, CancellationToken cancellationToken);

    Task<RnRomManager.Core.Database.Page<ReviewItem>> ListAsync(
        string status,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        ReviewFilter? filter = null);

    Task SetResolutionAsync(
        long id,
        string status,
        Guid? gameId,
        int userId,
        DateTime now,
        CancellationToken cancellationToken);

    /// <summary>B14: marks every one of <paramref name="ids"/> in one transaction; when any isn't Pending, none changes and it throws.</summary>
    Task SetResolutionAsync(
        IReadOnlyList<long> ids,
        string status,
        Guid? gameId,
        int userId,
        DateTime now,
        CancellationToken cancellationToken);

    Task<ReviewItem?> GetBySha1Async(string sha1, CancellationToken cancellationToken);

    Task UpsertAsync(ReviewItem item, CancellationToken cancellationToken);

    Task CompleteSourceAsync(
        string sourcePath,
        string? archiveEntry,
        IngestItemResult result,
        Guid jobId,
        DateTime now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingReviewSource>> ListPendingSourcesAsync(CancellationToken cancellationToken);

    Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every <c>Pending</c> row of one source: all entries of <paramref name="archivePath"/> when set, else the
    /// loose file at <paramref name="sourcePath"/>. Resolved and dismissed rows stay as history (D2).
    /// </summary>
    /// <returns>The number of rows deleted.</returns>
    /// <summary>
    /// I4: the Pending loose-file row of <paramref name="originalPath"/> with this SHA1 now names
    /// <paramref name="restoredPath"/>, where a failed placement put its verified bytes back.
    /// </summary>
    Task RetargetPendingAsync(
        string originalPath,
        string sha1,
        string restoredPath,
        CancellationToken cancellationToken);

    Task<int> DeletePendingBySourceAsync(string sourcePath, string? archivePath, CancellationToken cancellationToken);

    /// <summary>B12: the <c>Pending</c> loose-file rows (<c>archive_path</c> null) whose exact <c>source_path</c> is one of <paramref name="sourcePaths"/>.</summary>
    Task<IReadOnlyList<ReviewItem>> ListPendingByPathsAsync(IReadOnlyCollection<string> sourcePaths, CancellationToken cancellationToken);
}
