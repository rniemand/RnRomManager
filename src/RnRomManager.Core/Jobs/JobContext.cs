using RnRomManager.Core.Audit;

namespace RnRomManager.Core.Jobs;

/// <param name="Actor">AU3: who the job's file actions are recorded for; null means a scheduled run (<c>new(null, null, JobId)</c>).</param>
public sealed record JobContext(Guid JobId, JobProgress Progress, AuditActor? Actor = null)
{
    public AuditActor Actor { get; } = Actor ?? new AuditActor(null, null, JobId);
}
