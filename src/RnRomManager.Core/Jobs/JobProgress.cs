namespace RnRomManager.Core.Jobs;

/// <summary>Live progress (§3.1), held in memory only and read by <c>GET /api/jobs/current</c>.</summary>
public sealed class JobProgress
{
    private int _total;
    private int _current;
    private string? _currentItem;
    private StepState? _step;
    private long _stepDone;

    public int Total => Volatile.Read(ref _total);
    public int Current => Volatile.Read(ref _current);
    public string? CurrentItem => Volatile.Read(ref _currentItem);

    public void SetTotal(int total) => Volatile.Write(ref _total, total);

    /// <summary>Also ends any step (B3): a new item never shows the previous file's bar.</summary>
    public void SetCurrentItem(string? item)
    {
        EndStep();
        Volatile.Write(ref _currentItem, item);
    }

    /// <summary>One more item finished.</summary>
    public void ItemDone() => Interlocked.Increment(ref _current);

    /// <summary>
    /// A streamed phase (<see cref="JobStepPhases"/>) of the current item begins at 0 bytes. The byte count is reset
    /// before the step is published, and <see cref="StepAt"/> reads the step first, so a new step is never paired
    /// with the previous step's bytes.
    /// </summary>
    public void BeginStep(string phase, long bytesTotal, DateTime startedAtUtc)
    {
        Volatile.Write(ref _stepDone, 0);
        Volatile.Write(ref _step, new StepState(phase, bytesTotal, startedAtUtc));
    }

    /// <param name="bytesDone">Cumulative bytes of the current step.</param>
    public void StepAdvanced(long bytesDone) => Volatile.Write(ref _stepDone, bytesDone);

    public void EndStep() => Volatile.Write(ref _step, null);

    /// <summary>
    /// The running step, or null. <c>BytesPerSecond</c> is the average since the step began, null until a second
    /// has passed and some bytes are done (B4); <c>BytesDone</c> is clamped to the total (a file that grows).
    /// </summary>
    public JobStepSnapshot? StepAt(DateTime nowUtc)
    {
        var step = Volatile.Read(ref _step);
        if (step is null)
            return null;
        var done = Math.Clamp(Volatile.Read(ref _stepDone), 0, Math.Max(0, step.BytesTotal));
        var elapsed = (nowUtc - step.StartedAtUtc).TotalSeconds;
        long? perSecond = elapsed >= 1 && done > 0 ? (long)(done / elapsed) : null;
        return new JobStepSnapshot(step.Phase, done, step.BytesTotal, perSecond);
    }
}
