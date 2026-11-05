using RnRomManager.Core.Database;

namespace RnRomManager.Core.Integrity;

/// <summary><c>integrity_issues</c> (spec §14.1, §12.2).</summary>
public interface IIntegrityIssueRepository
{
    /// <summary>Open issues of these types on one ZIP entry.</summary>
    Task<IReadOnlyList<OpenIssue>> ListOpenAsync(string zipPath, string entryName, IReadOnlyCollection<string> types, CancellationToken cancellationToken);

    /// <summary>A page of issues, newest first; null filters aren't applied (§23 <c>/api/integrity/issues</c>).</summary>
    Task<Page<IntegrityIssue>> ListAsync(string? status, string? type, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Every <c>Open</c> issue.</summary>
    Task<IReadOnlyList<IntegrityIssue>> ListAllOpenAsync(CancellationToken cancellationToken);

    Task<long> InsertAsync(IntegrityIssue issue, CancellationToken cancellationToken);

    /// <summary>An open issue seen again: <c>last_seen_job_id</c> plus the latest severity, details and game.</summary>
    Task TouchAsync(long id, Guid jobId, string severity, string? details, Guid? gameId, CancellationToken cancellationToken);

    Task ResolveAsync(IReadOnlyCollection<long> ids, DateTime resolvedAt, CancellationToken cancellationToken);

    /// <summary>Retention (§12.2): deletes non-<c>Open</c> issues resolved (or, if never resolved, detected) before <paramref name="cutoff"/>.</summary>
    Task<int> PurgeAsync(DateTime cutoff, CancellationToken cancellationToken);
}
