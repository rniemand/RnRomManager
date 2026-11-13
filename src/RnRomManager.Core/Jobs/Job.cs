namespace RnRomManager.Core.Jobs;

public enum JobType
{
    Ingest,
    Integrity,
    DatRefresh,
    MediaRetry,
    ReviewResolve,
}

public enum JobMode
{
    Quick,
    Deep,

    /// <summary>B15: an integrity check of one game's ZIP, as deep as <see cref="Deep"/>.</summary>
    Game,
}

public enum JobTrigger
{
    Manual,
    Schedule,
}

public enum JobStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,

    /// <summary>Still <c>Running</c> at startup: the process died mid-job (§7.4).</summary>
    Aborted,
}

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
