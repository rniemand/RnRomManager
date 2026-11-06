using RnRomManager.TestData;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace RnRomManager.Data.Tests.Database;

/// <summary>The real migrations (spec §14.1, D41) against MariaDB 11.4, checked through information_schema.</summary>
[Category("Integration")]
public class SchemaMigrationTests
{
    private static readonly string[] Tables =
    [
        "data_protection_keys", "games", "images", "ingest_review_items", "integrity_issues",
        "job_items", "jobs", "roms", "user_roles", "users",
    ];

    private string _cs = null!;

    [OneTimeSetUp]
    public async Task MigrateOnce()
    {
        _cs = await MariaDbServer.CreateDatabaseAsync();
        Lifecycle(_cs).Migrate();
    }

    private static MySqlDatabaseLifecycle Lifecycle(string cs) =>
        new(new MySqlConnectionFactory(cs), NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System);

    [Test]
    public async Task FromScratch_CreatesEveryTableAndJournalsElevenScripts()
    {
        var tables = await ColumnAsync<string>(_cs,
            "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME");
        var scripts = await ColumnAsync<string>(_cs, "SELECT ScriptName FROM schema_versions ORDER BY ScriptName");

        Assert.That(tables, Is.EquivalentTo(Tables.Append("schema_versions")));
        Assert.That(scripts.Select(s => s.Split('.')[^2]), Is.EqualTo(new[]
        {
            "0001_users", "0002_games", "0003_roms", "0004_images", "0005_jobs", "0006_job_items",
            "0007_ingest_review_items", "0008_integrity_issues", "0009_data_protection_keys",
            "0010_game_rating", "0011_user_roles",
        }));
    }

    [Test]
    public async Task ReRun_IsANoOp()
    {
        Lifecycle(_cs).Migrate();

        Assert.That(await ColumnAsync<long>(_cs, "SELECT COUNT(*) FROM schema_versions"), Is.EqualTo(new[] { 11L }));
    }

