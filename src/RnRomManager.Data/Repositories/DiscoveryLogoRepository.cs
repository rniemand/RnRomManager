using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Discovery;

namespace RnRomManager.Data.Repositories;

/// <summary><c>discovery_logos</c> (B5, B13). The kind is stored lowercase; the hex key as its 32 digest bytes.</summary>
public sealed class DiscoveryLogoRepository(IDbConnectionFactory connections) : IDiscoveryLogoRepository
{
    private static string Kind(LogoKind kind) => kind.ToString().ToLowerInvariant();

    public async Task<DiscoveryLogo?> GetAsync(LogoKind kind, string key, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<(string Key, string MediaType, byte[] Bytes, int? UpdatedBy, DateTime UpdatedAt)>(
            new CommandDefinition("""
                SELECT LOWER(HEX(key_digest)), media_type, bytes, updated_by, updated_at
                FROM discovery_logos WHERE kind = @kind AND key_digest = UNHEX(@key)
                """, new { kind = Kind(kind), key }, cancellationToken: cancellationToken));
        return rows.Select(r => new DiscoveryLogo(kind, r.Key, r.MediaType, r.Bytes, r.UpdatedBy, r.UpdatedAt)).SingleOrDefault();
    }

    public async Task UpsertAsync(DiscoveryLogo logo, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO discovery_logos (kind, key_digest, media_type, bytes, updated_by, updated_at)
            VALUES (@Kind, UNHEX(@Key), @MediaType, @Bytes, @UpdatedBy, @UpdatedAt)
            ON DUPLICATE KEY UPDATE media_type = VALUES(media_type), bytes = VALUES(bytes),
                                    updated_by = VALUES(updated_by), updated_at = VALUES(updated_at)
            """, new { Kind = Kind(logo.Kind), logo.Key, logo.MediaType, logo.Bytes, logo.UpdatedBy, logo.UpdatedAt },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteAsync(LogoKind kind, string key, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM discovery_logos WHERE kind = @kind AND key_digest = UNHEX(@key)",
            new { kind = Kind(kind), key }, cancellationToken: cancellationToken)) > 0;
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> ListUpdatedAtAsync(
        LogoKind kind, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
            return new Dictionary<string, DateTime>();
        var args = new DynamicParameters();
        args.Add("kind", Kind(kind));
        var names = keys.Select((key, i) =>
        {
            args.Add($"k{i}", key);
            return $"UNHEX(@k{i})";
        }).ToList();
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<(string Key, DateTime UpdatedAt)>(new CommandDefinition(
            $"SELECT LOWER(HEX(key_digest)), updated_at FROM discovery_logos WHERE kind = @kind AND key_digest IN ({string.Join(", ", names)})",
            args, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.Key, r => DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc), StringComparer.Ordinal);
    }
}
