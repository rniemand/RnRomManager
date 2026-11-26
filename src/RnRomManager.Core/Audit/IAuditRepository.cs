namespace RnRomManager.Core.Audit;

/// <summary>The <c>audit_events</c> table (AU1).</summary>
public interface IAuditRepository
{
    Task<Database.Page<AuditItem>> ListAsync(
        AuditQuery query,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>UI polish B21: the newest <paramref name="limit"/> rows with one of <paramref name="actions"/>, newest first (ties by id).</summary>
    Task<IReadOnlyList<AuditItem>> ListRecentAsync(IReadOnlyList<string> actions, int limit, CancellationToken cancellationToken);

    /// <summary>Inserts one event; text over a column's length is truncated.</summary>
    Task InsertAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    Task<int> PurgeAsync(DateTime olderThan, CancellationToken cancellationToken);
}