    [Test]
    public async Task EveryTable_IsInnoDbUtf8mb4UnicodeCi()
    {
        var rows = await ColumnAsync<string>(_cs,
            "SELECT CONCAT(TABLE_NAME, ' ', ENGINE, ' ', TABLE_COLLATION) FROM information_schema.TABLES " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> 'schema_versions' ORDER BY TABLE_NAME");

        Assert.That(rows, Is.EquivalentTo(Tables.Select(t => $"{t} InnoDB utf8mb4_unicode_ci")));
    }

    // One line per table: column type [NULL] [=default], in declaration order. Reviewed against spec §14.1.
    // MariaDB reports JSON as longtext (with a json_valid check, tested below).
    [TestCase("users", "id int(11), username varchar(64), password_hash varchar(255), is_disabled tinyint(1) =0, must_change_password tinyint(1) =0, failed_login_count int(11), lockout_until datetime(6) NULL, security_stamp char(32), created_at datetime(6), updated_at datetime(6), last_login_at datetime(6) NULL")]
    [TestCase("user_roles", "user_id int(11), role varchar(32)")]
    [TestCase("games", "id char(36), system_id varchar(16), title varchar(255), display_title varchar(255), zip_path varchar(512), bucket char(1), release_year smallint(6) NULL, developers longtext, publishers longtext, genres longtext, description text NULL, has_default_boxart tinyint(1) =0, zip_size_bytes bigint(20), zip_mtime_utc datetime, info_schema_version int(11), created_at datetime(6), updated_at datetime(6), rating tinyint(4) NULL, favourite tinyint(1) =0")]
    [TestCase("roms", "id bigint(20), game_id char(36), system_id varchar(16), file_name varchar(255), original_file_name varchar(255), base_title varchar(255), regions longtext, languages longtext, flags longtext, region_key varchar(255), revision varchar(32) NULL, is_primary tinyint(1) =0, id_method varchar(16), dat_name varchar(255) NULL, dat_version varchar(255) NULL, dat_game_name varchar(255) NULL, size_bytes bigint(20), crc32 char(8), md5 char(32), sha1 char(40), header longtext NULL, date_added datetime(6), date_modified datetime(6)")]
    [TestCase("images", "id bigint(20), game_id char(36), file_name varchar(255), type varchar(16), source varchar(32), source_url varchar(1024) NULL, is_default tinyint(1) =0, size_bytes bigint(20), sha1 char(40), width int(11), height int(11), date_added datetime(6)")]
    [TestCase("jobs", "id char(36), type varchar(16), mode varchar(16) NULL, trigger_source varchar(16), status varchar(16), requested_by int(11) NULL, started_at datetime(6), finished_at datetime(6) NULL, summary longtext NULL, error text NULL")]
    [TestCase("job_items", "id bigint(20), job_id char(36), file_id char(36), system_id varchar(16) NULL, source_path varchar(1024), archive_entry varchar(512) NULL, outcome varchar(16), reason varchar(64) NULL, details text NULL, game_id char(36) NULL, sha1 char(40) NULL, created_at datetime(6)")]
    [TestCase("ingest_review_items", "id bigint(20), system_id varchar(16), reason varchar(16), source_path varchar(1024), archive_path varchar(1024) NULL, archive_entry varchar(512) NULL, original_file_name varchar(255), size_bytes bigint(20), crc32 char(8), md5 char(32), sha1 char(40), header longtext NULL, suggestions longtext, dat_identification longtext NULL, candidate_game_ids longtext NULL, status varchar(16), game_id char(36) NULL, created_at datetime(6), last_seen_job_id char(36), resolved_at datetime(6) NULL, resolved_by int(11) NULL")]
    [TestCase("integrity_issues", "id bigint(20), type varchar(32), severity varchar(8), system_id varchar(16) NULL, zip_path varchar(512) NULL, game_id char(36) NULL, entry_name varchar(512) NULL, details text NULL, status varchar(16), first_seen_job_id char(36), last_seen_job_id char(36), detected_at datetime(6), resolved_at datetime(6) NULL")]
    [TestCase("data_protection_keys", "id int(11), friendly_name varchar(255), xml text, created_at datetime(6)")]
    public async Task Columns_MatchSpec(string table, string expected)
    {
        var columns = await ColumnAsync<string>(_cs,
            "SELECT CONCAT(COLUMN_NAME, ' ', COLUMN_TYPE, IF(IS_NULLABLE = 'YES', ' NULL', ''), " +
            "IF(COLUMN_DEFAULT IS NOT NULL AND COLUMN_DEFAULT <> 'NULL', CONCAT(' =', COLUMN_DEFAULT), '')) " +
            $"FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' ORDER BY ORDINAL_POSITION");

        Assert.That(string.Join(", ", columns), Is.EqualTo(expected));
    }

    [Test]
    public async Task IndexesAndUniqueKeys_MatchSpec()
    {
        // "table index unique? columns"; fk_* indexes are the ones InnoDB adds for FKs with no covering index.
        var expected = new[]
        {
            "data_protection_keys PRIMARY U id",
            "games ix_games_created_at - created_at",
            "games ix_games_system_bucket_title - system_id,bucket,title",
            "games ix_games_title - title",
            "games PRIMARY U id",
            "games ux_games_zip_path U zip_path",
            "images PRIMARY U id",
            "images ux_images_game_file U game_id,file_name",
            "ingest_review_items fk_ingest_review_items_resolved_by - resolved_by",
            "ingest_review_items ix_ingest_review_items_status_created_at - status,created_at",
            "ingest_review_items PRIMARY U id",
            "ingest_review_items ux_ingest_review_items_sha1 U sha1",
            "integrity_issues ix_integrity_issues_status_type - status,type",
            "integrity_issues ix_integrity_issues_zip_path - zip_path",
            "integrity_issues PRIMARY U id",
            "job_items ix_job_items_job_outcome - job_id,outcome",
            "job_items PRIMARY U id",
            "jobs fk_jobs_requested_by - requested_by",
            "jobs ix_jobs_type_started_at - type,started_at",
            "jobs PRIMARY U id",
            "roms ix_roms_crc32 - crc32",
            "roms ix_roms_game_id - game_id",
            "roms ix_roms_system_base_title - system_id,base_title",
            "roms PRIMARY U id",
            "roms ux_roms_sha1 U sha1",
            "user_roles PRIMARY U user_id,role",
            "users PRIMARY U id",
            "users ux_users_username U username",
        };

        var actual = await ColumnAsync<string>(_cs,
            "SELECT CONCAT(TABLE_NAME, ' ', INDEX_NAME, ' ', IF(NON_UNIQUE = 0, 'U', '-'), ' ', " +
            "GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX)) FROM information_schema.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> 'schema_versions' " +
            "GROUP BY TABLE_NAME, INDEX_NAME, NON_UNIQUE ORDER BY TABLE_NAME, INDEX_NAME");

        Assert.That(actual, Is.EquivalentTo(expected));
    }

    [Test]
    public async Task ForeignKeys_MatchSpec()
    {
        var actual = await ColumnAsync<string>(_cs,
            "SELECT CONCAT(TABLE_NAME, ' -> ', REFERENCED_TABLE_NAME, ' ', DELETE_RULE) " +
            "FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() ORDER BY TABLE_NAME");

        Assert.That(actual, Is.EquivalentTo(new[]
        {
            "images -> games CASCADE",
            "ingest_review_items -> users RESTRICT",
            "job_items -> jobs CASCADE",
            "jobs -> users RESTRICT",
            "roms -> games CASCADE",
            "user_roles -> users CASCADE",
        }));
    }

    [Test]
    public async Task Username_IsUniqueIgnoringCase()
    {
        var cs = await FreshSchemaAsync();
        await InsertUserAsync(cs, "Admin");

        var ex = await Assert.ThrowsAsync<MySqlException>(() => InsertUserAsync(cs, "aDMIN"));

        Assert.That(ex!.ErrorCode, Is.EqualTo(MySqlErrorCode.DuplicateKeyEntry));
    }

    [Test]
    public async Task ZipPath_IsUniqueIgnoringCase()
    {
        var cs = await FreshSchemaAsync();
        await InsertGameAsync(cs, "gb/T/Tetris.zip");

        var ex = await Assert.ThrowsAsync<MySqlException>(() => InsertGameAsync(cs, "GB/t/TETRIS.zip"));

        Assert.That(ex!.ErrorCode, Is.EqualTo(MySqlErrorCode.DuplicateKeyEntry));
    }

    [Test]
    public async Task ZipPath_IsAccentSensitive_D41()
    {
        var cs = await FreshSchemaAsync();
        await InsertGameAsync(cs, "gb/C/Café.zip");

        await Assert.DoesNotThrowAsync(() => InsertGameAsync(cs, "gb/C/Cafe.zip"));
    }

    [Test]
    public async Task DeletingAGame_CascadesToRomsAndImages()
    {
        var cs = await FreshSchemaAsync();
        var gameId = await InsertGameAsync(cs, "gb/T/Tetris.zip");
        await ExecuteAsync(cs,
            "INSERT INTO roms (game_id, system_id, file_name, original_file_name, base_title, regions, languages, flags, " +
            "region_key, id_method, size_bytes, crc32, md5, sha1, date_added, date_modified) VALUES " +
            "(@g, 'gb', 'Tetris.gb', 'Tetris.gb', 'Tetris', '[]', '[]', '[]', '|World|', 'Dat', 32768, " +
            "'00000000', REPEAT('0', 32), REPEAT('0', 40), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))", ("@g", gameId));
        await ExecuteAsync(cs,
            "INSERT INTO images (game_id, file_name, type, source, size_bytes, sha1, width, height, date_added) VALUES " +
            "(@g, 'boxart.png', 'BoxArt', 'Default', 1, REPEAT('0', 40), 512, 512, UTC_TIMESTAMP(6))", ("@g", gameId));

        await ExecuteAsync(cs, "DELETE FROM games WHERE id = @g", ("@g", gameId));

        Assert.That(await ColumnAsync<long>(cs, "SELECT (SELECT COUNT(*) FROM roms) + (SELECT COUNT(*) FROM images)"), Is.EqualTo(new[] { 0L }));
    }

    [Test]
    public async Task JsonColumns_RejectInvalidJson()
    {
        var cs = await FreshSchemaAsync();

        var ex = await Assert.ThrowsAsync<MySqlException>(() => InsertGameAsync(cs, "gb/T/Tetris.zip", developers: "not json"));

        Assert.That(ex!.Number, Is.EqualTo(4025)); // MariaDB ER_CONSTRAINT_FAILED (json_valid check); no MySqlErrorCode member
    }

    [Test]
    public async Task UserRoles_BackfillAdminToBothRoles_UserToNone_AndRotateEveryStamp()
    {
        var cs = await MariaDbServer.CreateDatabaseAsync();
        foreach (var script in MigrationScripts().TakeWhile(s => !s.Name.Contains("0011_", StringComparison.Ordinal)))
            await ExecuteAsync(cs, script.Sql);
        foreach (var (name, role) in new[] { ("boss", "Admin"), ("plain", "User") })
            await ExecuteAsync(cs,
                "INSERT INTO users (username, password_hash, role, failed_login_count, security_stamp, created_at, updated_at) " +
                "VALUES (@u, 'hash', @r, 0, REPEAT('a', 32), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))", ("@u", name), ("@r", role));

        await ExecuteAsync(cs, MigrationScripts().Single(s => s.Name.Contains("0011_", StringComparison.Ordinal)).Sql);

        Assert.That(await ColumnAsync<string>(cs,
                "SELECT CONCAT(u.username, ' ', r.role) FROM user_roles r JOIN users u ON u.id = r.user_id ORDER BY u.username, r.role"),
            Is.EqualTo(new[] { "boss admin", "boss rom-manager" }));
        var stamps = await ColumnAsync<string>(cs, "SELECT security_stamp FROM users");
        Assert.That(stamps, Has.All.Not.EqualTo(new string('a', 32)).And.All.Length.EqualTo(32));
        Assert.That(stamps.Distinct().Count(), Is.EqualTo(2));
        Assert.That(await ColumnAsync<long>(cs,
            "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'users' AND COLUMN_NAME = 'role'"),
            Is.EqualTo(new[] { 0L }));
    }

    [Test]
    public async Task DeletingAUser_CascadesToTheirRoles()
    {
        var cs = await FreshSchemaAsync();
        await InsertUserAsync(cs, "boss");
        await ExecuteAsync(cs, "INSERT INTO user_roles (user_id, role) SELECT id, 'admin' FROM users");

        await ExecuteAsync(cs, "DELETE FROM users");

        Assert.That(await ColumnAsync<long>(cs, "SELECT COUNT(*) FROM user_roles"), Is.EqualTo(new[] { 0L }));
    }

    /// <summary>The embedded migration scripts in run order, for tests that stop part-way.</summary>
    private static IEnumerable<(string Name, string Sql)> MigrationScripts()
    {
        var assembly = typeof(MySqlDatabaseLifecycle).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            yield return (name, reader.ReadToEnd());
        }
    }

