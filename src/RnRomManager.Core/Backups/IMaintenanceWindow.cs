namespace RnRomManager.Core.Backups;

/// <summary>
/// B27.4: while open, every other request is refused and audit writes are held, so nothing lands between a live
/// restore's drop and its inserts. Implemented in the Api (<c>MaintenanceGate</c>).
/// </summary>
public interface IMaintenanceWindow
{
    /// <summary>
    /// Opens the window for the calling request and waits for the other in-flight requests to finish. Returns the handle
    /// that closes it, or null (and the window stays closed) when they didn't finish in time.
    /// </summary>
    Task<IAsyncDisposable?> TryEnterAsync(CancellationToken cancellationToken);
}
