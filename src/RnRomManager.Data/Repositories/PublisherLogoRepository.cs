using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Discovery;

namespace RnRomManager.Data.Repositories;

/// <summary><c>publisher_logos</c> (B5). The hex key is stored as its 32 digest bytes.</summary>
public sealed class PublisherLogoRepository(IDbConnectionFactory connections) : IPublisherLogoRepository
{
    public async Task<PublisherLogo?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.QuerySingleOrDefaultAsync<PublisherLogo>(new CommandDefinition("""
            SELECT LOWER(HEX(key_digest)) AS `Key`, media_type AS MediaType, bytes AS Bytes, updated_by AS UpdatedBy,
                   updated_at AS UpdatedAt
            FROM publisher_logos WHERE key_digest = UNHEX(@key)
            """, new { key }, cancellationToken: cancellationToken));
    }

    public async Task UpsertAsync(PublisherLogo logo, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO publisher_logos (key_digest, media_type, bytes, updated_by, updated_at)
            VALUES (UNHEX(@Key), @MediaType, @Bytes, @UpdatedBy, @UpdatedAt)
            ON DUPLICATE KEY UPDATE media_type = VALUES(media_type), bytes = VALUES(bytes),
                                    updated_by = VALUES(updated_by), updated_at = VALUES(updated_at)
            """, logo, cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM publisher_logos WHERE key_digest = UNHEX(@key)", new { key }, cancellationToken: cancellationToken)) > 0;
    }
}
