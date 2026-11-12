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

    /// <summary>B9: a <c>.zip</c>/<c>.7z</c> for a <c>FileSets</c> system stays in ingest; nothing is extracted.</summary>
    public const string ArchiveNotSupported = "ArchiveNotSupported";

    /// <summary>B7: a set member waits in ingest: a file its cue names is missing, its cue is broken, or a lone data track of a multi-track disc.</summary>
    public const string IncompleteSet = "IncompleteSet";

    /// <summary>B7: the cue names a file that isn't a <c>.bin</c>, names itself, or has a name that can't be stored.</summary>
    public const string UnsupportedSetMember = "UnsupportedSetMember";

    /// <summary>B7: a file is named by two cues, or a cue's name matches two files that differ only by case.</summary>
    public const string SetConflict = "SetConflict";

    /// <summary>B7a: two or more games own the data track's base title; the set stays until the Admin merges or moves them.</summary>
    public const string AmbiguousGroup = "AmbiguousGroup";
}

/// <summary><c>ingest_review_items.reason</c> values added for disc sets (the older ones are literals at their call sites).</summary>
public static class ReviewReasons
{
    /// <summary>B7a: two or more members of a set match the DAT; the items can't be resolved, only deleted (B15).</summary>
    public const string AmbiguousSet = "AmbiguousSet";
}

/// <summary>A review row still <c>Pending</c>: just what step 5 needs to check its source still exists.</summary>
/// <param name="SystemId">Picks the storage mode, so external sources get the checked cleanup (spec I4).</param>
public sealed record PendingReviewSource(long Id, string SystemId, string SourcePath, string? ArchivePath);

public interface IIngestReviewRepository
{
    Task<ReviewItem?> GetAsync(long id, CancellationToken cancellationToken);

    Task<RnRomManager.Core.Database.Page<ReviewItem>> ListAsync(string status, int page, int pageSize, CancellationToken cancellationToken,
        ReviewFilter? filter = null);

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
    /// <summary>
    /// I4: the Pending loose-file row of <paramref name="originalPath"/> with this SHA1 now names
    /// <paramref name="restoredPath"/>, where a failed placement put its verified bytes back.
    /// </summary>
    Task RetargetPendingAsync(string originalPath, string sha1, string restoredPath, CancellationToken cancellationToken);

    Task<int> DeletePendingBySourceAsync(string sourcePath, string? archivePath, CancellationToken cancellationToken);
}

/// <summary>RV3: optional review-list filters; null fields are not applied. <paramref name="Query"/> is a trimmed substring of the file name or header title.</summary>
public sealed record ReviewFilter(string? SystemId = null, string? Reason = null, string? Query = null);
