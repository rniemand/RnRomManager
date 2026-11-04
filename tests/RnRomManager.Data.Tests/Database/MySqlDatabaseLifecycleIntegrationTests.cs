using RnRomManager.TestData;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using RnRomManager.Core.Database;

namespace RnRomManager.Data.Tests.Database;

[Category("Integration")]
public class MySqlDatabaseLifecycleIntegrationTests
{
    /// <param name="withTestScripts">Use this test assembly's <c>Migrations/*.sql</c> instead of the app's.</param>
    private static MySqlDatabaseLifecycle Lifecycle(string connectionString, bool withTestScripts = false) => new(
        new MySqlConnectionFactory(connectionString), NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System)
    {
        WaitTimeout = TimeSpan.FromSeconds(5),
        RetryInterval = TimeSpan.FromMilliseconds(200),
        ScriptAssembly = withTestScripts
            ? typeof(MySqlDatabaseLifecycleIntegrationTests).Assembly
            : typeof(MySqlDatabaseLifecycle).Assembly,
    };

    [Test]
    public async Task WaitUntilAvailable_ReachableDatabase_Returns()
    {
        var cs = await MariaDbServer.CreateDatabaseAsync();

        await Assert.DoesNotThrowAsync(() => Lifecycle(cs).WaitUntilAvailableAsync(CancellationToken.None));
    }

    [Test]
    public async Task WaitUntilAvailable_UnknownDatabase_FailsImmediatelyNamingIt()
    {
        var cs = new MySqlConnectionStringBuilder(MariaDbServer.ServerConnectionString) { Database = "does_not_exist" }.ConnectionString;
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => Lifecycle(cs).WaitUntilAvailableAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.StartWith("Database 'does_not_exist' does not exist or user 'root' has no access"));
        Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(4)), "should not retry");
    }

    [Test]
    public async Task WaitUntilAvailable_AccessDenied_FailsImmediately()
    {
        var cs = new MySqlConnectionStringBuilder(await MariaDbServer.CreateDatabaseAsync()) { Password = "wrong" }.ConnectionString;

        var ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => Lifecycle(cs).WaitUntilAvailableAsync(CancellationToken.None));

        Assert.That(ex!.InnerException, Is.InstanceOf<MySqlException>());
        Assert.That(ex.Message, Does.Not.Contain("wrong"));
    }

    [Test]
    public async Task Migrate_AppliesScriptsOnce_AndJournalsThemInSchemaVersions()
    {
        var cs = await MariaDbServer.CreateDatabaseAsync();
        var lifecycle = Lifecycle(cs, withTestScripts: true);

        lifecycle.Migrate();
        lifecycle.Migrate(); // second run is a no-op

        Assert.That(await ScalarAsync(cs, "SELECT COUNT(*) FROM schema_versions"), Is.EqualTo(1));
        Assert.That(await ScalarAsync(cs, "SELECT ScriptName FROM schema_versions"), Does.EndWith(".Migrations.0001_test_table.sql"));
        Assert.That(await ScalarAsync(cs, "SELECT COUNT(*) FROM migration_test"), Is.EqualTo(1));
    }

    [Test]
    public async Task Migrate_FailingScript_ThrowsNamingTheScript()
    {
        var cs = await MariaDbServer.CreateDatabaseAsync();
        // The test script inserts into a table it creates; pre-creating it with an incompatible shape breaks it.
        await ExecuteAsync(cs, "CREATE TABLE migration_test (other INT NOT NULL)");

        var ex = Assert.Throws<DatabaseMigrationException>(() => Lifecycle(cs, withTestScripts: true).Migrate());

        Assert.That(ex!.Message, Does.Contain("0001_test_table.sql"));
    }

    [Test]
    public async Task Ping_ReachableDatabase_True_UnknownDatabase_False()
    {
        var cs = await MariaDbServer.CreateDatabaseAsync();
        var missing = new MySqlConnectionStringBuilder(cs) { Database = "does_not_exist" }.ConnectionString;

        Assert.That(await Lifecycle(cs).PingAsync(CancellationToken.None), Is.True);
        Assert.That(await Lifecycle(missing).PingAsync(CancellationToken.None), Is.False);
    }

    [Test]
    public async Task Connections_ReadDatetimeAsUtc()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST('2026-01-02 03:04:05.123456' AS DATETIME(6))";

        var value = (DateTime)(await command.ExecuteScalarAsync())!;

        Assert.That(value.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(value, Is.EqualTo(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560)));
    }

    private static async Task<object?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is long l ? (int)l : value;
    }

    private static async Task ExecuteAsync(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
