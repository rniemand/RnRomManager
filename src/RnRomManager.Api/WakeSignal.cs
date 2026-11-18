namespace RnRomManager.Api;

/// <summary>
/// Wakes a daily scheduler when its settings change, so it reschedules. Shared by <see cref="BackupScheduler"/> and
/// <see cref="DatRefreshScheduler"/>.
/// </summary>
public sealed class WakeSignal(TimeProvider time) : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource _wake = new();

    public void Wake()
    {
        lock (_gate)
            _wake.Cancel();
    }

    /// <summary>A fresh wake token: a change signalled before this point was already seen by the caller.</summary>
    public CancellationToken Token()
    {
        lock (_gate)
        {
            if (_wake.IsCancellationRequested)
            {
                _wake.Dispose();
                _wake = new CancellationTokenSource();
            }

            return _wake.Token;
        }
    }

    /// <summary>True when the wait elapsed; false when woken by <paramref name="wake"/> or stopping.</summary>
    public async Task<bool> WaitAsync(TimeSpan wait, CancellationToken wake, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(wake, stoppingToken);
        try
        {
            if (wait != Timeout.InfiniteTimeSpan && wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            await Task.Delay(wait, time, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _wake.Dispose();
    }
}
