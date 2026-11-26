using System.Data;
using System.Text.Json;
using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;

namespace RnRomManager.Data.Repositories;

public sealed class GameRepository : IGameRepository
{
    internal const string Select = """
        SELECT id AS Id, system_id AS SystemId, title AS Title, display_title AS DisplayTitle, zip_path AS ZipPath,
               bucket AS Bucket, release_year AS ReleaseYear, developers AS Developers, publishers AS Publishers,
               genres AS Genres, description AS Description, has_default_boxart AS HasDefaultBoxArt,
               rating AS Rating, favourite AS Favourite, igdb_id AS IgdbId, save_count AS SaveCount,
               zip_size_bytes AS ZipSizeBytes, zip_mtime_utc AS ZipMtimeUtc, info_schema_version AS InfoSchemaVersion,
               created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM games
        """;

    private readonly IDbConnectionFactory _connections;

    public GameRepository(IDbConnectionFactory connections)
    {
        JsonStringListHandler.Register();
        _connections = connections;
    }

    public async Task<GameRow?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return await connection.QuerySingleOrDefaultAsync<GameRow>(
            new CommandDefinition($"{Select} WHERE id = @id", new { id }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetUserSaveCountsAsync(string username, IReadOnlyCollection<Guid> gameIds,
        CancellationToken cancellationToken)
    {
        if (gameIds.Count == 0)
            return new Dictionary<Guid, int>();
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<(Guid GameId, int SaveCount)>(new CommandDefinition(
                "SELECT game_id, save_count FROM game_user_save_counts WHERE username = @username AND game_id IN @gameIds",
                new { username, gameIds }, cancellationToken: cancellationToken)))
            .ToDictionary(r => r.GameId, r => r.SaveCount);
    }

    public async Task ClearIgdbSyncAsync(Guid gameId, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE game_actions SET igdb_rating = NULL, igdb_rating_count = NULL, last_igdb_sync_at = NULL WHERE game_id = @gameId",
            new { gameId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<GameRow>> ListIgdbLinkedAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<GameRow>(new CommandDefinition(
            $"{Select} g WHERE g.igdb_id > 0 ORDER BY g.title, g.id", cancellationToken: cancellationToken))).AsList();
    }

    public async Task SaveIgdbSyncAsync(
        Guid gameId,
        double? rating,
        int? count,
        DateTime syncedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO game_actions (game_id, igdb_rating, igdb_rating_count, last_igdb_sync_at) VALUES (@gameId, @rating, @count, @syncedAt)
            ON DUPLICATE KEY UPDATE igdb_rating = VALUES(igdb_rating), igdb_rating_count = VALUES(igdb_rating_count),
                                    last_igdb_sync_at = VALUES(last_igdb_sync_at)
            """, new { gameId, rating, count, syncedAt }, cancellationToken: cancellationToken));
    }

    public async Task<GameActionsRow?> GetActionsAsync(Guid gameId, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return await connection.QuerySingleOrDefaultAsync<GameActionsRow>(new CommandDefinition("""
            SELECT cover_image_pulled_at AS CoverImagePulledAt, last_igdb_sync_at AS LastIgdbSyncAt, igdb_rating AS IgdbRating,
                   igdb_rating_count AS IgdbRatingCount
            FROM game_actions WHERE game_id = @gameId
            """, new { gameId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, DateTime>> ListCoverPulledAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<(Guid GameId, DateTime PulledAt)>(new CommandDefinition(
                "SELECT game_id, cover_image_pulled_at FROM game_actions WHERE cover_image_pulled_at IS NOT NULL", cancellationToken: cancellationToken)))
            .ToDictionary(r => r.GameId, r => DateTime.SpecifyKind(r.PulledAt, DateTimeKind.Utc));
    }

    public async Task<GameActionsSummary> GetActionsSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        var row = await connection.QuerySingleAsync<(long Linked, long Pending, DateTime? LastSync)>(new CommandDefinition("""
            SELECT COUNT(*),
                   CAST(COALESCE(SUM(a.cover_image_pulled_at IS NULL), 0) AS SIGNED),
                   (SELECT MAX(last_igdb_sync_at) FROM game_actions)
            FROM games g LEFT JOIN game_actions a ON a.game_id = g.id
            WHERE g.igdb_id > 0
            """, cancellationToken: cancellationToken));
        return new GameActionsSummary((int)row.Linked, (int)row.Pending,
            row.LastSync is { } last ? DateTime.SpecifyKind(last, DateTimeKind.Utc) : null);
    }

    public async Task<int> DeleteOrphanActionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM game_actions WHERE NOT EXISTS (SELECT 1 FROM games g WHERE g.id = game_actions.game_id)", cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<GameRow>> ListAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<GameRow>(new CommandDefinition(Select, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<GameSyncState>> ListSyncStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        var rows = await connection.QueryAsync<(Guid Id, string ZipPath, long Size, DateTime Mtime, string SystemId)>(new CommandDefinition(
            "SELECT id, zip_path, zip_size_bytes, zip_mtime_utc, system_id FROM games", cancellationToken: cancellationToken));
        return rows.Select(r => new GameSyncState(r.Id, r.ZipPath, r.Size, r.Mtime, r.SystemId)).ToList();
    }

    /// <summary>Rows per INSERT into the staging table.</summary>
    internal const int ZipPathBatchSize = 500;

    /// <summary><c>games.zip_path</c> is VARCHAR(512): a longer path can't equal a stored one and can't be staged.</summary>
    private const int MaxZipPathLength = 512;

    public async Task<IReadOnlyList<ZipPathEquivalenceGroup>> ListZipPathEquivalenceAsync(IReadOnlyList<string> zipPaths, CancellationToken cancellationToken)
    {
        if (zipPaths.Count == 0)
            return [];

        // The temporary table is local to the session, so everything runs on this one open connection.
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var groupOf = new Dictionary<int, int>(); // input ordinal -> the first ordinal of its group
        var owners = new List<(int Grp, Guid Id, string ZipPath)>();
        try
        {
            // Copying the column from games gives the staging column its exact type and collation. The key is the
            // input's ordinal, not the path, so spellings the collation treats as equal are all kept.
            await connection.ExecuteAsync(new CommandDefinition("""
                DROP TEMPORARY TABLE IF EXISTS tmp_zip_path_inputs;
                CREATE TEMPORARY TABLE tmp_zip_path_inputs (ord INT NOT NULL PRIMARY KEY, KEY ix_zip_path (zip_path))
                    AS SELECT 0 AS ord, zip_path FROM games WHERE 1 = 0;
                """, cancellationToken: cancellationToken)).ConfigureAwait(false);

            var staged = Enumerable.Range(0, zipPaths.Count).Where(i => zipPaths[i].EnumerateRunes().Count() <= MaxZipPathLength).ToList();
            foreach (var batch in staged.Chunk(ZipPathBatchSize))
            {
                var parameters = new DynamicParameters();
                var values = new List<string>(batch.Length);
                foreach (var ordinal in batch)
                {
                    parameters.Add($"o{ordinal}", ordinal);
                    parameters.Add($"p{ordinal}", zipPaths[ordinal]);
                    values.Add($"(@o{ordinal}, @p{ordinal})");
                }
                await connection.ExecuteAsync(new CommandDefinition(
                    $"INSERT INTO tmp_zip_path_inputs (ord, zip_path) VALUES {string.Join(", ", values)}", parameters,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            // Both joins compare with "=" under the column's own collation, in the database. Don't group or
            // partition by the path instead: a window partition over this column put an unequal path between two
            // equal ones (most likely the sort keys are cut at max_sort_length), splitting their group.
            foreach (var (ord, grp) in await connection.QueryAsync<(int Ord, int Grp)>(new CommandDefinition("""
                         SELECT a.ord, MIN(b.ord)
                         FROM tmp_zip_path_inputs a
                         JOIN tmp_zip_path_inputs b ON b.zip_path = a.zip_path
                         GROUP BY a.ord
                         """, cancellationToken: cancellationToken)).ConfigureAwait(false))
                groupOf[ord] = grp;
            var matches = await connection.QueryAsync<(int Ord, Guid Id, string ZipPath)>(new CommandDefinition("""
                SELECT t.ord, g.id, g.zip_path
                FROM tmp_zip_path_inputs t
                JOIN games g ON g.zip_path = t.zip_path
                ORDER BY g.zip_path, g.id
                """, cancellationToken: cancellationToken)).ConfigureAwait(false);
            owners.AddRange(matches.Select(m => (groupOf[m.Ord], m.Id, m.ZipPath)).Distinct());
        }
        finally
        {
            // Without the caller's token: a pooled connection must not carry the table into its next use.
            await connection.ExecuteAsync("DROP TEMPORARY TABLE IF EXISTS tmp_zip_path_inputs").ConfigureAwait(false);
        }

        var ownersByGroup = owners.ToLookup(o => o.Grp, o => new ZipPathOwner(o.Id, o.ZipPath));
        return Enumerable.Range(0, zipPaths.Count)
            .GroupBy(i => groupOf.GetValueOrDefault(i, i)) // an unstaged (overlong) path is its own group
            .OrderBy(g => g.Key)
            .Select(g => new ZipPathEquivalenceGroup(g.Select(i => zipPaths[i]).ToList(), ownersByGroup[g.Key].ToList()))
            .ToList();
    }

    public async Task<Page<GameListItem>> ListAsync(GameQuery query, CancellationToken cancellationToken)
    {
        var (filter, args) = Where(query);
        var words = query.Words;
        // B15/B17: inner join, so memberships of games no longer in the library don't show.
        var from = query.CollectionId is null
            ? "games g"
            : "games g JOIN collection_games cg ON cg.game_id = g.id AND cg.collection_id = @collection";

        // Fixed SQL fragments only; nothing user-supplied is spliced in.
        var dir = query.Descending ? "DESC" : "ASC";
        var order = query.Sort switch
        {
            GameSort.Year => $"g.release_year IS NULL, g.release_year {dir}, g.title, g.id",
            GameSort.Added when query.CollectionId is not null => $"cg.added_at {dir}, g.id",
            GameSort.Added => $"g.created_at {dir}, g.id",
            GameSort.Rating => $"g.rating IS NULL, g.rating {dir}, g.title, g.id",
            GameSort.Relevance when query.Phrase is not null => $"{RelevanceScore(words.Count)} DESC, g.title, g.id",
            GameSort.Relevance => "g.title, g.id",
            _ => $"g.title {dir}, g.id",
        };

        args.AddDynamicParams(new
        {
            collection = query.CollectionId,
            phrase = query.Phrase,
            prefix = query.Phrase is null ? null : SqlLike.Escape(query.Phrase) + "%",
            pageSize = query.PageSize,
            offset = (long)(query.Page - 1) * query.PageSize,
        });

        await using var connection = _connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {from} {filter}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<ListRow>(new CommandDefinition($"""
            SELECT g.id AS Id, g.system_id AS SystemId, g.display_title AS DisplayTitle, g.release_year AS ReleaseYear,
                   (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id) AS RomCount,
                   (SELECT GROUP_CONCAT(r.region_key SEPARATOR '') FROM roms r WHERE r.game_id = g.id) AS RegionKeys,
                   (SELECT MIN(i.id) FROM images i WHERE i.game_id = g.id AND i.is_cover = 1) AS BoxArtImageId,
                   (SELECT i.sha1 FROM images i WHERE i.game_id = g.id AND i.is_cover = 1 ORDER BY i.id LIMIT 1) AS BoxArtSha1,
                   (SELECT CAST(r.size_bytes + COALESCE((SELECT SUM(f.size_bytes) FROM rom_files f WHERE f.rom_id = r.id), 0) AS SIGNED)
                    FROM roms r WHERE r.game_id = g.id AND r.is_primary = 1 ORDER BY 1 DESC LIMIT 1) AS PrimarySizeBytes,
                   (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id AND JSON_CONTAINS(r.flags, '"Update"')) AS UpdateCount,
                   (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id AND JSON_CONTAINS(r.flags, '"DLC"') AND NOT JSON_CONTAINS(r.flags, '"Update"')) AS DlcCount,
                   (SELECT r.id FROM roms r WHERE r.game_id = g.id AND r.is_primary = 1 ORDER BY r.id LIMIT 1) AS PrimaryRomId,
                   (SELECT r.file_name FROM roms r WHERE r.game_id = g.id AND r.is_primary = 1 ORDER BY r.id LIMIT 1) AS PrimaryRomFileName,
                   (SELECT r.external_file_name IS NOT NULL FROM roms r WHERE r.game_id = g.id AND r.is_primary = 1 ORDER BY r.id LIMIT 1) AS PrimaryIsExternal,
                   (SELECT EXISTS(SELECT 1 FROM rom_files f WHERE f.rom_id = r.id) FROM roms r WHERE r.game_id = g.id AND r.is_primary = 1 ORDER BY r.id LIMIT 1) AS PrimaryHasFiles,
                   (SELECT JSON_EXTRACT(r.header, '$.encrypted') = 'true' FROM roms r WHERE r.game_id = g.id AND r.is_primary = 1 ORDER BY r.id LIMIT 1) AS PrimaryEncrypted,
                   g.rating AS Rating, g.favourite AS Favourite, g.save_count AS SaveCount, g.title AS Title, g.igdb_id AS IgdbId,
                   g.developers AS Developers, g.publishers AS Publishers, g.genres AS Genres,
                   {(words.Count == 0 ? "NULL" : AddOnNamesJson)} AS AddOns
            FROM {from} {filter}
            ORDER BY {order}
            LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));

