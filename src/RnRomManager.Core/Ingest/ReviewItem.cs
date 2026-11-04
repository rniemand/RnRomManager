namespace RnRomManager.Core.Ingest;

/// <summary>A persisted review item (§14.1); JSON columns retain the wire representation.</summary>
public sealed record ReviewItem
{
    public long Id { get; init; }
    public string SystemId { get; init; } = "";
    public string Reason { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string? ArchivePath { get; init; }
    public string? ArchiveEntry { get; init; }
    public string OriginalFileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Crc32 { get; init; } = "";
    public string Md5 { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public string? Header { get; init; }
    public string Suggestions { get; init; } = "[]";
    public string? DatIdentification { get; init; }
    public string? CandidateGameIds { get; init; }
    public string Status { get; init; } = "Pending";
    public Guid? GameId { get; init; }
    public DateTime CreatedAt { get; init; }
    public Guid LastSeenJobId { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public int? ResolvedBy { get; init; }
}
