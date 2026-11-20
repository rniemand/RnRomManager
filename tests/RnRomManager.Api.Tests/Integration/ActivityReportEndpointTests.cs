using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>UI polish B21: <c>GET /api/reports/activity</c> against MariaDB.</summary>
[Category("Integration")]
public class ActivityReportEndpointTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
    private ApiFactory _factory = null!;
    private int _seed;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        await TestUsers.CreateAsync(_factory, "site", Core.Users.Roles.None);
    }

    private async Task<AuthClient> Login(string username)
    {
        var client = await AuthClient.CreateAsync(_factory);
        (await client.LoginAsync(username, TestUsers.Password)).Dispose();
        // Drop the sign-in rows so only the fixture's audit rows are listed.
        await Service<AuditWriter>().FlushAsync();
        await Service<IAuditRepository>().PurgeAsync(DateTime.UtcNow.AddDays(1), default);
        return client;
    }

    private async Task<Guid> AddGame(string title, DateTime addedAt)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with { Metadata = new InfoJsonGameMetadata(When, When, "0.1.0") };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, $"gb/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        await using var db = Service<IDbConnectionFactory>().Create();
        await db.ExecuteAsync("UPDATE roms SET date_added = @addedAt WHERE game_id = @id", new { addedAt, id = game.GameId });
        return game.GameId;
    }

    private Task Audit(string action, Guid? game, DateTime at, string? details = null) =>
        Service<IAuditRepository>().InsertAsync(new AuditEvent(action, new AuditActor(1, "boss", null), "gb", game,
            "roms/gb/T/Gone Game.zip", Details: details) { OccurredAt = at }, default);

    private static List<string?> Details(JsonElement list) => list.EnumerateArray().Select(i => i.GetProperty("details").GetString()).ToList();

    [Test]
    public async Task Admin_GetsEveryList_NewestFirst_CappedAt20()
    {
        using var admin = await Login("boss");
        var tetris = await AddGame("Tetris", T0);
        for (var i = 0; i < 21; i++)
            await AddGame($"Game {i:00}", T0.AddMinutes(1 + i));
        for (var i = 0; i < 22; i++)
            await Audit(AuditActions.GamePlayed, tetris, T0.AddMinutes(i), $"play {i:00}");
        await Audit(AuditActions.SaveDeleted, tetris, T0, "save");
        await Audit(AuditActions.CoverSet, tetris, T0, "edit");
        await Audit(AuditActions.ReleaseEdited, tetris, T0.AddMinutes(1), "release edit");
        await Audit(AuditActions.ReleaseDeleted, Guid.NewGuid(), T0, "gone.gb");
        await Audit(AuditActions.SignInFailed, null, T0, "bad");
        await Audit(AuditActions.DatsRefreshed, null, T0, "not listed");

        using var response = await admin.GetAsync("/api/reports/activity");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);

        var last = body.GetProperty("lastSignIn");
        Assert.That(last.GetProperty("username").GetString(), Is.EqualTo("boss"));
        Assert.That(last.GetProperty("at").GetString(), Does.EndWith("Z"));

        var added = body.GetProperty("addedRoms");
        Assert.That(added.GetArrayLength(), Is.EqualTo(20));
        Assert.That(added[0].GetProperty("gameTitle").GetString(), Is.EqualTo("Game 20"));
        Assert.That(added[19].GetProperty("gameTitle").GetString(), Is.EqualTo("Game 01"));
        Assert.That(added[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[]
            { "romId", "gameId", "gameTitle", "fileName", "system", "sizeBytes", "addedAt" }));
        Assert.That((added[0].GetProperty("fileName").GetString(), added[0].GetProperty("system").GetString(),
            added[0].GetProperty("addedAt").GetString()), Is.EqualTo(("Game 20.gb", "gb", "2026-11-17T10:21:00Z")));

        var plays = body.GetProperty("plays");
        Assert.That(Details(plays), Is.EqualTo(Enumerable.Range(2, 20).Reverse().Select(i => $"play {i:00}")));
        Assert.That(plays[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[]
            { "id", "occurredAt", "action", "system", "gameId", "user", "jobId", "path", "targetPath", "details", "gameTitle" }));
        Assert.That(plays[0].GetProperty("gameTitle").GetString(), Is.EqualTo("Tetris"));
        Assert.That(plays[0].GetProperty("occurredAt").GetString(), Does.EndWith("Z"));

        Assert.That(Details(body.GetProperty("saves")), Is.EqualTo(new[] { "save" }));
        Assert.That(Details(body.GetProperty("edits")), Is.EqualTo(new[] { "release edit", "edit" }));
        Assert.That(Details(body.GetProperty("signIns")), Is.EqualTo(new[] { "bad" }));
        var deletion = body.GetProperty("deletions").EnumerateArray().Single();
        Assert.That(deletion.GetProperty("gameTitle").ValueKind, Is.EqualTo(JsonValueKind.Null), "the game is gone");
        Assert.That(deletion.GetProperty("path").GetString(), Is.EqualTo("roms/gb/T/Gone Game.zip"));
    }

    [Test]
    public async Task EmptyLibrary_GivesEmptyLists()
    {
        using var admin = await Login("boss");
        using var response = await admin.GetAsync("/api/reports/activity");
        var body = await AuthClient.JsonAsync(response);
        foreach (var list in new[] { "addedRoms", "signIns", "plays", "saves", "edits", "deletions" })
            Assert.That(body.GetProperty(list).GetArrayLength(), Is.Zero, list);
    }

    [Test]
    public async Task SiteUser_Is403()
    {
        using var site = await Login("site");
        using var response = await site.GetAsync("/api/reports/activity");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
