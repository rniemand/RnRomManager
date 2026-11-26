using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Database;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>User Management S7: <c>saveCount</c> is the viewer's own for a Site user, the game's total for an Admin.</summary>
public partial class LibraryEndpointsTests
{
    /// <summary>Total 3 saves: 2 by <c>plain</c>, 1 by <c>other</c>.</summary>
    private async Task SeedSaveCountsAsync(Guid id)
    {
        await using var connection = Service<IDbConnectionFactory>().Create();
        await connection.ExecuteAsync("""
            UPDATE games SET save_count = 3 WHERE id = @id;
            INSERT INTO game_user_save_counts (game_id, username, save_count) VALUES (@id, 'plain', 2), (@id, 'other', 1);
            """, new { id });
    }

    private async Task<(int List, int Detail)> SaveCountsAsync(AuthClient client, Guid id, string listUrl = "/api/games")
    {
        using var list = await client.GetAsync(listUrl);
        using var detail = await client.GetAsync($"/api/games/{id}");
        Assert.That((list.StatusCode, detail.StatusCode), Is.EqualTo((HttpStatusCode.OK, HttpStatusCode.OK)));
        var item = (await AuthClient.JsonAsync(list)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
        return (item.GetProperty("saveCount").GetInt32(), (await AuthClient.JsonAsync(detail)).GetProperty("saveCount").GetInt32());
    }

    [Test]
    public async Task SaveCount_SiteUserSeesTheirOwn_AnotherSiteUserZero_AdminTheTotal()
    {
        var id = await Tetris();
        await SeedSaveCountsAsync(id);
        using var stranger = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "stranger");
        (await stranger.LoginAsync("stranger", TestUsers.Password)).Dispose();
        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();

        Assert.That(await SaveCountsAsync(_user, id), Is.EqualTo((2, 2)));
        Assert.That(await SaveCountsAsync(stranger, id), Is.EqualTo((0, 0)));
        Assert.That(await SaveCountsAsync(admin, id), Is.EqualTo((3, 3)));
    }

    [Test]
    public async Task OwnSaveCount_IsTheViewersOwn_ForAdminsToo()
    {
        var id = await Tetris();
        await SeedSaveCountsAsync(id);
        await using (var connection = Service<IDbConnectionFactory>().Create())
            await connection.ExecuteAsync("INSERT INTO game_user_save_counts (game_id, username, save_count) VALUES (@id, 'boss', 1)", new { id });
        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        using var stranger = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "stranger");
        (await stranger.LoginAsync("stranger", TestUsers.Password)).Dispose();

        async Task<(int Saves, int Own)> Counts(AuthClient client)
        {
            using var list = await client.GetAsync("/api/games");
            var item = (await AuthClient.JsonAsync(list)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
            return (item.GetProperty("saveCount").GetInt32(), item.GetProperty("ownSaveCount").GetInt32());
        }

        Assert.That(await Counts(admin), Is.EqualTo((3, 1)), "the total stays the ZIP's count");
        Assert.That(await Counts(_user), Is.EqualTo((2, 2)));
        Assert.That(await Counts(stranger), Is.EqualTo((0, 0)));
    }

    [Test]
    public async Task SaveCount_InACollectionList_IsTheSiteUsersOwn()
    {
        var id = await Tetris();
        await SeedSaveCountsAsync(id);
        using var create = await _user.PostAsync("/api/collections", new { name = "Mine" });
        var collection = (await AuthClient.JsonAsync(create)).GetProperty("id").GetGuid();
        using var add = await _user.PostAsync($"/api/collections/{collection}/games", new { gameIds = new[] { id } });
        Assert.That(add.IsSuccessStatusCode, Is.True);

        Assert.That((await SaveCountsAsync(_user, id, $"/api/games?collection={collection}")).List, Is.EqualTo(2));
    }
}
