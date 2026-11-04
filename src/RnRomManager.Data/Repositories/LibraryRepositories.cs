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

    public async Task<IReadOnlyList<GameSyncState>> ListSyncStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        var rows = await connection.QueryAsync<(Guid Id, string ZipPath, long Size, DateTime Mtime)>(new CommandDefinition(
            "SELECT id, zip_path, zip_size_bytes, zip_mtime_utc FROM games", cancellationToken: cancellationToken));
        return rows.Select(r => new GameSyncState(r.Id, r.ZipPath, r.Size, r.Mtime)).ToList();
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

    public async Task<RomRow?> GetBySha1Async(string sha1, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return await connection.QuerySingleOrDefaultAsync<RomRow>(
            new CommandDefinition($"{Select} WHERE sha1 = @sha1", new { sha1 }, cancellationToken: cancellationToken));
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
    public async Task<IReadOnlyList<ImageRow>> ListByGameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return (await connection.QueryAsync<ImageRow>(new CommandDefinition("""
            SELECT id AS Id, game_id AS GameId, file_name AS FileName, type AS Type, source AS Source,
                   source_url AS SourceUrl, is_default AS IsDefault, size_bytes AS SizeBytes, sha1 AS Sha1,
                   width AS Width, height AS Height, date_added AS DateAdded
            FROM images WHERE game_id = @gameId ORDER BY id
            """, new { gameId }, cancellationToken: cancellationToken))).AsList();
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
                                   genres, description, has_default_boxart, zip_size_bytes, zip_mtime_utc, info_schema_version,
                                   created_at, updated_at)
                VALUES (@Id, @SystemId, @Title, @DisplayTitle, @ZipPath, @Bucket, @ReleaseYear, @Developers, @Publishers,
                        @Genres, @Description, @HasDefaultBoxArt, @ZipSizeBytes, @ZipMtimeUtc, @InfoSchemaVersion,
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
                    INSERT INTO images (game_id, file_name, type, source, source_url, is_default, size_bytes, sha1, width, height, date_added)
                    VALUES (@GameId, @FileName, @Type, @Source, @SourceUrl, @IsDefault, @SizeBytes, @Sha1, @Width, @Height, @DateAdded)
                    """, images.Select(i => new
                    {
                        i.GameId, i.FileName, Type = i.Type.ToString(), i.Source, i.SourceUrl, i.IsDefault, i.SizeBytes, i.Sha1,
                        i.Width, i.Height, i.DateAdded,
                    }), transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
