namespace RnRomManager.Core.Jobs;

/// <summary>
/// The one global job lock (spec §3.1): background jobs, library edits and review resolutions all take it.
/// Never waits: busy means <c>409 JobRunning</c>.
/// </summary>
public sealed class JobLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <returns>A handle that releases the lock on dispose, or null when busy.</returns>
    public IDisposable? TryAcquire() => _semaphore.Wait(0) ? new Releaser(_semaphore) : null;

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
