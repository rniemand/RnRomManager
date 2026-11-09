using Dapper;
using MySqlConnector;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Database;

namespace RnRomManager.Data.Repositories;

/// <summary><c>collections</c> (B1–B7).</summary>
public sealed class CollectionRepository(IDbConnectionFactory connections) : ICollectionRepository
{
    // B6/B17: only memberships whose game still exists count.
    private const string Select = """
        SELECT c.id AS Id, c.user_id AS UserId, c.name AS Name, c.notes AS Notes, c.cover_format AS CoverFormat,
               c.cover_updated_at AS CoverUpdatedAt, c.created_at AS CreatedAt, c.updated_at AS UpdatedAt,
               (SELECT COUNT(*) FROM collection_games cg JOIN games g ON g.id = cg.game_id
                WHERE cg.collection_id = c.id) AS GameCount
        FROM collections c
        """;

    public async Task InsertAsync(Collection collection, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO collections (id, user_id, name, notes, cover_format, cover_updated_at, created_at, updated_at)
                VALUES (@Id, @UserId, @Name, @Notes, @CoverFormat, @CoverUpdatedAt, @CreatedAt, @UpdatedAt)
                """, collection, cancellationToken: cancellationToken));
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new CollectionNameTakenException(collection.Name, ex);
        }
    }

    public async Task<Collection?> GetAsync(Guid id, int userId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.QuerySingleOrDefaultAsync<Collection>(new CommandDefinition(
            $"{Select} WHERE c.id = @id AND c.user_id = @userId", new { id, userId }, cancellationToken: cancellationToken));
    }

    public async Task<Page<Collection>> ListAsync(
        int userId, IReadOnlyList<string> words, int page, int pageSize, CancellationToken cancellationToken)
    {
        var where = "WHERE c.user_id = @userId";
        var args = new DynamicParameters(new { userId, pageSize, offset = (long)Page<object>.Offset(page, pageSize) });
        for (var i = 0; i < words.Count; i++)
        {
            // The column is accent-sensitive (uniqueness); search is not (B6), like GameRepository.JsonLike.
            where += $" AND c.name COLLATE utf8mb4_unicode_ci LIKE @w{i}";
            args.Add($"w{i}", SqlLike.Contains(words[i]));
        }

        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM collections c {where}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<Collection>(new CommandDefinition(
            $"{Select} {where} ORDER BY c.name, c.id LIMIT @pageSize OFFSET @offset", args, cancellationToken: cancellationToken));
        return new Page<Collection>(rows.AsList(), page, pageSize, total);
    }

    public async Task<bool> UpdateAsync(Collection collection, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        try
        {
            // Rows matched, not changed (connector default UseAffectedRows=false): an identical write still finds the row.
            return await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE collections SET name = @Name, notes = @Notes, cover_format = @CoverFormat,
                       cover_updated_at = @CoverUpdatedAt, updated_at = @UpdatedAt
                WHERE id = @Id AND user_id = @UserId
                """, collection, cancellationToken: cancellationToken)) > 0;
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new CollectionNameTakenException(collection.Name, ex);
        }
    }

    public async Task<bool> DeleteAsync(Guid id, int userId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM collections WHERE id = @id AND user_id = @userId", new { id, userId }, cancellationToken: cancellationToken)) > 0;
    }
}
