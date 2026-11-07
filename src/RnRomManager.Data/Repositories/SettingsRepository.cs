using Dapper;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;

namespace RnRomManager.Data.Repositories;

/// <summary><c>app_settings</c> (B15).</summary>
public sealed class SettingsRepository(IDbConnectionFactory connections) : ISettingsRepository
{
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0) return new Dictionary<string, string>();
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<(string Name, string Value)>(new CommandDefinition(
            "SELECT name, value FROM app_settings WHERE name IN @names", new { names }, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);
    }

    public async Task SetAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (name, value) in values)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO app_settings (name, value) VALUES (@name, @value) ON DUPLICATE KEY UPDATE value = VALUES(value)",
                new { name, value }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
