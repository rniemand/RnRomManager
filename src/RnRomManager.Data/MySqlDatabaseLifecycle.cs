using System.Reflection;
using DbUp;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using RnRomManager.Core.Database;

namespace RnRomManager.Data;

/// <summary>DB wait, DbUp migrations and the readiness ping (spec §4.1 steps 5–6, §14.3, §17.1).</summary>
public sealed class MySqlDatabaseLifecycle(
    MySqlConnectionFactory connections,
    ILogger<MySqlDatabaseLifecycle> logger,
    TimeProvider time) : IDatabaseLifecycle
{
    public const string JournalTable = "schema_versions";

    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Embedded <c>*.Migrations.*.sql</c> scripts are read from here (tests swap it).</summary>
    public Assembly ScriptAssembly { get; init; } = typeof(MySqlDatabaseLifecycle).Assembly;

    public async Task WaitUntilAvailableAsync(CancellationToken cancellationToken)
    {
        var database = connections.Database;
        var deadline = time.GetUtcNow() + WaitTimeout;

        for (var attempt = 1; ; attempt++)
        {
            var remaining = deadline - time.GetUtcNow();
            try
            {
                // Each attempt gets only the time left, so one hanging connect can't overrun the wait.
                using var attemptTimeout = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);
                await SelectOneAsync(linked.Token);
                logger.LogInformation("Database {Database} is available", database);
                return;
            }
            catch (MySqlException ex) when (IsPermanent(ex))
            {
                // Retrying can't fix these: fail now, naming the database (spec §4.1 step 5).
                throw new DatabaseUnavailableException(
                    $"Database '{database}' does not exist or user '{connections.UserId}' has no access to it: {ex.Message}", ex);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                remaining = deadline - time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    throw new DatabaseUnavailableException(
                        $"Database '{database}' was not reachable within {WaitTimeout.TotalSeconds:0} s: {ex.Message}", ex);
                }

                logger.LogWarning("Database {Database} not reachable (attempt {Attempt}), retrying: {Error}", database, attempt, ex.Message);
                await Task.Delay(remaining < RetryInterval ? remaining : RetryInterval, time, cancellationToken);
            }
        }
    }

    public void Migrate()
    {
        // DbUp journals `applied` with a local DateTime.Now, which MySqlConnector rejects under DateTimeKind=Utc.
        // Only DbUp's own schema_versions row is affected; app data still goes through the forced-UTC factory.
        var dbUpConnectionString = new MySqlConnectionStringBuilder(connections.ConnectionString)
        {
            DateTimeKind = MySqlDateTimeKind.Unspecified,
        }.ConnectionString;

        var result = DeployChanges.To
            .MySqlDatabase(dbUpConnectionString)
            .WithScriptsEmbeddedInAssembly(ScriptAssembly, name => name.Contains(".Migrations.", StringComparison.Ordinal))
            .JournalToMySqlTable(connections.Database, JournalTable)
            .LogTo(new DbUpLogger(logger))
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            throw new DatabaseMigrationException(
                $"Migration {result.ErrorScript?.Name ?? "(unknown script)"} failed: {result.Error?.Message}", result.Error);
        }

        var applied = result.Scripts.Select(s => s.Name).ToList();
        if (applied.Count == 0)
            logger.LogInformation("Database schema is up to date");
        else
            logger.LogInformation("Applied {Count} migration(s): {Scripts}", applied.Count, string.Join(", ", applied));
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(PingTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await SelectOneAsync(linked.Token);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Database ping failed: {Error}", ex.Message);
            return false;
        }
    }

    private async Task SelectOneAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static bool IsPermanent(MySqlException ex) => ex.ErrorCode is
        MySqlErrorCode.UnknownDatabase or MySqlErrorCode.AccessDenied or MySqlErrorCode.DatabaseAccessDenied;
}
