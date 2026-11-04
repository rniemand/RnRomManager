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
        _container = new MariaDbBuilder("mariadb:11.4").WithUsername("root").Build();
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
        var name = "t_" + Guid.NewGuid().ToString("N")[..16];
        await using var connection = new MySqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
        await command.ExecuteNonQueryAsync();
        return new MySqlConnectionStringBuilder(ServerConnectionString) { Database = name }.ConnectionString;
    }
}
