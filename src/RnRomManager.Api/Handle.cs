namespace RnRomManager.Api;

internal sealed class Handle(MaintenanceGate gate) : IAsyncDisposable
{
    private int _disposed;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            gate.Exit();
        return ValueTask.CompletedTask;
    }
}
