using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Audit;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>AU1/AU5 <c>audit_events</c> against MariaDB.</summary>
[Category("Integration")]
public class AuditRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    private MySqlConnectionFactory _factory = null!;
    private AuditRepository _audit = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(_factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _audit = new AuditRepository(_factory);
    }

    private async Task<List<AuditRepositoryRow>> Rows()
    {
        await using var connection = _factory.Create();
        return (await connection.QueryAsync<AuditRepositoryRow>("""
            SELECT occurred_at AS OccurredAt, action AS Action, system_id AS SystemId, game_id AS GameId, user_id AS UserId,
                   username AS Username, job_id AS JobId, path AS Path, target_path AS TargetPath, details AS Details
            FROM audit_events ORDER BY id
            """)).AsList();
    }

    [Test]
    public async Task Insert_StoresEveryColumn()
    {
        var game = Guid.NewGuid();
        var job = Guid.NewGuid();
        await _audit.InsertAsync(new AuditEvent(AuditActions.ReleaseMoved, new AuditActor(7, "boss", job), "gb", game,
            "roms/gb/T/Tetris.zip", "roms/gb/T/Tetris DX.zip", "Tetris (World).gb") { OccurredAt = T0.AddTicks(12_340) }, default);
        await _audit.InsertAsync(new AuditEvent(AuditActions.DatsRefreshed, new AuditActor(null, null, null)) { OccurredAt = T0 }, default);

        var rows = await Rows();

        Assert.That(rows[0], Is.EqualTo(new AuditRepositoryRow
        {
            OccurredAt = T0.AddTicks(12_340), Action = "ReleaseMoved", SystemId = "gb", GameId = game, UserId = 7, Username = "boss",
            JobId = job, Path = "roms/gb/T/Tetris.zip", TargetPath = "roms/gb/T/Tetris DX.zip", Details = "Tetris (World).gb",
        }).UsingPropertiesComparer());
        Assert.That((rows[1].Action, rows[1].SystemId, rows[1].GameId, rows[1].UserId, rows[1].Username, rows[1].JobId, rows[1].Path),
            Is.EqualTo(("DatsRefreshed", (string?)null, (Guid?)null, (int?)null, (string?)null, (Guid?)null, (string?)null)));
    }

    [Test]
    public async Task Scheduled_ListsOnlyRowsWithoutIdAndUsername()
    {
        await _audit.InsertAsync(new AuditEvent(AuditActions.DatsRefreshed, new AuditActor(null, null, null)) { OccurredAt = T0 }, default);
        await _audit.InsertAsync(new AuditEvent(AuditActions.SignInFailed, new AuditActor(null, "nobody", null)) { OccurredAt = T0 }, default);
        await _audit.InsertAsync(new AuditEvent(AuditActions.SignedIn, new AuditActor(7, "boss", null)) { OccurredAt = T0 }, default);

        var page = await _audit.ListAsync(new AuditQuery(Scheduled: true), 1, 50, default);

        Assert.That(page.Items.Select(i => i.Action), Is.EqualTo(new[] { AuditActions.DatsRefreshed }));
    }

    [Test]
    public async Task Insert_TruncatesTextOverTheColumnLength()
    {
        await _audit.InsertAsync(new AuditEvent(new string('a', 40), new AuditActor(1, new string('u', 70), null), new string('s', 20),
            Path: new string('p', 1100), TargetPath: new string('t', 1100), Details: new string('d', 1100)) { OccurredAt = T0 }, default);

        var row = (await Rows()).Single();

        Assert.That((row.Action.Length, row.SystemId!.Length, row.Username!.Length, row.Path!.Length, row.TargetPath!.Length, row.Details!.Length),
            Is.EqualTo((32, 16, 64, 1024, 1024, 1024)));
    }

    [Test]
    public async Task Purge_DeletesRowsOlderThanTheCutoff_KeepsNewer()
    {
        var actor = new AuditActor(null, null, null);
        await _audit.InsertAsync(new AuditEvent("Old", actor) { OccurredAt = T0.AddDays(-31) }, default);
        await _audit.InsertAsync(new AuditEvent("Edge", actor) { OccurredAt = T0.AddDays(-30) }, default);
        await _audit.InsertAsync(new AuditEvent("New", actor) { OccurredAt = T0.AddDays(-1) }, default);

        var deleted = await _audit.PurgeAsync(T0 - AuditLog.Retention, default);

        Assert.That(deleted, Is.EqualTo(1));
        Assert.That((await Rows()).Select(r => r.Action), Is.EqualTo(new[] { "Edge", "New" }));
    }

    private async Task<Guid> InsertGameAsync(string title)
    {
        var id = Guid.NewGuid();
        await using var connection = _factory.Create();
        await connection.ExecuteAsync("""
            INSERT INTO games (id, system_id, title, display_title, zip_path, bucket, developers, publishers, genres,
                               zip_size_bytes, zip_mtime_utc, info_schema_version, created_at, updated_at)
            VALUES (@id, 'gb', @title, @title, @zip, 'T', '[]', '[]', '[]', 1, @T0, 2, @T0, @T0)
            """, new { id, title, zip = $"gb/T/{id}.zip", T0 });
        return id;
    }

    [Test]
    public async Task List_CarriesTheGameTitle_NullWhenTheGameIsGone_AndFiltersStillApply()
    {
        var game = await InsertGameAsync("Tetris");
        var actor = new AuditActor(7, "boss", null);
        await _audit.InsertAsync(new AuditEvent(AuditActions.GameUpdated, actor, "gb", game) { OccurredAt = T0 }, default);
        await _audit.InsertAsync(new AuditEvent(AuditActions.GameDeleted, actor, "gb", Guid.NewGuid()) { OccurredAt = T0.AddMinutes(1) }, default);
        await _audit.InsertAsync(new AuditEvent(AuditActions.DatsRefreshed, actor, "nes") { OccurredAt = T0.AddMinutes(2) }, default);

        var all = await _audit.ListAsync(new AuditQuery(), 1, 50, default);
        var gb = await _audit.ListAsync(new AuditQuery(System: "gb", User: "boss"), 1, 50, default);

        Assert.That(all.Items.Select(i => (i.Action, i.GameTitle)), Is.EqualTo(new (string, string?)[]
        {
            (AuditActions.DatsRefreshed, null), (AuditActions.GameDeleted, null), (AuditActions.GameUpdated, "Tetris"),
        }));
        Assert.That(gb.Total, Is.EqualTo(2));
        Assert.That(gb.Items.Select(i => i.Action), Is.EqualTo(new[] { AuditActions.GameDeleted, AuditActions.GameUpdated }));
    }

    [Test]
    public async Task ListRecent_FiltersByAction_NewestFirstTiesById_CappedAtTheLimit_NullTitleForAGoneGame()
    {
        var game = await InsertGameAsync("Zelda");
        var actor = new AuditActor(7, "boss", null);
        await _audit.InsertAsync(new AuditEvent(AuditActions.SaveAdded, actor, "gb", game, Details: "old") { OccurredAt = T0 }, default);
        await _audit.InsertAsync(new AuditEvent(AuditActions.SaveDeleted, actor, "gb", Guid.NewGuid(), Details: "tie a") { OccurredAt = T0.AddMinutes(1) }, default);
        await _audit.InsertAsync(new AuditEvent(AuditActions.SaveUpdated, actor, "gb", game, Details: "tie b") { OccurredAt = T0.AddMinutes(1) }, default);
        await _audit.InsertAsync(new AuditEvent(AuditActions.GameUpdated, actor, "gb", game, Details: "other") { OccurredAt = T0.AddMinutes(9) }, default);

        var saves = new[] { AuditActions.SaveAdded, AuditActions.SaveUpdated, AuditActions.SaveDeleted };
        var all = await _audit.ListRecentAsync(saves, 20, default);
        var two = await _audit.ListRecentAsync(saves, 2, default);

        Assert.That(all.Select(i => (i.Details, i.GameTitle)), Is.EqualTo(new (string?, string?)[]
        {
            ("tie b", "Zelda"), ("tie a", null), ("old", "Zelda"),
        }));
        Assert.That(two.Select(i => i.Details), Is.EqualTo(new[] { "tie b", "tie a" }));
        Assert.That(all[0].User, Is.EqualTo("boss"));
    }
}
