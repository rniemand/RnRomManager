namespace RnRomManager.Core.Jobs;

internal sealed class RunningJob(Job row, bool cancellable)
{
    public Job Row { get; } = row;
    public bool Cancellable { get; } = cancellable;
    public JobProgress Progress { get; } = new();

    // Not disposed: Cancel() may race the job's end, and a CTS without timers holds nothing to free.
    public CancellationTokenSource Cancellation { get; } = new();

    public Task? Completion { get; set; }

    public JobSnapshot Snapshot(DateTime nowUtc) => new(
        Row.Id, Row.Type, Row.Mode, JobStatus.Running, Progress.Current, Progress.Total, Progress.CurrentItem, Row.StartedAt,
        Progress.StepAt(nowUtc));
}