        var items = rows.Select(r => new GameListItem(
            r.Id, r.SystemId, r.DisplayTitle, r.ReleaseYear, Regions(r.RegionKeys), (int)r.RomCount, r.BoxArtImageId, r.BoxArtSha1,
            r.Rating, r.Favourite, r.PrimarySizeBytes, SearchMatch.Find(words, r.Title, r.DisplayTitle, r.Developers, r.Publishers, r.Genres, AddOnNames(r.AddOns)),
            (int)r.UpdateCount, r.PrimaryRomId, r.PrimaryRomFileName, r.PrimaryIsExternal == true, r.SaveCount, (int)r.DlcCount, r.PrimaryHasFiles == true, r.PrimaryEncrypted == true, r.IgdbId)).ToList();
        return new Page<GameListItem>(items, query.Page, query.PageSize, total);
    }

    public async Task<GameFacets> ListFacetsAsync(GameQuery query, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();

        async Task<Dictionary<string, int>> Grouped(string column, GameQuery without)
        {
            var (filter, args) = Where(without);
            return (await connection.QueryAsync<(string Key, long Count)>(new CommandDefinition(
                    $"SELECT {column}, COUNT(*) FROM games g {filter} GROUP BY {column}", args, cancellationToken: cancellationToken)))
                .ToDictionary(r => r.Key, r => (int)r.Count, StringComparer.Ordinal);
        }

        var systems = await Grouped("g.system_id", query with { System = null });
        var letters = await Grouped("g.bucket", query with { Letter = null });
        var (regionFilter, regionArgs) = Where(query with { Region = null });
        // shortcut: regions are counted in memory from each matching game's region keys; fine for tens of thousands of
        // games, move to a rom_regions table if it gets slow.
        var keys = await connection.QueryAsync<string?>(new CommandDefinition(
            $"SELECT (SELECT GROUP_CONCAT(r.region_key SEPARATOR '') FROM roms r WHERE r.game_id = g.id) FROM games g {regionFilter}",
            regionArgs, cancellationToken: cancellationToken));
        var regions = keys.SelectMany(Regions).CountBy(r => r, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return new GameFacets(systems, regions, letters);
    }

    /// <summary>
    /// The library filters shared by the list and the facets (F2): the <c>WHERE</c> clause (empty when nothing applies) and
    /// its parameters. Null filters are left out, so a facet passes the query with its own filter cleared.
    /// </summary>
    private static (string Filter, DynamicParameters Args) Where(GameQuery query)
    {
        var where = new List<string>();
        if (query.System is not null)
            where.Add("g.system_id = @system");
        if (query.Letter is not null)
            where.Add("g.bucket = @letter");
        if (query.Region is not null)
            where.Add("EXISTS (SELECT 1 FROM roms r WHERE r.game_id = g.id AND r.region_key LIKE @region)");
        var words = query.Words;
        // shortcut: LIKE '%word%' over five columns and the DLC/update file names is a full scan, and list values are
        // matched in their JSON text (escaped characters may not match literally); move to FULLTEXT if search gets slow.
        for (var i = 0; i < words.Count; i++)
            where.Add($"({TitleLike(i)} OR {JsonLike("developers", i)} OR {JsonLike("publishers", i)} OR {JsonLike("genres", i)} OR {AddOnLike(i)})");
        if (query.Favourite is not null)
            where.Add("g.favourite = @favourite");
        if (query.NotInCollectionId is not null)
            where.Add("NOT EXISTS (SELECT 1 FROM collection_games x WHERE x.game_id = g.id AND x.collection_id = @notInCollection)");
        if (query.GameIds is not null)
            where.Add(query.GameIds.Count == 0 ? "FALSE" : "g.id IN @gameIds");
        if (query.YearFrom is not null)
            where.Add("g.release_year >= @yearFrom");
        if (query.YearTo is not null)
            where.Add("g.release_year <= @yearTo");
        if (query.IgdbLinked)
            where.Add("g.igdb_id > 0");
        var filter = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        var args = new DynamicParameters(new
        {
            system = query.System,
            notInCollection = query.NotInCollectionId,
            letter = query.Letter,
            region = query.Region is null ? null : SqlLike.Contains("|" + query.Region + "|"),
            favourite = query.Favourite,
            gameIds = query.GameIds is { Count: > 0 } ids ? ids : null,
            yearFrom = query.YearFrom,
            yearTo = query.YearTo,
        });
        for (var i = 0; i < words.Count; i++)
            args.Add($"w{i}", SqlLike.Contains(words[i]));
        return (filter, args);
    }

    private static string TitleLike(int i) => $"(g.display_title LIKE @w{i} OR g.title LIKE @w{i})";

    // JSON columns are utf8mb4_bin; compare them like the titles (case- and accent-insensitive).
    private static string JsonLike(string column, int i) => $"CAST(g.{column} AS CHAR) COLLATE utf8mb4_unicode_ci LIKE @w{i}";

    private const string IsAddOn = """(JSON_CONTAINS(r.flags, '"DLC"') OR JSON_CONTAINS(r.flags, '"Update"'))""";

    // DLC B8: roms.file_name is utf8mb4_unicode_ci, so it compares like the titles.
    private static string AddOnLike(int i) =>
        $"EXISTS (SELECT 1 FROM roms r WHERE r.game_id = g.id AND {IsAddOn} AND r.file_name LIKE @w{i})";

    // DLC B11: a rom flagged both counts as an update (B1).
    private const string AddOnNamesJson = $"""
        (SELECT JSON_ARRAYAGG(JSON_OBJECT('f', IF(JSON_CONTAINS(r.flags, '"Update"'), 'update', 'dlc'), 'n', r.file_name))
         FROM roms r WHERE r.game_id = g.id AND {IsAddOn})
        """;

    private static readonly JsonSerializerOptions AddOnJson = new() { PropertyNameCaseInsensitive = true };

    /// <returns>DLC B10: the add-on names, DLC before updates, each by ordinal file name.</returns>
    private static IReadOnlyList<AddOnName> AddOnNames(string? json) => json is null
        ? []
        : JsonSerializer.Deserialize<List<AddOnRow>>(json, AddOnJson)!
            .OrderBy(a => a.F == "dlc" ? 0 : 1).ThenBy(a => a.N, StringComparer.Ordinal)
            .Select(a => new AddOnName(a.F, ReleaseName.Of(a.N))).ToList();

    /// <summary>
    /// S2: 100 exact title + 60 title prefix + per word 40 title, 20 developers, 20 publishers, 15 DLC/update file names
    /// (DLC B9; only for a word not in the title), 10 genres.
    /// </summary>
    private static string RelevanceScore(int words)
    {
        var parts = new List<string>
        {
            "(CASE WHEN g.display_title = @phrase OR g.title = @phrase THEN 100 ELSE 0 END)",
            "(CASE WHEN g.display_title LIKE @prefix OR g.title LIKE @prefix THEN 60 ELSE 0 END)",
        };
        for (var i = 0; i < words; i++)
        {
            parts.Add($"(CASE WHEN {TitleLike(i)} THEN 40 ELSE 0 END)");
            parts.Add($"(CASE WHEN {JsonLike("developers", i)} THEN 20 ELSE 0 END)");
            parts.Add($"(CASE WHEN {JsonLike("publishers", i)} THEN 20 ELSE 0 END)");
            // DLC B9: add-on names repeat the title, so they score only for a word the title lacks.
            parts.Add($"(CASE WHEN NOT {TitleLike(i)} AND {AddOnLike(i)} THEN 15 ELSE 0 END)");
            parts.Add($"(CASE WHEN {JsonLike("genres", i)} THEN 10 ELSE 0 END)");
        }
        return "(" + string.Join(" + ", parts) + ")";
    }

    public async Task<IReadOnlyList<(int Year, int Count)>> ListReleaseYearCountsAsync(string? systemId, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<(int Year, long Count)>(new CommandDefinition("""
            SELECT release_year, COUNT(*) FROM games
            WHERE igdb_id > 0 AND release_year IS NOT NULL AND (@systemId IS NULL OR system_id = @systemId)
            GROUP BY release_year ORDER BY release_year
            """, new { systemId }, cancellationToken: cancellationToken))).Select(r => (r.Year, (int)r.Count)).ToList();
    }

    public async Task<IReadOnlyList<SystemStats>> ListSystemStatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        var games = (await connection.QueryAsync<(string SystemId, long Count)>(new CommandDefinition(
            "SELECT system_id, COUNT(*) FROM games GROUP BY system_id", cancellationToken: cancellationToken)))
            .ToDictionary(r => r.SystemId, r => r.Count);
        var roms = (await connection.QueryAsync<(string SystemId, long Count)>(new CommandDefinition(
            "SELECT system_id, COUNT(*) FROM roms GROUP BY system_id", cancellationToken: cancellationToken)))
            .ToDictionary(r => r.SystemId, r => r.Count);
        var regionKeys = (await connection.QueryAsync<(string SystemId, string RegionKey)>(new CommandDefinition(
            "SELECT DISTINCT system_id, region_key FROM roms", cancellationToken: cancellationToken)))
            .ToLookup(r => r.SystemId, r => r.RegionKey);

        return games.Keys.Order(StringComparer.Ordinal).Select(system => new SystemStats(
            system, (int)games[system], (int)roms.GetValueOrDefault(system), Regions(string.Concat(regionKeys[system])))).ToList();
    }

    public async Task<IReadOnlyList<GameRow>> ListMediaRetryCandidatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<GameRow>(new CommandDefinition($"""
            {Select} g
            WHERE g.has_default_boxart
               OR NOT EXISTS (SELECT 1 FROM images i WHERE i.game_id = g.id AND i.type = 'BoxArt')
               OR NOT EXISTS (SELECT 1 FROM images i WHERE i.game_id = g.id AND i.type = 'Snap')
            ORDER BY g.title, g.id
            """, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<GameRow>> ListPullCoverCandidatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<GameRow>(new CommandDefinition($"""
            {Select} g
            WHERE g.igdb_id > 0
              AND NOT EXISTS (SELECT 1 FROM game_actions a WHERE a.game_id = g.id AND a.cover_image_pulled_at IS NOT NULL)
            ORDER BY g.title, g.id
            """, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<GameRow>> ListStorageMovesAsync(IReadOnlyCollection<string> embedSystemIds,
        IReadOnlyCollection<string> externalSystemIds, CancellationToken cancellationToken)
    {
        if (embedSystemIds.Count == 0 && externalSystemIds.Count == 0)
            return [];
        await using var connection = _connections.Create();
        // Dapper expands an empty list to a never-true IN.
        return (await connection.QueryAsync<GameRow>(new CommandDefinition($"""
            {Select} g
            WHERE (g.system_id IN @embedSystemIds
                   AND EXISTS (SELECT 1 FROM roms r WHERE r.game_id = g.id AND r.external_file_name IS NOT NULL))
               OR (g.system_id IN @externalSystemIds
                   AND EXISTS (SELECT 1 FROM roms r WHERE r.game_id = g.id AND r.external_file_name IS NULL))
            ORDER BY g.zip_path
            """, new { embedSystemIds, externalSystemIds }, cancellationToken: cancellationToken))).AsList();
    }

    /// <summary>Concatenated <c>region_key</c>s (<c>|USA||Europe|</c>) → distinct regions, sorted ordinally.</summary>
    private static List<string> Regions(string? regionKeys) =>
        (regionKeys ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
