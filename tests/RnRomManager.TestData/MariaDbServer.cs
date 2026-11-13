using MySqlConnector;
using Testcontainers.MariaDb;

namespace RnRomManager.TestData;

/// <summary>
/// One MariaDB 11.4 container per test assembly (plan §1.4). Each assembly's Integration namespace has a
/// <c>[SetUpFixture]</c> that calls <see cref="StartAsync"/> / <see cref="StopAsync"/>. Needs Docker.
/// </summary>
public static class MariaDbServer
{
    private static MariaDbContainer? _container;

    /// <summary>Root connection to the server (no database selected).</summary>
    public static string ServerConnectionString =>
        new MySqlConnectionStringBuilder(Container.GetConnectionString()) { Database = "" }.ConnectionString;

    private static MariaDbContainer Container =>
        _container ?? throw new InvalidOperationException("MariaDbServer hasn't been started by a [SetUpFixture].");

    public static async Task StartAsync()
    {
        _container = new MariaDbBuilder("mariadb:11.4")
            .WithUsername("root")
            // Throwaway data: keep it in memory and skip durability work. Every test creates a database and runs the
            // migrations (dozens of CREATE/ALTER TABLE), which is mostly file creation and fsync on disk.
            .WithTmpfsMount("/var/lib/mysql")
            .WithCommand(
                "--innodb-flush-log-at-trx-commit=0",
                "--innodb-doublewrite=0",
                "--sync-binlog=0",
                "--skip-log-bin")
            .Build();
        await _container.StartAsync();
    }

    public static async Task StopAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
        _container = null;
    }

    /// <summary>A fresh, empty <c>utf8mb4_unicode_ci</c> database, so tests don't share state.</summary>
    public static async Task<string> CreateDatabaseAsync()
    {
        // Each test database gets its own connection pool; idle ones from earlier tests would pile up past the
        // server's max_connections (151) and later hosts would time out waiting for the DB.
        await MySqlConnection.ClearAllPoolsAsync();
        var name = "t_" + Guid.NewGuid().ToString("N")[..16];
        await using var connection = new MySqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
        await command.ExecuteNonQueryAsync();
        return new MySqlConnectionStringBuilder(ServerConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>
    /// Copies every table of the database in <paramref name="connectionString"/> into a <c>&lt;name&gt;_snap</c>
    /// database, for <see cref="RestoreAsync"/> (shared test hosts reset to their just-started state per test).
    /// </summary>
    public static async Task SnapshotAsync(string connectionString)
    {
        var name = new MySqlConnectionStringBuilder(connectionString).Database;
        await using var connection = new MySqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        var tables = await TablesAsync(connection, name);
        var sql = new System.Text.StringBuilder($"DROP DATABASE IF EXISTS `{name}_snap`; CREATE DATABASE `{name}_snap`;");
        foreach (var table in tables)
            sql.Append($"CREATE TABLE `{name}_snap`.`{table}` LIKE `{name}`.`{table}`; INSERT INTO `{name}_snap`.`{table}` SELECT * FROM `{name}`.`{table}`;");
        await using var command = new MySqlCommand(sql.ToString(), connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Puts every table back to its <see cref="SnapshotAsync"/> rows, auto-increment counters included.</summary>
    public static async Task RestoreAsync(string connectionString)
    {
        var name = new MySqlConnectionStringBuilder(connectionString).Database;
        await using var connection = new MySqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        var tables = await TablesAsync(connection, name);
        var sql = new System.Text.StringBuilder("SET FOREIGN_KEY_CHECKS = 0;");
        foreach (var table in tables)
        {
            sql.Append($"DELETE FROM `{name}`.`{table}`; INSERT INTO `{name}`.`{table}` SELECT * FROM `{name}_snap`.`{table}`;");
            sql.Append($"ALTER TABLE `{name}`.`{table}` AUTO_INCREMENT = 1;");
        }
        sql.Append("SET FOREIGN_KEY_CHECKS = 1;");
        await using var command = new MySqlCommand(sql.ToString(), connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> TablesAsync(MySqlConnection connection, string database)
    {
        await using var command = new MySqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @db AND table_type = 'BASE TABLE'", connection);
        command.Parameters.AddWithValue("@db", database);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        return tables;
    }
}
