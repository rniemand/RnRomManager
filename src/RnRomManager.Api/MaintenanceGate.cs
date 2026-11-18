using RnRomManager.Core.Backups;

namespace RnRomManager.Api;

/// <summary>B27.4: endpoint metadata for long-lived streams that don't hold a live restore back (they read files, not tables).</summary>
public sealed class MaintenanceExempt
{
    public static readonly MaintenanceExempt Instance = new();
}

/// <summary>
/// B27.4: the maintenance window behind a live restore. Its middleware (right after the security headers, before auth,
/// since cookie validation reads <c>users</c>) answers every <c>/api</c> and <c>/health/ready</c> request but the owning
/// one <c>503 Maintenance</c> while open, and counts the in-flight <c>/api</c> requests (not <see cref="MaintenanceExempt"/>
/// ones) so <see cref="TryEnterAsync"/> can wait for them. Opening also holds the audit writer until it closes.
/// </summary>
public sealed class MaintenanceGate(IHttpContextAccessor accessor, AuditWriter audit, TimeProvider time, ILogger<MaintenanceGate> logger)
    : IMaintenanceWindow
{
    /// <summary>How long in-flight requests get to finish before the restore is refused (<c>409 Busy</c>).</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private const string CountedKey = "rnrm.maintenance.counted";

    private readonly Lock _lock = new();
    private int _inFlight;
    private volatile HttpContext? _owner;
    private volatile bool _open;
    private TaskCompletionSource _changed = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOpen => _open;

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var path = ctx.Request.Path;
        var api = path.StartsWithSegments("/api");
        if (!api && !path.StartsWithSegments("/health/ready"))
        {
            await next(ctx);
            return;
        }

        if (_open && !ReferenceEquals(ctx, _owner))
        {
            await RefuseAsync(ctx);
            return;
        }

        if (!api || ctx.GetEndpoint()?.Metadata.GetMetadata<MaintenanceExempt>() is not null)
        {
            await next(ctx);
            return;
        }

        // Checked again under the lock: a window that opened after the check above has already drained without
        // counting this request, so it must not run against the tables being replaced.
        bool refused;
        lock (_lock)
        {
            refused = _open && !ReferenceEquals(ctx, _owner);
            if (!refused)
                _inFlight++;
        }
        if (refused)
        {
            await RefuseAsync(ctx);
            return;
        }
        ctx.Items[CountedKey] = true;
        try
        {
            await next(ctx);
        }
        finally
        {
            TaskCompletionSource changed;
            lock (_lock)
            {
                _inFlight--;
                changed = _changed;
                _changed = NewSignal();
            }
            changed.TrySetResult();
        }
    }

    private static Task RefuseAsync(HttpContext ctx)
    {
        ctx.Response.Headers.RetryAfter = "5";
        return Problems.Create(ctx, StatusCodes.Status503ServiceUnavailable, "Maintenance",
            "A backup is being restored. Try again in a moment.").ExecuteAsync(ctx);
    }

    public async Task<IAsyncDisposable?> TryEnterAsync(CancellationToken cancellationToken)
    {
        var owner = accessor.HttpContext;
        var self = owner?.Items.ContainsKey(CountedKey) == true ? 1 : 0;
        lock (_lock)
        {
            if (_open)
                return null;
            _owner = owner;
            _open = true;
        }

        using var timeout = new CancellationTokenSource(DrainTimeout, time);
        try
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    if (_inFlight <= self)
                        break;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(timeout.Token).WaitAsync(cancellationToken);
            }
            await audit.HoldAsync();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Maintenance window refused: requests still running after {Timeout}", DrainTimeout);
            Close();
            return null;
        }
        catch
        {
            Close();
            throw;
        }

        logger.LogWarning("Maintenance window open: other requests get 503 until it closes");
        return new Handle(this);
    }

    private void Exit()
    {
        audit.Release();
        Close();
        logger.LogWarning("Maintenance window closed");
    }

    private void Close()
    {
        lock (_lock)
        {
            _open = false;
            _owner = null;
        }
    }

    private sealed class Handle(MaintenanceGate gate) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                gate.Exit();
            return ValueTask.CompletedTask;
        }
    }
}
