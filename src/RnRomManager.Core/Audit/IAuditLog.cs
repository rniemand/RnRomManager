namespace RnRomManager.Core.Audit;

/// <summary>AU2: the best-effort file-action log.</summary>
public interface IAuditLog
{
    /// <summary>
    /// Stamps the time and queues the event; synchronous, non-blocking, and never throws. Call it only after the file
    /// action succeeded. A full queue or any internal failure drops the event with a <c>Warning</c>.
    /// </summary>
    void Record(AuditEvent auditEvent);

    /// <summary>AU5: deletes rows older than <paramref name="olderThan"/> (UTC); returns how many.</summary>
    Task<int> PurgeAsync(DateTime olderThan, CancellationToken cancellationToken);
}

/// <summary>The <c>audit_events</c> table (AU1).</summary>
public interface IAuditRepository
{
    Task<Database.Page<AuditItem>> ListAsync(AuditQuery query, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Inserts one event; text over a column's length is truncated.</summary>
    Task InsertAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    Task<int> PurgeAsync(DateTime olderThan, CancellationToken cancellationToken);
}
