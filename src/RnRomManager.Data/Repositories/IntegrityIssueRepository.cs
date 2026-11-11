using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Integrity;

namespace RnRomManager.Data.Repositories;

public sealed class IntegrityIssueRepository(IDbConnectionFactory connections) : IIntegrityIssueRepository
{
    private const string Select = """
        SELECT id AS Id, type AS Type, severity AS Severity, system_id AS SystemId, zip_path AS ZipPath, game_id AS GameId,
               entry_name AS EntryName, details AS Details, status AS Status, first_seen_job_id AS FirstSeenJobId,
               last_seen_job_id AS LastSeenJobId, detected_at AS DetectedAt, resolved_at AS ResolvedAt
        FROM integrity_issues
        """;

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

    public async Task<IReadOnlyList<ExternalOpenIssue>> ListOpenExternalAsync(
        string zipPath, string externalFileName, IReadOnlyCollection<string> types, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return (await connection.QueryAsync<ExternalOpenIssue>(new CommandDefinition("""
            SELECT id AS Id, type AS Type, zip_path AS ZipPath, entry_name AS EntryName, game_id AS GameId FROM integrity_issues
            WHERE status = 'Open' AND zip_path = @zipPath AND entry_name = @externalFileName AND type IN @types
            ORDER BY id
            """, new { zipPath, externalFileName, types }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<Page<IntegrityIssue>> ListAsync(string? status, string? type, int page, int pageSize, CancellationToken cancellationToken)
    {
        const string where = "WHERE (@status IS NULL OR status = @status) AND (@type IS NULL OR type = @type)";
        var args = new { status, type, pageSize, offset = (long)(page - 1) * pageSize };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM integrity_issues {where}", args, cancellationToken: cancellationToken));
        var items = await connection.QueryAsync<IntegrityIssue>(new CommandDefinition(
            $"{Select} {where} ORDER BY detected_at DESC, id DESC LIMIT @pageSize OFFSET @offset", args, cancellationToken: cancellationToken));
        return new Page<IntegrityIssue>(items.AsList(), page, pageSize, total);
    }

    public async Task<IReadOnlyList<IntegrityIssue>> ListAllOpenAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return (await connection.QueryAsync<IntegrityIssue>(new CommandDefinition(
            $"{Select} WHERE status = 'Open' ORDER BY id", cancellationToken: cancellationToken))).AsList();
    }

    public async Task<long> InsertAsync(IntegrityIssue issue, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO integrity_issues (type, severity, system_id, zip_path, game_id, entry_name, details, status,
                                          first_seen_job_id, last_seen_job_id, detected_at, resolved_at)
            VALUES (@Type, @Severity, @SystemId, @ZipPath, @GameId, @EntryName, @Details, @Status,
                    @FirstSeenJobId, @LastSeenJobId, @DetectedAt, @ResolvedAt);
            SELECT LAST_INSERT_ID();
            """, issue, cancellationToken: cancellationToken));
    }

    public async Task TouchAsync(long id, Guid jobId, string severity, string? details, Guid? gameId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE integrity_issues SET last_seen_job_id = @jobId, severity = @severity, details = @details,
                game_id = COALESCE(@gameId, game_id)
            WHERE id = @id AND status = 'Open'
            """, new { id, jobId, severity, details, gameId }, cancellationToken: cancellationToken));
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

    public async Task<int> PurgeAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM integrity_issues WHERE status <> 'Open' AND COALESCE(resolved_at, detected_at) < @cutoff",
            new { cutoff }, cancellationToken: cancellationToken));
    }
}
