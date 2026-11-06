using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RnRomManager.Core.Audit;

/// <summary>
/// AU2: <see cref="IAuditLog"/> on a bounded in-memory channel. The Api's <c>AuditWriter</c> drains <see cref="Reader"/>
/// and inserts each event, so synchronous pipeline helpers can record and a cancelled request never loses a queued row.
/// </summary>
public sealed class AuditLog(IAuditRepository repository, TimeProvider time, ILogger<AuditLog> logger, int capacity = AuditLog.Capacity)
    : IAuditLog
{
    public const int Capacity = 10_000;

    /// <summary>AU5: rows older than this are purged daily. A constant, not a setting.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    // Wait + TryWrite: a full channel makes TryWrite return false (DropWrite/DropOldest would drop silently).
    private readonly Channel<AuditEvent> _channel = Channel.CreateBounded<AuditEvent>(
        new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    private int _pending;

    public ChannelReader<AuditEvent> Reader => _channel.Reader;

    /// <summary>Events queued or being written; <c>AuditWriter.FlushAsync</c> waits for 0.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Record(AuditEvent auditEvent)
    {
        try
        {
            var stamped = auditEvent with { OccurredAt = time.GetUtcNow().UtcDateTime };
            Interlocked.Increment(ref _pending);
            if (_channel.Writer.TryWrite(stamped))
                return;
            Interlocked.Decrement(ref _pending);
            logger.LogWarning("Audit queue full; dropped {Action} for {Path}", auditEvent.Action, auditEvent.Path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audit event {Action} for {Path} couldn't be recorded", auditEvent.Action, auditEvent.Path);
        }
    }

    /// <summary>Called by the writer once an event is inserted or dropped.</summary>
    public void Completed() => Interlocked.Decrement(ref _pending);

    public Task<int> PurgeAsync(DateTime olderThan, CancellationToken cancellationToken) =>
        repository.PurgeAsync(olderThan, cancellationToken);
}
