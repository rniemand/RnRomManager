namespace RnRomManager.Core.Ingest;

/// <summary>A review row still <c>Pending</c>: just what step 5 needs to check its source still exists.</summary>
/// <param name="SystemId">Picks the storage mode, so external sources get the checked cleanup (spec I4).</param>
public sealed record PendingReviewSource(
    long Id,
    string SystemId,
    string SourcePath,
    string? ArchivePath);
