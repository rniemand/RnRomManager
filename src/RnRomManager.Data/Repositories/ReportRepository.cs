using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Reports;

namespace RnRomManager.Data.Repositories;

/// <summary>Reporting queries over <c>games</c>, <c>roms</c> and <c>images</c> (RP2–RP8).</summary>
public sealed class ReportRepository(IDbConnectionFactory connections) : IReportRepository
{
    // MissingMetadataItem's developers/publishers are JSON columns; this class may be the only repository a test builds.
    static ReportRepository() => JsonStringListHandler.Register();

    private const string NoCover = "NOT EXISTS (SELECT 1 FROM images i WHERE i.game_id = g.id AND i.is_cover = 1)";

    /// <summary>RP4's predicate and order, shared by the report and <see cref="NextMissingImageAsync"/>.</summary>
    private const string MissingImage = $"({NoCover} OR g.has_default_boxart = 1)";

    private const string MissingImageOrder = "g.system_id, g.display_title, g.id";

    public async Task<Page<MissingImageItem>> ListMissingImagesAsync(string? systemId, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var where = $"WHERE {MissingImage}" + (systemId is null ? "" : " AND g.system_id = @systemId");
        var args = new { systemId, pageSize, offset = (long)Page<object>.Offset(page, pageSize) };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM games g {where}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<MissingImageItem>(new CommandDefinition($"""
            SELECT g.id AS Id, g.display_title AS DisplayTitle, g.system_id AS SystemId,
                   CASE WHEN {NoCover} THEN '{MissingImageReasons.NoCover}' ELSE '{MissingImageReasons.Placeholder}' END AS Reason
            FROM games g {where}
            ORDER BY {MissingImageOrder}
            LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));
        return new Page<MissingImageItem>(rows.AsList(), page, pageSize, total);
    }

    public async Task<Page<ImageResolutionItem>> ListImageResolutionsAsync(string? systemId, ImageType? type, int? maxWidth, int? maxHeight,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var limits = (maxWidth, maxHeight) switch
        {
            (null, null) => "",
            (not null, null) => " AND i.width <= @maxWidth",
            (null, not null) => " AND i.height <= @maxHeight",
            _ => " AND (i.width <= @maxWidth OR i.height <= @maxHeight)",
        };
        var where = "WHERE i.is_default = 0" + (systemId is null ? "" : " AND g.system_id = @systemId")
            + (type is null ? "" : " AND i.type = @type") + limits;
        // R3: images.type is a VARCHAR, so the enum goes in by name (Dapper would send its number).
        var args = new { systemId, type = type?.ToString(), maxWidth, maxHeight, pageSize, offset = (long)Page<object>.Offset(page, pageSize) };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM images i JOIN games g ON g.id = i.game_id {where}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<ImageResolutionItem>(new CommandDefinition($"""
            SELECT i.id AS ImageId, g.id AS GameId, g.display_title AS DisplayTitle, g.system_id AS SystemId, i.type AS Type,
                   i.width AS Width, i.height AS Height, i.size_bytes AS SizeBytes
            FROM images i JOIN games g ON g.id = i.game_id {where}
            ORDER BY CAST(i.width AS SIGNED) * i.height, g.system_id, g.display_title, i.id
            LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));
        return new Page<ImageResolutionItem>(rows.AsList(), page, pageSize, total);
    }

