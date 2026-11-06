using Dapper;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;

namespace RnRomManager.Data.Repositories;

/// <summary><c>audit_events</c> (AU1, AU5).</summary>
public sealed class AuditRepository(IDbConnectionFactory connections) : IAuditRepository
{
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
