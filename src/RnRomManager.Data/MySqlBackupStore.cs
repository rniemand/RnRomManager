using System.Data;
using System.Globalization;
using System.Reflection;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MySqlConnector;
using RnRomManager.Core;
using RnRomManager.Core.Backups;

namespace RnRomManager.Data;

/// <summary>
/// Backup ZIPs written in-process (B13): the chiseled image has no <c>mariadb-dump</c>. Every table is read inside one
/// <c>START TRANSACTION WITH CONSISTENT SNAPSHOT</c>, streamed row by row.
/// </summary>
public sealed class MySqlBackupStore(MySqlConnectionFactory connections) : IBackupStore
{
    /// <summary>Wraps a binary column's value: <c>{ "$b64": "…" }</c>.</summary>
    public const string Base64Key = "$b64";

    /// <summary>Rows per multi-row <c>INSERT</c> on restore (B19).</summary>
    public const int InsertBatchSize = 500;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Where the migration scripts are embedded (the same scripts <see cref="MySqlDatabaseLifecycle"/> runs).</summary>
    public Assembly ScriptAssembly { get; init; } = typeof(MySqlDatabaseLifecycle).Assembly;

    public IReadOnlySet<string> KnownMigrations => ScriptAssembly.GetManifestResourceNames()
        .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);

    public async Task<BackupManifest> DumpAsync(
        Stream destination, Guid backupId, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var connection = (MySqlConnection)connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ", cancellationToken);
        await ExecuteAsync(connection, "START TRANSACTION WITH CONSISTENT SNAPSHOT", cancellationToken);
        try
        {
            var tables = await TablesAsync(connection, cancellationToken);
            var migrations = await MigrationsAsync(connection, tables, cancellationToken);

            using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            await WriteSchemaAsync(zip, connection, tables, cancellationToken);

            var counts = new List<BackupTable>(tables.Count);
            foreach (var table in tables)
                counts.Add(new BackupTable(table, await WriteRowsAsync(zip, connection, table, cancellationToken)));

            var manifest = new BackupManifest(BackupManifest.CurrentFormatVersion, backupId, createdAt.ToUniversalTime(),
                AppInfo.Version, connections.Database, migrations, counts);
            await WriteManifestAsync(zip, manifest, cancellationToken);
            return manifest;
        }
        finally
        {
            // Read-only: nothing to commit, but end the snapshot before the connection goes back to the pool.
            await ExecuteAsync(connection, "ROLLBACK", CancellationToken.None);
        }
    }

    /// <summary>Base tables of the configured database, ordinal name order.</summary>
    internal static async Task<List<string>> TablesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'
            """;
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                tables.Add(reader.GetString(0));
        }

        tables.Sort(StringComparer.Ordinal);
        return tables;
    }

    private static async Task<List<string>> MigrationsAsync(
        MySqlConnection connection, List<string> tables, CancellationToken cancellationToken)
    {
        if (!tables.Contains(MySqlDatabaseLifecycle.JournalTable, StringComparer.Ordinal)) return [];
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT scriptname FROM {Quote(MySqlDatabaseLifecycle.JournalTable)} ORDER BY schemaversionid";
        var scripts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            scripts.Add(reader.GetString(0));
        return scripts;
    }

    private static async Task WriteSchemaAsync(
        ZipArchive zip, MySqlConnection connection, List<string> tables, CancellationToken cancellationToken)
    {
        await using var entry = await zip.CreateEntry(BackupManifest.SchemaFileName, CompressionLevel.Optimal).OpenAsync(cancellationToken);
        await using var writer = new StreamWriter(entry, Utf8);
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SHOW CREATE TABLE {Quote(table)}";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            await writer.WriteAsync(reader.GetString(1));
            await writer.WriteAsync(";\n\n");
        }
    }

    private static async Task<long> WriteRowsAsync(
        ZipArchive zip, MySqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var entry = await zip.CreateEntry(BackupManifest.DataEntry(table), CompressionLevel.Optimal).OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(table)}";
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var newline = "\n"u8.ToArray();
        await using var json = new Utf8JsonWriter(entry);
        long rows = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            json.WriteStartObject();
            for (var i = 0; i < names.Length; i++)
            {
                json.WritePropertyName(names[i]);
                WriteValue(json, await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i));
            }

            json.WriteEndObject();
            await json.FlushAsync(cancellationToken);
            await entry.WriteAsync(newline, cancellationToken);
            json.Reset();
            rows++;
        }

        return rows;
    }

    /// <summary>B13's value mapping; restore converts back by the target column's type.</summary>
    internal static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null or DBNull: json.WriteNullValue(); break;
            case bool b: json.WriteBooleanValue(b); break;
            case sbyte n: json.WriteNumberValue(n); break;
            case byte n: json.WriteNumberValue(n); break;
            case short n: json.WriteNumberValue(n); break;
            case ushort n: json.WriteNumberValue(n); break;
            case int n: json.WriteNumberValue(n); break;
            case uint n: json.WriteNumberValue(n); break;
            case long n: json.WriteNumberValue(n); break;
            case ulong n: json.WriteNumberValue(n); break;
            case float n: json.WriteNumberValue(n); break;
            case double n: json.WriteNumberValue(n); break;
            case decimal n: json.WriteNumberValue(n); break;
            case string s: json.WriteStringValue(s); break;
            case Guid g: json.WriteStringValue(g.ToString("D")); break;
            case DateTime d:
                json.WriteStringValue(DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset d: json.WriteStringValue(d.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeSpan t: json.WriteStringValue(t.ToString("c", CultureInfo.InvariantCulture)); break;
            case byte[] bytes:
                json.WriteStartObject();
                json.WriteBase64String(Base64Key, bytes);
                json.WriteEndObject();
                break;
            default: json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static async Task WriteManifestAsync(ZipArchive zip, BackupManifest manifest, CancellationToken cancellationToken)
    {
        await using var entry = await zip.CreateEntry(BackupManifest.FileName, CompressionLevel.Optimal).OpenAsync(cancellationToken);
        await using var json = new Utf8JsonWriter(entry, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("formatVersion", manifest.FormatVersion);
        json.WriteString("backupId", manifest.BackupId.ToString("D"));
        json.WriteString("createdAt", manifest.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        json.WriteString("appVersion", manifest.AppVersion);
        json.WriteString("database", manifest.Database);
        json.WriteStartArray("migrations");
        foreach (var script in manifest.Migrations) json.WriteStringValue(script);
        json.WriteEndArray();
        json.WriteStartArray("tables");
        foreach (var table in manifest.Tables)
        {
            json.WriteStartObject();
            json.WriteString("name", table.Name);
            json.WriteNumber("rows", table.Rows);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    public async Task RestoreAsync(ZipArchive backup, BackupManifest manifest, CancellationToken cancellationToken)
    {
        var schemaEntry = backup.GetEntry(BackupManifest.SchemaFileName)
            ?? throw new BackupRestoreException($"The backup has no {BackupManifest.SchemaFileName}.");
        foreach (var table in manifest.Tables)
        {
            if (backup.GetEntry(BackupManifest.DataEntry(table.Name)) is null)
                throw new BackupRestoreException($"The backup has no {BackupManifest.DataEntry(table.Name)}.");
        }

        string schema;
        await using (var stream = await schemaEntry.OpenAsync(cancellationToken))
        using (var reader = new StreamReader(stream, Utf8))
            schema = await reader.ReadToEndAsync(cancellationToken);

        await using var connection = (MySqlConnection)connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "SET FOREIGN_KEY_CHECKS = 0", cancellationToken);
        try
        {
            // Drop everything first, so a failed restore can simply be retried.
            var existing = await TablesAsync(connection, cancellationToken);
            if (existing.Count > 0)
                await ExecuteAsync(connection, "DROP TABLE " + string.Join(", ", existing.Select(Quote)), cancellationToken);

            // SHOW CREATE TABLE output, one statement per table; MySqlConnector runs them as one batch.
            if (!string.IsNullOrWhiteSpace(schema))
                await ExecuteAsync(connection, schema, cancellationToken);

            foreach (var table in manifest.Tables)
            {
                var entry = backup.GetEntry(BackupManifest.DataEntry(table.Name))!;
                var rows = await InsertRowsAsync(connection, entry, table.Name, cancellationToken);
                if (rows != table.Rows)
                    throw new BackupRestoreException(
                        $"Table {table.Name}: restored {rows} rows but the manifest lists {table.Rows}.");
            }
        }
        finally
        {
            await ExecuteAsync(connection, "SET FOREIGN_KEY_CHECKS = 1", CancellationToken.None);
        }
    }

    private static async Task<long> InsertRowsAsync(
        MySqlConnection connection, ZipArchiveEntry entry, string table, CancellationToken cancellationToken)
    {
        var types = await ColumnTypesAsync(connection, table, cancellationToken);
        var batch = new List<JsonElement>(InsertBatchSize);
        long rows = 0;
        await using var stream = await entry.OpenAsync(cancellationToken);
        using var reader = new StreamReader(stream, Utf8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0) continue;
            batch.Add(JsonSerializer.Deserialize<JsonElement>(line));
            if (batch.Count == InsertBatchSize)
            {
                rows += await InsertBatchAsync(connection, table, types, batch, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            rows += await InsertBatchAsync(connection, table, types, batch, cancellationToken);
        return rows;
    }

    /// <summary>Column name → <c>DATA_TYPE</c> (lower case) of the restored table.</summary>
    private static async Task<Dictionary<string, string>> ColumnTypesAsync(
        MySqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, data_type FROM information_schema.columns
            WHERE table_schema = DATABASE() AND table_name = @table
            """;
        command.Parameters.AddWithValue("@table", table);
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                types[reader.GetString(0)] = reader.GetString(1).ToLowerInvariant();
        }

        if (types.Count == 0)
            throw new BackupRestoreException($"Table {table} is in the manifest but not in {BackupManifest.SchemaFileName}.");
        return types;
    }

    private static async Task<int> InsertBatchAsync(MySqlConnection connection, string table,
        Dictionary<string, string> types, List<JsonElement> rows, CancellationToken cancellationToken)
    {
        // Columns of the first row; every row of a dump has the same keys.
        var columns = rows[0].EnumerateObject().Select(p => p.Name).ToList();
        var unknown = columns.FirstOrDefault(c => !types.ContainsKey(c));
        if (unknown is not null)
            throw new BackupRestoreException($"Table {table} has no column {unknown}.");

        await using var command = connection.CreateCommand();
        var sql = new StringBuilder("INSERT INTO ").Append(Quote(table)).Append(" (")
            .AppendJoin(", ", columns.Select(Quote)).Append(") VALUES ");
        for (var r = 0; r < rows.Count; r++)
        {
            sql.Append(r == 0 ? "(" : ", (");
            for (var c = 0; c < columns.Count; c++)
            {
                var parameter = $"@p{r}_{c}";
                sql.Append(c == 0 ? parameter : ", " + parameter);
                var value = rows[r].TryGetProperty(columns[c], out var element) ? element : default;
                command.Parameters.AddWithValue(parameter, ReadValue(value, types[columns[c]]));
            }

            sql.Append(')');
        }

        command.CommandText = sql.ToString();
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>B13's JSON value back to a parameter value for a column of <paramref name="dataType"/>.</summary>
    internal static object ReadValue(JsonElement value, string dataType)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return DBNull.Value;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Object when value.TryGetProperty(Base64Key, out var b64):
                return b64.GetBytesFromBase64();
            case JsonValueKind.Number:
                if (dataType is "decimal" or "numeric") return value.GetDecimal();
                if (dataType is "float" or "double" or "real") return value.GetDouble();
                if (value.TryGetInt64(out var signed)) return signed;
                if (value.TryGetUInt64(out var unsigned)) return unsigned;
                return value.GetDecimal();
            case JsonValueKind.String:
                var text = value.GetString()!;
                return dataType is "datetime" or "timestamp" or "date"
                    ? DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                    : text;
            default:
                throw new BackupRestoreException($"Unexpected JSON value {value.GetRawText()} for a {dataType} column.");
        }
    }

    internal static string Quote(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
