using System.Data;
using System.Text.Json;
using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;

namespace RnRomManager.Data.Repositories;

/// <summary>Reads/writes the JSON string-array columns (<c>developers</c>, <c>regions</c>, …).</summary>
internal sealed class JsonStringListHandler : SqlMapper.TypeHandler<IReadOnlyList<string>>
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            SqlMapper.AddTypeHandler(new JsonStringListHandler());
    }

    public override IReadOnlyList<string> Parse(object value) => JsonSerializer.Deserialize<List<string>>((string)value) ?? [];

    public override void SetValue(IDbDataParameter parameter, IReadOnlyList<string>? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = JsonSerializer.Serialize(value ?? []);
    }
}

public sealed class GameRepository : IGameRepository
{
    internal const string Select = """
        SELECT id AS Id, system_id AS SystemId, title AS Title, display_title AS DisplayTitle, zip_path AS ZipPath,
               bucket AS Bucket, release_year AS ReleaseYear, developers AS Developers, publishers AS Publishers,
               genres AS Genres, description AS Description, has_default_boxart AS HasDefaultBoxArt,
               rating AS Rating, favourite AS Favourite,
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

    public async Task<IReadOnlyList<GameRow>> ListAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<GameRow>(new CommandDefinition(Select, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<GameSyncState>> ListSyncStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        var rows = await connection.QueryAsync<(Guid Id, string ZipPath, long Size, DateTime Mtime)>(new CommandDefinition(
            "SELECT id, zip_path, zip_size_bytes, zip_mtime_utc FROM games", cancellationToken: cancellationToken));
        return rows.Select(r => new GameSyncState(r.Id, r.ZipPath, r.Size, r.Mtime)).ToList();
    }

    public async Task<Page<GameListItem>> ListAsync(GameQuery query, CancellationToken cancellationToken)
    {
        var where = new List<string>();
        if (query.System is not null)
            where.Add("g.system_id = @system");
        if (query.Letter is not null)
            where.Add("g.bucket = @letter");
        if (query.Region is not null)
            where.Add("EXISTS (SELECT 1 FROM roms r WHERE r.game_id = g.id AND r.region_key LIKE @region)");
        var words = query.Words;
        // shortcut: LIKE '%word%' over five columns is a full scan, and list values are matched in their JSON text
        // (escaped characters may not match literally); move to FULLTEXT if search gets slow.
        for (var i = 0; i < words.Count; i++)
            where.Add($"({TitleLike(i)} OR {JsonLike("developers", i)} OR {JsonLike("publishers", i)} OR {JsonLike("genres", i)})");
        if (query.Favourite is not null)
            where.Add("g.favourite = @favourite");
        var filter = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        // B15/B17: inner join, so memberships of games no longer in the library don't show.
        var from = query.CollectionId is null
            ? "games g"
            : "games g JOIN collection_games cg ON cg.game_id = g.id AND cg.collection_id = @collection";

        // Fixed SQL fragments only; nothing user-supplied is spliced in.
        var dir = query.Descending ? "DESC" : "ASC";
        var order = query.Sort switch
        {
            GameSort.Year => $"g.release_year IS NULL, g.release_year {dir}, g.id",
            GameSort.Added when query.CollectionId is not null => $"cg.added_at {dir}, g.id",
            GameSort.Added => $"g.created_at {dir}, g.id",
            GameSort.Rating => $"g.rating IS NULL, g.rating {dir}, g.title, g.id",
            GameSort.Relevance when query.Phrase is not null => $"{RelevanceScore(words.Count)} DESC, g.title, g.id",
            GameSort.Relevance => "g.title, g.id",
            _ => $"g.title {dir}, g.id",
        };

        var args = new DynamicParameters(new
        {
            system = query.System,
            collection = query.CollectionId,
            letter = query.Letter,
            region = query.Region is null ? null : SqlLike.Contains("|" + query.Region + "|"),
            favourite = query.Favourite,
            phrase = query.Phrase,
            prefix = query.Phrase is null ? null : SqlLike.Escape(query.Phrase) + "%",
            pageSize = query.PageSize,
            offset = (long)(query.Page - 1) * query.PageSize,
        });
        for (var i = 0; i < words.Count; i++)
            args.Add($"w{i}", SqlLike.Contains(words[i]));

        await using var connection = _connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {from} {filter}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<ListRow>(new CommandDefinition($"""
            SELECT g.id AS Id, g.system_id AS SystemId, g.display_title AS DisplayTitle, g.release_year AS ReleaseYear,
                   (SELECT COUNT(*) FROM roms r WHERE r.game_id = g.id) AS RomCount,
                   (SELECT GROUP_CONCAT(r.region_key SEPARATOR '') FROM roms r WHERE r.game_id = g.id) AS RegionKeys,
                   (SELECT MIN(i.id) FROM images i WHERE i.game_id = g.id AND i.is_cover = 1) AS BoxArtImageId,
                   (SELECT MAX(r.size_bytes) FROM roms r WHERE r.game_id = g.id AND r.is_primary = 1) AS PrimarySizeBytes,
                   g.rating AS Rating, g.favourite AS Favourite, g.title AS Title,
                   g.developers AS Developers, g.publishers AS Publishers, g.genres AS Genres
            FROM {from} {filter}
            ORDER BY {order}
            LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));

        var items = rows.Select(r => new GameListItem(
            r.Id, r.SystemId, r.DisplayTitle, r.ReleaseYear, Regions(r.RegionKeys), (int)r.RomCount, r.BoxArtImageId,
            r.Rating, r.Favourite, r.PrimarySizeBytes, SearchMatch.Find(words, r.Title, r.DisplayTitle, r.Developers, r.Publishers, r.Genres))).ToList();
        return new Page<GameListItem>(items, query.Page, query.PageSize, total);
    }

    private static string TitleLike(int i) => $"(g.display_title LIKE @w{i} OR g.title LIKE @w{i})";

    // JSON columns are utf8mb4_bin; compare them like the titles (case- and accent-insensitive).
    private static string JsonLike(string column, int i) => $"CAST(g.{column} AS CHAR) COLLATE utf8mb4_unicode_ci LIKE @w{i}";

    /// <summary>S2: 100 exact title + 60 title prefix + per word 40 title, 20 developers, 20 publishers, 10 genres.</summary>
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
            parts.Add($"(CASE WHEN {JsonLike("genres", i)} THEN 10 ELSE 0 END)");
        }
        return "(" + string.Join(" + ", parts) + ")";
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

    /// <summary>Concatenated <c>region_key</c>s (<c>|USA||Europe|</c>) → distinct regions, sorted ordinally.</summary>
    private static List<string> Regions(string? regionKeys) =>
        (regionKeys ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private sealed class ListRow
    {
        public Guid Id { get; init; }
        public string SystemId { get; init; } = "";
        public string DisplayTitle { get; init; } = "";
        public int? ReleaseYear { get; init; }
        public long RomCount { get; init; }
        public string? RegionKeys { get; init; }
        public long? BoxArtImageId { get; init; }
        public long? PrimarySizeBytes { get; init; }
        public int? Rating { get; init; }
        public bool Favourite { get; init; }
        public string Title { get; init; } = "";
        public IReadOnlyList<string> Developers { get; init; } = [];
        public IReadOnlyList<string> Publishers { get; init; } = [];
        public IReadOnlyList<string> Genres { get; init; } = [];
    }
}

