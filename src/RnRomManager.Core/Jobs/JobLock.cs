namespace RnRomManager.Core.Jobs;

/// <summary>L1: what holds the job lock: a job (its <see cref="JobType"/>) or another activity such as "Saving changes".</summary>
public sealed record JobLockActivity(string Activity, JobType? JobType);

/// <summary>
/// The one global job lock (spec §3.1): background jobs, library edits and review resolutions all take it.
/// Never waits: busy means <c>409 JobRunning</c>.
/// </summary>
public sealed class JobLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private volatile JobLockActivity? _activity;

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

    private sealed class Releaser(JobLock owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            owner._activity = null;
            owner._semaphore.Release();
        }
    }
}
