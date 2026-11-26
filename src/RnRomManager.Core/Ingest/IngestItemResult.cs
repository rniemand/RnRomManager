namespace RnRomManager.Core.Ingest;

/// <summary>What happened to one ingest item: becomes its <c>job_items</c> row.</summary>
public sealed record IngestItemResult(
    string Outcome,
    string? Reason = null,
    string? Details = null,
    string? SystemId = null,
    Guid? GameId = null,
    string? Sha1 = null)
{
    /// <summary>Only affects archive fate: a previously resolved entry counts as ingested (§7.3).</summary>
    public bool ResolvedArchiveDuplicate { get; init; }

    public static IngestItemResult Error(
        string reason,
        string? details,
        string? systemId = null,
        string? sha1 = null) =>
        new(IngestOutcome.Error, reason, details, systemId, Sha1: sha1);
}
