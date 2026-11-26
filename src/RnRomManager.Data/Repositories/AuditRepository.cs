using Dapper;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;

namespace RnRomManager.Data.Repositories;

/// <summary><c>audit_events</c> (AU1, AU5).</summary>
public sealed class AuditRepository(IDbConnectionFactory connections) : IAuditRepository
{
    // GameTitle is AuditItem's last parameter, so it is the last column (Dapper maps the record by constructor).
    private const string Select = """
        SELECT a.id AS Id, a.occurred_at AS OccurredAt, a.action AS Action, a.system_id AS System, a.game_id AS GameId,
               a.username AS User, a.job_id AS JobId, a.path AS Path, a.target_path AS TargetPath, a.details AS Details,
               g.display_title AS GameTitle
        FROM audit_events a LEFT JOIN games g ON g.id = a.game_id
        """;

    public async Task<Page<AuditItem>> ListAsync(
        AuditQuery query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var clauses = new List<string>();
        if (query.Action is not null) clauses.Add("a.action = @Action");
        if (query.System is not null) clauses.Add("a.system_id = @System");
        if (query.User is not null) clauses.Add("a.username = @User");
        if (query.Scheduled) clauses.Add("a.user_id IS NULL AND a.username IS NULL");
        if (query.Q is not null) clauses.Add("(a.path LIKE @pattern OR a.target_path LIKE @pattern OR a.details LIKE @pattern)");
        if (query.From is not null) clauses.Add("a.occurred_at >= @From");
        if (query.To is not null) clauses.Add("a.occurred_at < @To");
        var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
        var args = new { query.Action, query.System, query.User, query.From, query.To,
            pattern = query.Q is null ? null : SqlLike.Contains(query.Q), pageSize, offset = (long)Page<object>.Offset(page, pageSize) };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM audit_events a {where}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<AuditItem>(new CommandDefinition($"""
            {Select} {where} ORDER BY a.occurred_at DESC, a.id DESC LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));
        return new Page<AuditItem>(rows.AsList(), page, pageSize, total);
    }

    public async Task<IReadOnlyList<AuditItem>> ListRecentAsync(IReadOnlyList<string> actions, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<AuditItem>(new CommandDefinition(
            // An array, not the IReadOnlyList: that type has a JSON handler, which would stop Dapper expanding IN.
            $"{Select} WHERE a.action IN @actions ORDER BY a.occurred_at DESC, a.id DESC LIMIT @limit",
            new { actions = actions.ToArray(), limit }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task InsertAsync(AuditEvent e, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_events (occurred_at, action, system_id, game_id, user_id, username, job_id, path, target_path, details)
            VALUES (@OccurredAt, @Action, @SystemId, @GameId, @UserId, @Username, @JobId, @Path, @TargetPath, @Details)
            """, new
        {
            e.OccurredAt,
            Action = Cut(e.Action, 32),
            SystemId = Cut(e.SystemId, 16),
            e.GameId,
            e.Actor.UserId,
            Username = Cut(e.Actor.Username, 64),
            e.Actor.JobId,
            Path = Cut(e.Path, 1024),
            TargetPath = Cut(e.TargetPath, 1024),
            Details = Cut(e.Details, 1024),
        }, cancellationToken: cancellationToken));
    }

    public async Task<int> PurgeAsync(DateTime olderThan, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM audit_events WHERE occurred_at < @olderThan", new { olderThan }, cancellationToken: cancellationToken));
    }

    // AU1: text over its column length is truncated (by UTF-16 code units; a split surrogate pair is dropped).
    private static string? Cut(string? value, int max)
    {
        if (value is null || value.Length <= max)
            return value;
        var cut = value[..max];
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }
}
