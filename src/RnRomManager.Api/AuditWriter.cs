using System.Diagnostics;
using RnRomManager.Core.Audit;

namespace RnRomManager.Api;

/// <summary>
/// AU2: drains <see cref="AuditLog"/> into <c>audit_events</c>, one insert at a time with <see cref="CancellationToken.None"/>
/// so a cancelled request or job never loses a recorded row. A failed insert is logged and dropped. On stop it drains what
/// is still queued for up to 5 s; it's registered before <c>JobShutdownService</c> so it stops after the running job.
/// </summary>
public sealed class AuditWriter(AuditLog log, IAuditRepository repository, ILogger<AuditWriter> logger) : BackgroundService
{
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    // B27.4: held by a live restore; each insert waits for it, so held events stay queued until Release.
    private readonly SemaphoreSlim _hold = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var auditEvent in log.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _hold.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Stopping while held: write this one anyway rather than lose it.
                    await WriteAsync(auditEvent).ConfigureAwait(false);
                    throw;
                }
                try
                {
                    await WriteAsync(auditEvent).ConfigureAwait(false);
                }
                finally
                {
                    _hold.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping; StopAsync drains the rest
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < DrainTimeout && !cancellationToken.IsCancellationRequested && log.Reader.TryRead(out var auditEvent))
            await WriteAsync(auditEvent).ConfigureAwait(false);
        if (log.Pending > 0)
            logger.LogWarning("Audit writer stopped with {Count} events not written", log.Pending);
    }

    /// <summary>Completes once every recorded event is written or dropped (tests call it before asserting).</summary>
    public async Task FlushAsync(TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (log.Pending > 0)
        {
            if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(10)))
                throw new TimeoutException($"{log.Pending} audit events still pending.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>B27.4: writes what is pending, then stops inserting until <see cref="Release"/>; new events stay queued.</summary>
    public async Task HoldAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        await _hold.WaitAsync().ConfigureAwait(false);
    }

    /// <summary>Resumes inserting after <see cref="HoldAsync"/>; the held events are written in order.</summary>
    public void Release() => _hold.Release();

    private async Task WriteAsync(AuditEvent auditEvent)
    {
        try
        {
            await repository.InsertAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audit event {Action} for {Path} couldn't be written", auditEvent.Action, auditEvent.Path);
        }
        finally
        {
            log.Completed();
        }
    }
}
