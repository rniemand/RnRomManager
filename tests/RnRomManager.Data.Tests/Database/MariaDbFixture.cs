using MySqlConnector;
using Testcontainers.MariaDb;

namespace RnRomManager.Data.Tests.Database;

/// <summary>One MariaDB container for every Integration test in this namespace (plan §1.4). Needs Docker.</summary>
[SetUpFixture]
public sealed class MariaDbFixture
{
    private static MariaDbContainer? _container;

    /// <summary>Root connection to the server (no database selected).</summary>
    public static string ServerConnectionString
    {
        get
        {
            var builder = new MySqlConnectionStringBuilder(Container.GetConnectionString()) { Database = "" };
            return builder.ConnectionString;
        }
    }

    private static MariaDbContainer Container =>
        _container ?? throw new InvalidOperationException("MariaDbFixture hasn't started.");

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _container = new MariaDbBuilder("mariadb:11.4").WithUsername("root").Build();
        await _container.StartAsync();
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
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
