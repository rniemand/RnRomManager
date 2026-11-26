namespace RnRomManager.Core.Jobs;

/// <summary>
/// The one global job lock (spec §3.1): background jobs, library edits and review resolutions all take it.
/// Never waits: busy means <c>409 JobRunning</c>.
/// </summary>
public sealed class JobLock
{
    internal readonly SemaphoreSlim _semaphore = new(1, 1);
    internal volatile JobLockActivity? _activity;

    /// <summary>L1: the holder's activity, or null when the lock is free.</summary>
    public JobLockActivity? Activity => _activity;

    /// <param name="activity">L1: shown to users while the lock is held (e.g. "Saving changes").</param>
    /// <returns>A handle that releases the lock on dispose, or null when busy.</returns>
    public IDisposable? TryAcquire(string activity, JobType? jobType = null)
    {
        if (!_semaphore.Wait(0))
            return null;
        _activity = new JobLockActivity(activity, jobType);
        return new Releaser(this);
    }
}
