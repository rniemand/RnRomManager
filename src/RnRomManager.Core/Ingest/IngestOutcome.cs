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
