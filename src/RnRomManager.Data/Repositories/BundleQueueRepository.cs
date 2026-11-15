using System.Data.Common;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using RnRomManager.Core.Bundles;
using RnRomManager.Core.Database;

namespace RnRomManager.Data.Repositories;

/// <summary><c>bundle_queues</c> (B2, B4): owned, revisioned snapshots stored as one JSON array per queue.</summary>
public sealed class BundleQueueRepository(IDbConnectionFactory connections) : IBundleQueueRepository
{
    // Relaxed escaping like Hotkeys' stored JSON, so non-ASCII labels aren't expanded to \uXXXX.
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Row(Guid Id, string Name, int Revision, string Entries);

    public async Task<IReadOnlyList<BundleQueueSummary>> ListAsync(int userId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<(Guid Id, string Name, int Revision, int ItemCount)>(new CommandDefinition("""
            SELECT id, name, revision, item_count FROM bundle_queues WHERE user_id = @userId ORDER BY name, id
            """, new { userId }, cancellationToken: cancellationToken));
        return rows.Select(r => new BundleQueueSummary(r.Id, r.Name, r.Revision, r.ItemCount)).ToList();
    }

    public async Task<BundleQueue?> GetAsync(Guid id, int userId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("""
            SELECT id AS Id, name AS Name, revision AS Revision, entries AS Entries
            FROM bundle_queues WHERE id = @id AND user_id = @userId
            """, new { id, userId }, cancellationToken: cancellationToken));
        return row is null ? null : ToQueue(row);
    }

    public async Task<BundleQueueWrite> CreateAsync(int userId, Guid id, string name, IReadOnlyList<BundleEntry> entries,
        DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // B4: lock the owner's users row so concurrent creates check the name and the cap one at a time.
        var owner = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT id FROM users WHERE id = @userId FOR UPDATE", new { userId }, transaction, cancellationToken: cancellationToken));
        if (owner is null) return BundleQueueWrite.NotFound;
        if (await NameTakenAsync(connection, transaction, userId, name, null, cancellationToken)) return BundleQueueWrite.NameTaken;

        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM bundle_queues WHERE user_id = @userId", new { userId }, transaction, cancellationToken: cancellationToken));
        if (count >= BundleQueueLimits.MaxQueuesPerUser) return BundleQueueWrite.Limit;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO bundle_queues (id, user_id, name, revision, item_count, entries, created_at, updated_at)
                VALUES (@id, @userId, @name, 1, @itemCount, @entries, @now, @now)
                """, new { id, userId, name, itemCount = entries.Count, entries = Serialize(entries), now },
                transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return BundleQueueWrite.NameTaken;
        }

        return new BundleQueueWrite(BundleQueueWriteStatus.Ok, new BundleQueue(id, name, 1, entries));
    }

    public async Task<BundleQueueWrite> ReplaceAsync(Guid id, int userId, string name, IReadOnlyList<BundleEntry> entries,
        int revision, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var status = await CheckAsync(connection, transaction, id, userId, revision, cancellationToken);
        if (status != BundleQueueWriteStatus.Ok) return new BundleQueueWrite(status);
        if (await NameTakenAsync(connection, transaction, userId, name, id, cancellationToken)) return BundleQueueWrite.NameTaken;

        try
        {
            // Conditional on the revision as well as the row lock, so a stale write can never land.
            var updated = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE bundle_queues
                SET name = @name, revision = revision + 1, item_count = @itemCount, entries = @entries, updated_at = @now
                WHERE id = @id AND user_id = @userId AND revision = @revision
                """, new { id, userId, name, itemCount = entries.Count, entries = Serialize(entries), now, revision },
                transaction, cancellationToken: cancellationToken));
            if (updated == 0) return BundleQueueWrite.Changed;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return BundleQueueWrite.NameTaken;
        }

        return new BundleQueueWrite(BundleQueueWriteStatus.Ok, new BundleQueue(id, name, revision + 1, entries));
    }

    public async Task<BundleQueueWriteStatus> DeleteAsync(Guid id, int userId, int revision, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var status = await CheckAsync(connection, transaction, id, userId, revision, cancellationToken);
        if (status != BundleQueueWriteStatus.Ok) return status;

        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM bundle_queues WHERE id = @id AND user_id = @userId AND revision = @revision",
            new { id, userId, revision }, transaction, cancellationToken: cancellationToken));
        if (deleted == 0) return BundleQueueWriteStatus.Changed;
        await transaction.CommitAsync(cancellationToken);
        return BundleQueueWriteStatus.Ok;
    }

    /// <summary>Ownership first (locking the row), then the revision.</summary>
    private static async Task<BundleQueueWriteStatus> CheckAsync(DbConnection connection, DbTransaction transaction,
        Guid id, int userId, int revision, CancellationToken cancellationToken)
    {
        var current = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT revision FROM bundle_queues WHERE id = @id AND user_id = @userId FOR UPDATE",
            new { id, userId }, transaction, cancellationToken: cancellationToken));
        if (current is null) return BundleQueueWriteStatus.NotFound;
        return current == revision ? BundleQueueWriteStatus.Ok : BundleQueueWriteStatus.Changed;
    }

    private static Task<bool> NameTakenAsync(DbConnection connection, DbTransaction transaction, int userId, string name,
        Guid? except, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM bundle_queues WHERE user_id = @userId AND name = @name AND (@except IS NULL OR id <> @except))
            """, new { userId, name, except }, transaction, cancellationToken: cancellationToken));

    private static string Serialize(IReadOnlyList<BundleEntry> entries) => JsonSerializer.Serialize(entries, StoredJson);

    private static BundleQueue ToQueue(Row row) => new(
        row.Id, row.Name, row.Revision,
        JsonSerializer.Deserialize<List<BundleEntry>>(row.Entries, StoredJson) ?? []);
}
