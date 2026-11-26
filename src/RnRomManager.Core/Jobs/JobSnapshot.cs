namespace RnRomManager.Core.Jobs;

/// <summary>What <c>GET /api/jobs/current</c> returns.</summary>
public sealed record JobSnapshot(
    Guid Id,
    JobType Type,
    JobMode? Mode,
    JobStatus Status,
    int Current,
    int Total,
    string? CurrentItem,
    DateTime StartedAt,
    JobStepSnapshot? Step = null);
