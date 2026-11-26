namespace RnRomManager.Core.Jobs;

/// <summary>A row of <c>jobs</c> (spec §14.1, §3.1). Times are UTC.</summary>
public sealed record Job
{
    public required Guid Id { get; init; }
    public required JobType Type { get; init; }
    public JobMode? Mode { get; init; }
    public required JobTrigger Trigger { get; init; }
    public required JobStatus Status { get; init; }
    public int? RequestedBy { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }

    /// <summary>JSON text (counts), set when the job ends.</summary>
    public string? Summary { get; init; }

    public string? Error { get; init; }
}
