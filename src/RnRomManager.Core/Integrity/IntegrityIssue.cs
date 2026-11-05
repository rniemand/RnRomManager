namespace RnRomManager.Core.Integrity;

/// <summary>A row of <c>integrity_issues</c> (spec §14.1).</summary>
public sealed record IntegrityIssue
{
    public const string Open = "Open";
    public const string AutoRepaired = "AutoRepaired";
    public const string Resolved = "Resolved";

    public long Id { get; init; }
    public required string Type { get; init; }
    public required string Severity { get; init; }
    public string? SystemId { get; init; }
    public string? ZipPath { get; init; }
    public Guid? GameId { get; init; }
    public string? EntryName { get; init; }
    public string? Details { get; init; }
    public required string Status { get; init; }
    public required Guid FirstSeenJobId { get; init; }
    public required Guid LastSeenJobId { get; init; }
    public required DateTime DetectedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
}
