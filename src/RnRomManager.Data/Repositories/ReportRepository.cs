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
}
