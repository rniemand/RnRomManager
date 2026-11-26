namespace RnRomManager.Core.Jobs;

internal sealed class Releaser(JobLock owner) : IDisposable
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
