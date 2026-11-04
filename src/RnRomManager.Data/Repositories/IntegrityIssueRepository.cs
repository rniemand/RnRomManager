using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Integrity;

namespace RnRomManager.Data.Repositories;

public sealed class IntegrityIssueRepository(IDbConnectionFactory connections) : IIntegrityIssueRepository
{
    public async Task<IReadOnlyList<OpenIssue>> ListOpenAsync(
        string zipPath, string entryName, IReadOnlyCollection<string> types, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return (await connection.QueryAsync<OpenIssue>(new CommandDefinition("""
            SELECT id AS Id, type AS Type FROM integrity_issues
            WHERE status = 'Open' AND zip_path = @zipPath AND entry_name = @entryName AND type IN @types
            ORDER BY id
            """, new { zipPath, entryName, types }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task ResolveAsync(IReadOnlyCollection<long> ids, DateTime resolvedAt, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE integrity_issues SET status = 'Resolved', resolved_at = @resolvedAt WHERE id IN @ids",
            new { ids, resolvedAt }, cancellationToken: cancellationToken));
    }
}