    /// <summary>Tests that write rows get their own database so they can't affect the shared schema checks.</summary>
    private static async Task<string> FreshSchemaAsync()
    {
        var cs = await MariaDbServer.CreateDatabaseAsync();
        Lifecycle(cs).Migrate();
        return cs;
    }

    private static Task InsertUserAsync(string cs, string username) => ExecuteAsync(cs,
        "INSERT INTO users (username, password_hash, failed_login_count, security_stamp, created_at, updated_at) " +
        "VALUES (@u, 'hash', 0, REPEAT('a', 32), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))", ("@u", username));

    private static async Task<string> InsertGameAsync(string cs, string zipPath, string developers = "[]")
    {
        var id = Guid.NewGuid().ToString();
        await ExecuteAsync(cs,
            "INSERT INTO games (id, system_id, title, display_title, zip_path, bucket, developers, publishers, genres, " +
            "zip_size_bytes, zip_mtime_utc, info_schema_version, created_at, updated_at) VALUES " +
            "(@id, 'gb', 'T', 'T', @p, 'T', @d, '[]', '[]', 1, UTC_TIMESTAMP(), 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))",
            ("@id", id), ("@p", zipPath), ("@d", developers));
        return id;
    }

    private static async Task ExecuteAsync(string cs, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<T>> ColumnAsync<T>(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<T>();
        while (await reader.ReadAsync())
            values.Add(reader.GetFieldValue<T>(0));
        return values;
    }
}