    public async Task<MissingImageSuccessor> NextMissingImageAsync(Guid? afterGameId, CancellationToken cancellationToken)
    {
        // The anchor is joined from games (not the report), so a game that was just fixed keeps its place. Both sides of the row
        // comparison are columns, so display_title compares under its own collation, exactly as the report sorts it. Rows after the
        // anchor come first, then the wrap from the top; the anchor itself is never picked.
        await using var connection = connections.Create();
        var next = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition($"""
            SELECT g.id FROM games g
            LEFT JOIN games a ON a.id = @afterGameId
            WHERE {MissingImage} AND (a.id IS NULL OR g.id <> a.id)
            ORDER BY CASE WHEN a.id IS NULL OR (g.system_id, g.display_title, g.id) > (a.system_id, a.display_title, a.id)
                          THEN 0 ELSE 1 END,
                     {MissingImageOrder}
            LIMIT 1
            """, new { afterGameId }, cancellationToken: cancellationToken));
        if (next is not null)
            return new MissingImageSuccessor(next, false);
        if (afterGameId is null)
            return new MissingImageSuccessor(null, false);
        var currentMissing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM games g WHERE g.id = @afterGameId AND {MissingImage}", new { afterGameId },
            cancellationToken: cancellationToken));
        return new MissingImageSuccessor(null, currentMissing > 0);
    }

    /// <summary>M1: <c>developers</c> and <c>publishers</c> are <c>JSON NOT NULL</c>, unknown written as <c>[]</c>.</summary>
    private const string MissingYear = "g.release_year IS NULL";
    private const string MissingDeveloper = "JSON_LENGTH(g.developers) = 0";
    private const string MissingPublisher = "JSON_LENGTH(g.publishers) = 0";
    private const string MissingMetadata = $"({MissingYear} OR {MissingDeveloper} OR {MissingPublisher})";
    private const string MissingMetadataOrder = "g.display_title, g.id";

    public async Task<Page<MissingMetadataItem>> ListMissingMetadataAsync(string? systemId, MissingMetadataField missing, string? q,
        MissingMetadataSort sort, bool descending, int page, int pageSize, CancellationToken cancellationToken)
    {
        // M2: the predicate, key and direction come only from the enums and bool, never from input text.
        var where = "WHERE " + missing switch
        {
            MissingMetadataField.Any => MissingMetadata,
            MissingMetadataField.Year => MissingYear,
            MissingMetadataField.Developer => MissingDeveloper,
            MissingMetadataField.Publisher => MissingPublisher,
            _ => throw new ArgumentOutOfRangeException(nameof(missing)),
        } + (systemId is null ? "" : " AND g.system_id = @systemId") + (q is null ? "" : " AND g.display_title LIKE @q");
        var dir = descending ? "DESC" : "";
        var order = sort switch
        {
            MissingMetadataSort.Name => $"g.display_title {dir}, g.id",
            MissingMetadataSort.System => $"g.system_id {dir}, g.display_title, g.id",
            MissingMetadataSort.Year => $"g.release_year IS NULL, g.release_year {dir}, g.display_title, g.id",
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };
        var args = new { systemId, q = q is null ? null : SqlLike.Contains(q), pageSize, offset = (long)Page<object>.Offset(page, pageSize) };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM games g {where}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<MissingMetadataRow>(new CommandDefinition($"""
            SELECT g.id AS Id, g.display_title AS DisplayTitle, g.system_id AS SystemId, g.release_year AS ReleaseYear,
                   g.developers AS Developers, g.publishers AS Publishers
            FROM games g {where}
            ORDER BY {order}
            LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));
        var items = rows.Select(r => new MissingMetadataItem(r.Id, r.DisplayTitle, r.SystemId, r.ReleaseYear, r.Developers, r.Publishers)).ToList();
        return new Page<MissingMetadataItem>(items, page, pageSize, total);
    }

    public async Task<Page<MissingIgdbItem>> ListMissingIgdbIdsAsync(string? systemId, MissingIgdbStatus status, string? q,
        MissingMetadataSort sort, bool descending, int page, int pageSize, CancellationToken cancellationToken)
    {
        // As ListMissingMetadataAsync: the predicate, key and direction come only from the enums and bool.
        var where = "WHERE " + status switch
        {
            MissingIgdbStatus.Missing => "g.igdb_id IS NULL",
            MissingIgdbStatus.NotOnIgdb => "g.igdb_id = 0",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        } + (systemId is null ? "" : " AND g.system_id = @systemId") + (q is null ? "" : " AND g.display_title LIKE @q");
        var dir = descending ? "DESC" : "";
        var order = sort switch
        {
            MissingMetadataSort.Name => $"g.display_title {dir}, g.id",
            MissingMetadataSort.System => $"g.system_id {dir}, g.display_title, g.id",
            MissingMetadataSort.Year => $"g.release_year IS NULL, g.release_year {dir}, g.display_title, g.id",
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };
        var args = new { systemId, q = q is null ? null : SqlLike.Contains(q), pageSize, offset = (long)Page<object>.Offset(page, pageSize) };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM games g {where}", args, cancellationToken: cancellationToken));
        // MissingMetadataRow: release_year is a SMALLINT, which Dapper only maps onto a settable int? (not a record parameter).
        var rows = await connection.QueryAsync<MissingMetadataRow>(new CommandDefinition($"""
            SELECT g.id AS Id, g.display_title AS DisplayTitle, g.system_id AS SystemId, g.release_year AS ReleaseYear
            FROM games g {where}
            ORDER BY {order}
            LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));
        var items = rows.Select(r => new MissingIgdbItem(r.Id, r.DisplayTitle, r.SystemId, r.ReleaseYear)).ToList();
        return new Page<MissingIgdbItem>(items, page, pageSize, total);
    }

    public async Task<MissingImageSuccessor> NextMissingMetadataAsync(Guid? afterGameId, CancellationToken cancellationToken)
    {
        // As NextMissingImageAsync: the anchor keeps its place even once fixed; rows after it first, then the wrap.
        await using var connection = connections.Create();
        var next = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition($"""
            SELECT g.id FROM games g
            LEFT JOIN games a ON a.id = @afterGameId
            WHERE {MissingMetadata} AND (a.id IS NULL OR g.id <> a.id)
            ORDER BY CASE WHEN a.id IS NULL OR (g.display_title, g.id) > (a.display_title, a.id) THEN 0 ELSE 1 END,
                     {MissingMetadataOrder}
            LIMIT 1
            """, new { afterGameId }, cancellationToken: cancellationToken));
        if (next is not null)
            return new MissingImageSuccessor(next, false);
        if (afterGameId is null)
            return new MissingImageSuccessor(null, false);
        var currentMissing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM games g WHERE g.id = @afterGameId AND {MissingMetadata}", new { afterGameId },
            cancellationToken: cancellationToken));
        return new MissingImageSuccessor(null, currentMissing > 0);
    }

    /// <summary>
    /// G6/B21: one row per game, its ZIP plus the recorded sizes of its external ROMs and their set files (catalogued, not what is on disk,
    /// so a missing file still counts).
    /// </summary>
    private const string GameBytes = """
        SELECT g.id, g.system_id, g.display_title,
               CAST(g.zip_size_bytes + COALESCE((SELECT SUM(r.size_bytes) FROM roms r
                   WHERE r.game_id = g.id AND r.external_file_name IS NOT NULL), 0)
                   + COALESCE((SELECT SUM(f.size_bytes) FROM rom_files f WHERE f.game_id = g.id), 0) AS SIGNED) AS Bytes,
               (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id AND JSON_CONTAINS(r.flags, '"Update"')) AS UpdateCount
        FROM games g
        """;

    public async Task<IReadOnlyList<SystemSize>> ListSystemSizesAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<SystemSizeRow>(new CommandDefinition($"""
            SELECT s.system_id AS SystemId, SUM(s.Bytes) AS Bytes, COUNT(*) AS GameCount, SUM(s.UpdateCount) AS UpdateCount
            FROM ({GameBytes}) s GROUP BY s.system_id ORDER BY Bytes DESC, s.system_id
            """, cancellationToken: cancellationToken));
        return rows.Select(r => new SystemSize(r.SystemId, (long)r.Bytes, (int)r.GameCount, (int)r.UpdateCount)).ToList();
    }

    /// <summary>B15: <see cref="GameBytes"/> plus each game's release count (rows flagged neither <c>Update</c> nor <c>DLC</c>, DLC B14).</summary>
    private const string GameReleases = $"""
        SELECT g.*, (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id AND NOT JSON_CONTAINS(r.flags, '"Update"') AND NOT JSON_CONTAINS(r.flags, '"DLC"')) AS ReleaseCount
        FROM ({GameBytes}) g
        """;

    public async Task<Page<MultipleReleaseItem>> ListMultipleReleasesAsync(string? systemId, int min, MultipleReleaseSort sort, bool descending, int page,
        int pageSize, CancellationToken cancellationToken)
    {
        // B17: the key and direction come only from the enum and bool, never from input text.
        var dir = descending ? "DESC" : "";
        var order = sort switch
        {
            MultipleReleaseSort.Name => $"m.display_title {dir}, m.system_id, m.id",
            MultipleReleaseSort.System => $"m.system_id {dir}, m.display_title, m.id",
            MultipleReleaseSort.Releases => $"m.ReleaseCount {dir}, m.display_title, m.id",
            MultipleReleaseSort.Updates => $"m.UpdateCount {dir}, m.display_title, m.id",
            MultipleReleaseSort.Size => $"m.Bytes {dir}, m.display_title, m.id",
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };
        var args = new { min, systemId, pageSize, offset = (long)Page<object>.Offset(page, pageSize) };
        const string where = "m.ReleaseCount >= @min AND (@systemId IS NULL OR m.system_id = @systemId)";
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM ({GameReleases}) m WHERE {where}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<MultipleReleaseRow>(new CommandDefinition($"""
            SELECT m.id AS Id, m.display_title AS DisplayTitle, m.system_id AS SystemId, m.ReleaseCount, m.UpdateCount, m.Bytes
            FROM ({GameReleases}) m WHERE {where}
            ORDER BY {order}
            LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));
        var items = rows.Select(r => new MultipleReleaseItem(r.Id, r.DisplayTitle, r.SystemId, (int)r.ReleaseCount, (int)r.UpdateCount,
            r.Bytes)).ToList();
        return new Page<MultipleReleaseItem>(items, page, pageSize, total);
    }

    public async Task<Page<HackedGameItem>> ListHackedGamesAsync(string? systemId, HackedGameSort sort, bool descending, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        // The key and direction come only from the enum and bool, never from input text.
        var dir = descending ? "DESC" : "";
        var order = sort switch
        {
            HackedGameSort.Name => $"m.display_title {dir}, m.id",
            HackedGameSort.System => $"m.system_id {dir}, m.display_title, m.id",
            HackedGameSort.Hacks => $"m.HackCount {dir}, m.display_title, m.id",
            HackedGameSort.Releases => $"m.ReleaseCount {dir}, m.display_title, m.id",
            HackedGameSort.Size => $"m.Bytes {dir}, m.display_title, m.id",
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };
        var args = new { systemId, pageSize, offset = (long)Page<object>.Offset(page, pageSize) };
        var hacked = $"""
            SELECT h.* FROM (
                SELECT g.*, (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id AND JSON_CONTAINS(r.flags, '"Hack"')) AS HackCount
                FROM ({GameReleases}) g WHERE @systemId IS NULL OR g.system_id = @systemId
            ) h WHERE h.HackCount > 0
            """;
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM ({hacked}) m", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<HackedGameRow>(new CommandDefinition($"""
            SELECT m.id AS Id, m.display_title AS DisplayTitle, m.system_id AS SystemId, m.HackCount, m.ReleaseCount, m.Bytes
            FROM ({hacked}) m
            ORDER BY {order}
            LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));
        var items = rows.Select(r => new HackedGameItem(r.Id, r.DisplayTitle, r.SystemId, (int)r.HackCount, (int)r.ReleaseCount, r.Bytes)).ToList();
        return new Page<HackedGameItem>(items, page, pageSize, total);
    }

    public async Task<IReadOnlyList<NewGameItem>> ListNewlyAddedGamesAsync(string? systemId, int perSystem, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<NewGameRow>(new CommandDefinition("""
            SELECT n.id AS Id, n.display_title AS DisplayTitle, n.system_id AS SystemId, n.created_at AS AddedAt,
                   (SELECT COUNT(*) FROM roms r WHERE r.game_id = n.id AND NOT JSON_CONTAINS(r.flags, '"Update"') AND NOT JSON_CONTAINS(r.flags, '"DLC"')) AS ReleaseCount
            FROM (
                SELECT g.id, g.display_title, g.system_id, g.created_at,
                       ROW_NUMBER() OVER (PARTITION BY g.system_id ORDER BY g.created_at DESC, g.id DESC) AS rn
                FROM games g WHERE @systemId IS NULL OR g.system_id = @systemId
            ) n
            WHERE n.rn <= @perSystem
            ORDER BY n.system_id, n.rn
            """, new { systemId, perSystem }, cancellationToken: cancellationToken));
        return rows.Select(r => new NewGameItem(r.Id, r.DisplayTitle, r.SystemId, DateTime.SpecifyKind(r.AddedAt, DateTimeKind.Utc), (int)r.ReleaseCount))
            .ToList();
    }

    public async Task<int> MaxReleaseCountAsync(string? systemId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COALESCE(MAX(m.ReleaseCount), 0) FROM ({GameReleases}) m WHERE @systemId IS NULL OR m.system_id = @systemId",
            new { systemId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<RecentRom>> ListRecentlyAddedRomsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        // SUM is DECIMAL in MariaDB; CAST so Dapper maps it to the record's long.
        var rows = await connection.QueryAsync<RecentRom>(new CommandDefinition("""
            SELECT r.id AS RomId, r.game_id AS GameId, g.display_title AS GameTitle, r.file_name AS FileName, r.system_id AS System,
                   CAST(r.size_bytes + COALESCE((SELECT SUM(f.size_bytes) FROM rom_files f WHERE f.rom_id = r.id), 0) AS SIGNED) AS SizeBytes,
                   r.date_added AS AddedAt
            FROM roms r JOIN games g ON g.id = r.game_id
            ORDER BY r.date_added DESC, r.id DESC LIMIT @limit
            """, new { limit }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<GameSize>> ListGameSizesAsync(string systemId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<GameSizeRow>(new CommandDefinition($"""
            SELECT g.id AS Id, g.display_title AS DisplayTitle, g.Bytes, g.UpdateCount,
                   (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id) AS RomCount
            FROM ({GameBytes}) g WHERE g.system_id = @systemId
            ORDER BY g.Bytes DESC, g.display_title, g.id
            """, new { systemId }, cancellationToken: cancellationToken));
        return rows.Select(r => new GameSize(r.Id, r.DisplayTitle, r.Bytes, (int)r.RomCount, (int)r.UpdateCount)).ToList();
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

    private sealed class MissingMetadataRow
    {
        public Guid Id { get; init; }
        public string DisplayTitle { get; init; } = "";
        public string SystemId { get; init; } = "";
        public int? ReleaseYear { get; init; }
        public IReadOnlyList<string> Developers { get; init; } = [];
        public IReadOnlyList<string> Publishers { get; init; } = [];
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
        public decimal UpdateCount { get; init; }
    }

    private sealed class MultipleReleaseRow
    {
        public Guid Id { get; init; }
        public string DisplayTitle { get; init; } = "";
        public string SystemId { get; init; } = "";
        public long ReleaseCount { get; init; }
        public long UpdateCount { get; init; }
        public long Bytes { get; init; }
    }

    private sealed class HackedGameRow
    {
        public Guid Id { get; init; }
        public string DisplayTitle { get; init; } = "";
        public string SystemId { get; init; } = "";
        public long HackCount { get; init; }
        public long ReleaseCount { get; init; }
        public long Bytes { get; init; }
    }

    private sealed class NewGameRow
    {
        public Guid Id { get; init; }
        public string DisplayTitle { get; init; } = "";
        public string SystemId { get; init; } = "";
        public DateTime AddedAt { get; init; }
        public long ReleaseCount { get; init; }
    }

    private sealed class GameSizeRow
    {
        public Guid Id { get; init; }
        public string DisplayTitle { get; init; } = "";
        public long Bytes { get; init; }
        public long RomCount { get; init; }
        public long UpdateCount { get; init; }
    }
}
