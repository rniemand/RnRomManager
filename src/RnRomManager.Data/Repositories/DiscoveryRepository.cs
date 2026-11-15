using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Discovery;

namespace RnRomManager.Data.Repositories;

/// <summary>Live discovery indexes over <c>games</c> (B2, B3), normalized by <see cref="DiscoverySql"/>.</summary>
public sealed class DiscoveryRepository(IDbConnectionFactory connections) : IDiscoveryRepository
{
    private sealed record GroupRow(string Key, string Name, long GameCount, long Total);

    public async Task<Page<DiscoveryGroup>> ListGroupsAsync(
        DiscoveryKind kind, int page, int pageSize, CancellationToken cancellationToken)
    {
        var groups = $"""
            WITH {DiscoverySql.ValuesCte(kind)}
            SELECT {DiscoverySql.Key} AS `Key`, {DiscoverySql.Name} AS Name, COUNT(DISTINCT game_id) AS GameCount,
                   COUNT(*) OVER () AS Total
            FROM discovery_values
            GROUP BY norm
            ORDER BY norm
            LIMIT @pageSize OFFSET @offset
            """;
        var args = new { pageSize, offset = (long)Page<object>.Offset(page, pageSize) };

        await using var connection = connections.Create();
        var rows = (await connection.QueryAsync<GroupRow>(new CommandDefinition(groups, args, cancellationToken: cancellationToken))).AsList();
        // The window total rides on the rows; a page past the end has none, so count separately.
        var total = rows.Count > 0 || args.offset == 0
            ? rows.FirstOrDefault()?.Total ?? 0
            : await connection.ExecuteScalarAsync<long>(new CommandDefinition($"""
                WITH {DiscoverySql.ValuesCte(kind)}
                SELECT COUNT(DISTINCT norm) FROM discovery_values
                """, cancellationToken: cancellationToken));
        return new Page<DiscoveryGroup>(
            rows.Select(r => new DiscoveryGroup(r.Key, r.Name, r.GameCount)).ToList(), page, pageSize, total);
    }
}