public sealed class RomRepository : IRomRepository
{
    private const string Select = """
        SELECT id AS Id, game_id AS GameId, system_id AS SystemId, file_name AS FileName,
               original_file_name AS OriginalFileName, base_title AS BaseTitle, regions AS Regions,
               languages AS Languages, flags AS Flags, region_key AS RegionKey, revision AS Revision,
               is_primary AS IsPrimary, id_method AS IdMethod, dat_name AS DatName, dat_version AS DatVersion,
               dat_game_name AS DatGameName, size_bytes AS SizeBytes, crc32 AS Crc32, md5 AS Md5, sha1 AS Sha1,
               header AS Header, date_added AS DateAdded, date_modified AS DateModified
        FROM roms
        """;

    private readonly IDbConnectionFactory _connections;

    public RomRepository(IDbConnectionFactory connections)
    {
        JsonStringListHandler.Register();
        _connections = connections;
    }

    public async Task<IReadOnlyList<RomRow>> ListByGameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<RomRow>(new CommandDefinition(
            $"{Select} WHERE game_id = @gameId ORDER BY id", new { gameId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<RomRow?> GetAsync(Guid gameId, long id, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return await connection.QuerySingleOrDefaultAsync<RomRow>(new CommandDefinition(
            $"{Select} WHERE id = @id AND game_id = @gameId", new { gameId, id }, cancellationToken: cancellationToken));
    }

    public async Task<RomRow?> GetBySha1Async(string sha1, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return await connection.QuerySingleOrDefaultAsync<RomRow>(
            new CommandDefinition($"{Select} WHERE sha1 = @sha1", new { sha1 }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> ListGameIdsByBaseTitleAsync(string systemId, string baseTitle, CancellationToken cancellationToken)
    {
        // utf8mb4_unicode_ci: the comparison is case-insensitive.
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT game_id FROM roms WHERE system_id = @systemId AND base_title = @baseTitle",
            new { systemId, baseTitle }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<(Guid GameId, string Sha1)>> ListSha1sAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<(Guid, string)>(
            new CommandDefinition("SELECT game_id, sha1 FROM roms", cancellationToken: cancellationToken))).AsList();
    }
}

public sealed class ImageRepository(IDbConnectionFactory connections) : IImageRepository
{
    private const string Select = """
        SELECT id AS Id, game_id AS GameId, file_name AS FileName, type AS Type, source AS Source,
               source_url AS SourceUrl, is_default AS IsDefault, is_cover AS IsCover, size_bytes AS SizeBytes, sha1 AS Sha1,
               width AS Width, height AS Height, date_added AS DateAdded
        FROM images
        """;

    public async Task<IReadOnlyList<ImageRow>> ListByGameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return (await connection.QueryAsync<ImageRow>(new CommandDefinition(
            $"{Select} WHERE game_id = @gameId ORDER BY id", new { gameId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<ImageRow?> GetAsync(Guid gameId, long id, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.QuerySingleOrDefaultAsync<ImageRow>(new CommandDefinition(
            $"{Select} WHERE id = @id AND game_id = @gameId", new { gameId, id }, cancellationToken: cancellationToken));
    }
}

public sealed class LibraryMirror : ILibraryMirror
{
    private readonly IDbConnectionFactory _connections;

    public LibraryMirror(IDbConnectionFactory connections)
    {
        JsonStringListHandler.Register();
        _connections = connections;
    }

    public async Task ApplyAsync(IReadOnlyList<MirrorEntry> replace, IReadOnlyCollection<Guid> delete, CancellationToken cancellationToken)
    {
        var rows = replace.Select(e => e.ToRows()).ToList();
        var ids = delete.Concat(rows.Select(r => r.Game.Id)).Distinct().ToList();
        var zipPaths = rows.Select(r => r.Game.ZipPath).ToList();

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Cascades to roms/images. Deleting first lets moves and swapped paths re-insert without unique clashes.
        if (ids.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM games WHERE id IN @ids",
                new { ids }, transaction, cancellationToken: cancellationToken));
        if (zipPaths.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM games WHERE zip_path IN @zipPaths",
                new { zipPaths }, transaction, cancellationToken: cancellationToken));

        foreach (var (game, roms, images) in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO games (id, system_id, title, display_title, zip_path, bucket, release_year, developers, publishers,
                                   genres, description, has_default_boxart, rating, favourite, zip_size_bytes, zip_mtime_utc, info_schema_version,
                                   created_at, updated_at)
                VALUES (@Id, @SystemId, @Title, @DisplayTitle, @ZipPath, @Bucket, @ReleaseYear, @Developers, @Publishers,
                        @Genres, @Description, @HasDefaultBoxArt, @Rating, @Favourite, @ZipSizeBytes, @ZipMtimeUtc, @InfoSchemaVersion,
                        @CreatedAt, @UpdatedAt)
                """, game, transaction, cancellationToken: cancellationToken));

            if (roms.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO roms (game_id, system_id, file_name, original_file_name, base_title, regions, languages, flags,
                                      region_key, revision, is_primary, id_method, dat_name, dat_version, dat_game_name,
                                      size_bytes, crc32, md5, sha1, header, date_added, date_modified)
                    VALUES (@GameId, @SystemId, @FileName, @OriginalFileName, @BaseTitle, @Regions, @Languages, @Flags,
                            @RegionKey, @Revision, @IsPrimary, @IdMethod, @DatName, @DatVersion, @DatGameName,
                            @SizeBytes, @Crc32, @Md5, @Sha1, @Header, @DateAdded, @DateModified)
                    """, roms.Select(r => new
                    {
                        r.GameId, r.SystemId, r.FileName, r.OriginalFileName, r.BaseTitle, r.Regions, r.Languages, r.Flags,
                        r.RegionKey, r.Revision, r.IsPrimary, IdMethod = r.IdMethod.ToString(), r.DatName, r.DatVersion,
                        r.DatGameName, r.SizeBytes, r.Crc32, r.Md5, r.Sha1, r.Header, r.DateAdded, r.DateModified,
                    }), transaction, cancellationToken: cancellationToken));

            if (images.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO images (game_id, file_name, type, source, source_url, is_default, is_cover, size_bytes, sha1, width, height, date_added)
                    VALUES (@GameId, @FileName, @Type, @Source, @SourceUrl, @IsDefault, @IsCover, @SizeBytes, @Sha1, @Width, @Height, @DateAdded)
                    """, images.Select(i => new
                    {
                        i.GameId, i.FileName, Type = i.Type.ToString(), i.Source, i.SourceUrl, i.IsDefault, i.IsCover, i.SizeBytes, i.Sha1,
                        i.Width, i.Height, i.DateAdded,
                    }), transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
