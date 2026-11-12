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

    /// <summary>
    /// G6/B21: one row per game, its ZIP plus the recorded sizes of its external ROMs and their set files (catalogued, not what is on disk,
    /// so a missing file still counts).
    /// </summary>
    private const string GameBytes = """
        SELECT g.id, g.system_id, g.display_title,
               CAST(g.zip_size_bytes + COALESCE((SELECT SUM(r.size_bytes) FROM roms r
                   WHERE r.game_id = g.id AND r.external_file_name IS NOT NULL), 0)
                   + COALESCE((SELECT SUM(f.size_bytes) FROM rom_files f WHERE f.game_id = g.id), 0) AS SIGNED) AS Bytes
        FROM games g
        """;

    public async Task<IReadOnlyList<SystemSize>> ListSystemSizesAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<SystemSizeRow>(new CommandDefinition($"""
            SELECT s.system_id AS SystemId, SUM(s.Bytes) AS Bytes, COUNT(*) AS GameCount
            FROM ({GameBytes}) s GROUP BY s.system_id ORDER BY Bytes DESC, s.system_id
            """, cancellationToken: cancellationToken));
        return rows.Select(r => new SystemSize(r.SystemId, (long)r.Bytes, (int)r.GameCount)).ToList();
    }

    public async Task<IReadOnlyList<GameSize>> ListGameSizesAsync(string systemId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<GameSizeRow>(new CommandDefinition($"""
            SELECT g.id AS Id, g.display_title AS DisplayTitle, g.Bytes,
                   (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id) AS RomCount
            FROM ({GameBytes}) g WHERE g.system_id = @systemId
            ORDER BY g.Bytes DESC, g.display_title, g.id
            """, new { systemId }, cancellationToken: cancellationToken));
        return rows.Select(r => new GameSize(r.Id, r.DisplayTitle, r.Bytes, (int)r.RomCount)).ToList();
    }

    /// <summary>
    /// RP8: groups <c>roms</c> by (system, base title) under the column's collation, so case/accent variants group together
    /// and <c>MIN(base_title)</c> is the shown variant. The games key is computed in SQL (same as
    /// <see cref="DuplicateGroups.GamesKey"/>) so dismissed groups drop out before paging.
    /// </summary>
    private const string Groups = """
        SELECT d.SystemId, d.BaseTitle, d.GamesKey, d.GameIds FROM (
            SELECT r.system_id AS SystemId, MIN(r.base_title) AS BaseTitle,
                   SHA2(GROUP_CONCAT(DISTINCT r.game_id ORDER BY r.game_id SEPARATOR ','), 256) AS GamesKey,
                   GROUP_CONCAT(DISTINCT r.game_id ORDER BY r.game_id SEPARATOR ',') AS GameIds
            FROM roms r
            WHERE @systemId IS NULL OR r.system_id = @systemId
            GROUP BY r.system_id, r.base_title
            HAVING COUNT(DISTINCT r.game_id) >= 2
        ) d
        LEFT JOIN duplicate_dismissals x
            ON x.system_id = d.SystemId AND x.base_title = d.BaseTitle AND x.games_key = d.GamesKey
        WHERE x.games_key IS NULL
        """;

    public async Task<Page<DuplicateGroup>> ListDuplicatesAsync(string? systemId, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var args = new { systemId, pageSize, offset = (long)Page<object>.Offset(page, pageSize) };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM ({Groups}) g", args, cancellationToken: cancellationToken));
        var groups = (await connection.QueryAsync<GroupRow>(new CommandDefinition(
            $"{Groups} ORDER BY d.SystemId, d.BaseTitle LIMIT @pageSize OFFSET @offset", args,
            cancellationToken: cancellationToken))).AsList();

        var ids = groups.SelectMany(g => g.GameIds.Split(',')).Distinct().ToList();
        var games = ids.Count == 0 ? [] : (await connection.QueryAsync<DuplicateGameRow>(new CommandDefinition("""
            SELECT g.id AS Id, g.display_title AS DisplayTitle,
                   (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id) AS RomCount,
                   g.zip_size_bytes AS ZipSizeBytes, g.created_at AS CreatedAt,
                   (SELECT MIN(i.id) FROM images i WHERE i.game_id = g.id AND i.is_cover = 1) AS BoxArtImageId
            FROM games g WHERE g.id IN @ids
            """, new { ids }, cancellationToken: cancellationToken)))
            .Select(r => new DuplicateGame(r.Id, r.DisplayTitle, (int)r.RomCount, r.ZipSizeBytes, r.CreatedAt, r.BoxArtImageId))
            .ToDictionary(g => g.Id);

        var items = groups.Select(g => new DuplicateGroup(g.SystemId, g.BaseTitle, g.GamesKey,
            g.GameIds.Split(',').Select(Guid.Parse).Where(games.ContainsKey).Select(id => games[id])
                // Suggested target first: most releases, then the oldest.
                .OrderByDescending(x => x.RomCount).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id.ToString("D"), StringComparer.Ordinal)
                .ToList())).ToList();
        return new Page<DuplicateGroup>(items, page, pageSize, total);
    }

    public async Task DismissDuplicateAsync(string systemId, string baseTitle, string gamesKey, int? userId, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT IGNORE INTO duplicate_dismissals (system_id, base_title, games_key, dismissed_by, dismissed_at)
            VALUES (@systemId, @baseTitle, @gamesKey, @userId, @now)
            """, new { systemId, baseTitle, gamesKey, userId, now }, cancellationToken: cancellationToken));
    }

    private sealed class DuplicateGameRow
    {
        public Guid Id { get; init; }
        public string DisplayTitle { get; init; } = "";
        public long RomCount { get; init; }
        public long ZipSizeBytes { get; init; }
        public DateTime CreatedAt { get; init; }
        public long? BoxArtImageId { get; init; }
    }

    private sealed class GroupRow
    {
        public string SystemId { get; init; } = "";
        public string BaseTitle { get; init; } = "";
        public string GamesKey { get; init; } = "";
        public string GameIds { get; init; } = "";
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
