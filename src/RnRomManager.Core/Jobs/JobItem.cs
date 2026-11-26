namespace RnRomManager.Core.Jobs;

/// <summary>A row of <c>job_items</c> (spec §14.1): one per ingest item, scoped by <see cref="FileId"/>.</summary>
public sealed record JobItem
{
    public long Id { get; init; }
    public required Guid JobId { get; init; }
    public required Guid FileId { get; init; }
    public string? SystemId { get; init; }
    public required string SourcePath { get; init; }
    public string? ArchiveEntry { get; init; }
    public required string Outcome { get; init; }

    /// <summary>A §23.1 code, e.g. <c>VerifyFailed</c>.</summary>
    public string? Reason { get; init; }

    public string? Details { get; init; }
    public Guid? GameId { get; init; }
    public string? Sha1 { get; init; }
    public required DateTime CreatedAt { get; init; }
}
