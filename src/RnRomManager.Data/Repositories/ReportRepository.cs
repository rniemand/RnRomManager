using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Reports;

namespace RnRomManager.Data.Repositories;

/// <summary>Reporting queries over <c>games</c>, <c>roms</c> and <c>images</c> (RP2–RP8).</summary>
public sealed class ReportRepository(IDbConnectionFactory connections) : IReportRepository
{
    private const string NoCover = "NOT EXISTS (SELECT 1 FROM images i WHERE i.game_id = g.id AND i.is_cover = 1)";

    public async Task<Page<MissingImageItem>> ListMissingImagesAsync(string? systemId, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var where = $"WHERE ({NoCover} OR g.has_default_boxart = 1)" + (systemId is null ? "" : " AND g.system_id = @systemId");
        var args = new { systemId, pageSize, offset = (long)Page<object>.Offset(page, pageSize) };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM games g {where}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<MissingImageItem>(new CommandDefinition($"""
            SELECT g.id AS Id, g.display_title AS DisplayTitle, g.system_id AS SystemId,
                   CASE WHEN {NoCover} THEN '{MissingImageReasons.NoCover}' ELSE '{MissingImageReasons.Placeholder}' END AS Reason
            FROM games g {where}
            ORDER BY g.system_id, g.display_title, g.id
            LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));
        return new Page<MissingImageItem>(rows.AsList(), page, pageSize, total);
    }

    public async Task<IReadOnlyList<SystemSize>> ListSystemSizesAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<SystemSizeRow>(new CommandDefinition("""
            SELECT system_id AS SystemId, SUM(zip_size_bytes) AS Bytes, COUNT(*) AS GameCount
            FROM games GROUP BY system_id ORDER BY Bytes DESC, system_id
            """, cancellationToken: cancellationToken));
        return rows.Select(r => new SystemSize(r.SystemId, (long)r.Bytes, (int)r.GameCount)).ToList();
    }

    public async Task<IReadOnlyList<GameSize>> ListGameSizesAsync(string systemId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<GameSizeRow>(new CommandDefinition("""
            SELECT g.id AS Id, g.display_title AS DisplayTitle, g.zip_size_bytes AS Bytes,
                   (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id) AS RomCount
            FROM games g WHERE g.system_id = @systemId
            ORDER BY g.zip_size_bytes DESC, g.display_title, g.id
            """, new { systemId }, cancellationToken: cancellationToken));
        return rows.Select(r => new GameSize(r.Id, r.DisplayTitle, r.Bytes, (int)r.RomCount)).ToList();
    }

    // SUM() is DECIMAL and COUNT() BIGINT on MariaDB: map loosely, then narrow.
    private sealed class SystemSizeRow
    {
        public string SystemId { get; init; } = "";
        public decimal Bytes { get; init; }
        public long GameCount { get; init; }
    }

    private sealed class GameSizeRow
    {
        public Guid Id { get; init; }
        public string DisplayTitle { get; init; } = "";
        public long Bytes { get; init; }
        public long RomCount { get; init; }
    }
}
