namespace RnRomManager.Core.Jobs;

public enum JobStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,

    /// <summary>Still <c>Running</c> at startup: the process died mid-job (§7.4).</summary>
    Aborted,
}
