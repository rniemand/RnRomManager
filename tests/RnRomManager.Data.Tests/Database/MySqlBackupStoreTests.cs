using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Backups;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>B13 backup dumps against MariaDB.</summary>
[Category("Integration")]
public class MySqlBackupStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateTime Created = new(2026, 11, 5, 10, 11, 12, 345_678 / 1000, DateTimeKind.Utc);

    private MySqlConnectionFactory _factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(_factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
    }

    private async Task<ZipArchive> DumpAsync(Guid id)
    {
        var stream = new MemoryStream();
        await new MySqlBackupStore(_factory).DumpAsync(stream, id, T0, default);
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static string Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new AssertionException($"No entry {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static List<JsonObject> Rows(ZipArchive zip, string table) =>
        Read(zip, BackupManifest.DataEntry(table)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToList();

    private async Task SeedUserAsync()
    {
        await using var connection = _factory.Create();
        await connection.ExecuteAsync("""
            INSERT INTO users (username, password_hash, is_disabled, must_change_password, failed_login_count, security_stamp,
                               created_at, updated_at)
            VALUES ('alice', 'hash', 0, 1, 0, 'stamp', @Created, @Created)
            """, new { Created });
    }

    [Test]
    public async Task Dump_WritesManifestWithMigrationsAndRowCounts()
    {
        await SeedUserAsync();
        var id = Guid.NewGuid();

        using var zip = await DumpAsync(id);

        var manifest = JsonNode.Parse(Read(zip, BackupManifest.FileName))!.AsObject();
        await using var connection = _factory.Create();
        var scripts = (await connection.QueryAsync<string>("SELECT scriptname FROM schema_versions ORDER BY schemaversionid")).ToList();
        var tables = (await connection.QueryAsync<string>("""
            SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'
            """)).Order(StringComparer.Ordinal).ToList();

        Assert.That(manifest["formatVersion"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(manifest["backupId"]!.GetValue<string>(), Is.EqualTo(id.ToString("D")));
        Assert.That(manifest["createdAt"]!.GetValue<string>(), Is.EqualTo("2026-11-06T03:30:00.0000000Z"));
        Assert.That(manifest["appVersion"]!.GetValue<string>(), Is.Not.Empty);
        Assert.That(manifest["database"]!.GetValue<string>(), Is.EqualTo(_factory.Database));
        Assert.That(scripts, Has.Count.GreaterThanOrEqualTo(14));
        Assert.That(manifest["migrations"]!.AsArray().Select(n => n!.GetValue<string>()), Is.EqualTo(scripts));
        var listed = manifest["tables"]!.AsArray().Select(n => (n!["name"]!.GetValue<string>(), n["rows"]!.GetValue<long>())).ToList();
        Assert.That(listed.Select(t => t.Item1), Is.EqualTo(tables));
        Assert.That(listed, Does.Contain(("users", 1L)));
        Assert.That(listed, Does.Contain(("schema_versions", (long)scripts.Count)));
        foreach (var (table, rows) in listed)
            Assert.That(Rows(zip, table), Has.Count.EqualTo(rows), table);
    }

    [Test]
    public async Task Dump_WritesCreateTableForEveryTable()
    {
        using var zip = await DumpAsync(Guid.NewGuid());

        var schema = Read(zip, BackupManifest.SchemaFileName);
        await using var connection = _factory.Create();
        var tables = await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'");
        foreach (var table in tables)
            Assert.That(schema, Does.Contain($"CREATE TABLE `{table}` (").And.Contain(";"), table);
        Assert.That(schema.IndexOf("CREATE TABLE `audit_events`", StringComparison.Ordinal),
            Is.LessThan(schema.IndexOf("CREATE TABLE `users`", StringComparison.Ordinal)), "name order");
    }

    [Test]
    public async Task Dump_MapsBoolsDatetimesGuidsBlobsAndNulls()
    {
        await SeedUserAsync();
        var gid = Guid.NewGuid();
        await using (var connection = _factory.Create())
        {
            await connection.ExecuteAsync("""
                CREATE TABLE zz_backup_types (
                    id INT NOT NULL PRIMARY KEY, gid CHAR(36) NOT NULL, payload BLOB NULL, flag TINYINT(1) NOT NULL,
                    amount DECIMAL(10,2) NULL, big BIGINT NOT NULL, note VARCHAR(20) NULL
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """);
            await connection.ExecuteAsync(
                "INSERT INTO zz_backup_types VALUES (1, @gid, @payload, 0, 12.50, 9007199254740993, NULL)",
                new { gid, payload = new byte[] { 0, 1, 2, 255 } });
        }

        using var zip = await DumpAsync(Guid.NewGuid());

        var user = Rows(zip, "users").Single();
        Assert.That(user["must_change_password"]!.GetValueKind(), Is.EqualTo(JsonValueKind.True));
        Assert.That(user["is_disabled"]!.GetValueKind(), Is.EqualTo(JsonValueKind.False));
        Assert.That(user["created_at"]!.GetValue<string>(), Is.EqualTo(Created.ToString("O")));
        Assert.That(user["last_login_at"], Is.Null);
        Assert.That(user.ContainsKey("last_login_at"), Is.True);

        var row = Rows(zip, "zz_backup_types").Single();
        Assert.That(row["gid"]!.GetValue<string>(), Is.EqualTo(gid.ToString("D")));
        Assert.That(row["payload"]!.ToJsonString(), Is.EqualTo("""{"$b64":"AAEC/w=="}"""));
        Assert.That(row["flag"]!.GetValueKind(), Is.EqualTo(JsonValueKind.False));
        Assert.That(row["amount"]!.ToJsonString(), Is.EqualTo("12.50"));
        Assert.That(row["big"]!.ToJsonString(), Is.EqualTo("9007199254740993"));
        Assert.That(row["note"], Is.Null);
    }

    [Test]
    public async Task Dump_LeavesTheDestinationOpen()
    {
        var stream = new MemoryStream();
        await new MySqlBackupStore(_factory).DumpAsync(stream, Guid.NewGuid(), T0, default);
        Assert.That(stream.CanWrite, Is.True);
        Assert.That(stream.Length, Is.GreaterThan(0));
    }
}
