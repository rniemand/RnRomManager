using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;

namespace RnRomManager.Data.Repositories;

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

    public async Task<IReadOnlyList<CoverRow>> ListCoversAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return (await connection.QueryAsync<CoverRow>(new CommandDefinition("""
            SELECT g.id AS GameId, g.zip_path AS ZipPath, i.file_name AS FileName, i.sha1 AS Sha1
            FROM images i JOIN games g ON g.id = i.game_id
            WHERE i.id = (SELECT MIN(c.id) FROM images c WHERE c.game_id = i.game_id AND c.is_cover = 1)
            ORDER BY g.id
            """, cancellationToken: cancellationToken))).AsList();
    }
}
