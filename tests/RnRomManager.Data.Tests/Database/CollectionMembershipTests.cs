using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Library;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>B13–B18 <c>collection_games</c> and the <c>/api/games</c> collection filter, against MariaDB.</summary>
[Category("Integration")]
public class CollectionMembershipTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    private string _cs = null!;
    private ILibraryMirror _mirror = null!;
    private GameRepository _games = null!;
    private CollectionRepository _collections = null!;
    private int _alice;
    private int _bob;
    private int _seed;

    [SetUp]
    public async Task SetUp()
    {
        _cs = await MariaDbServer.CreateDatabaseAsync();
        var factory = new MySqlConnectionFactory(_cs);
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _mirror = new LibraryMirror(factory);
        _games = new GameRepository(factory);
        _collections = new CollectionRepository(factory);
        await using var connection = new MySqlConnection(_cs);
        const string insertUser =
            "INSERT INTO users (username, password_hash, failed_login_count, security_stamp, created_at, updated_at) " +
            "VALUES (@username, 'hash', 0, REPEAT('a', 32), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)); SELECT LAST_INSERT_ID();";
        _alice = await connection.ExecuteScalarAsync<int>(insertUser, new { username = "alice" });
        _bob = await connection.ExecuteScalarAsync<int>(insertUser, new { username = "bob" });
    }

    private async Task<InfoJsonGame> AddGameAsync(string title, int addedMinutes = 0)
    {
        var created = When.AddMinutes(addedMinutes);
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with { Metadata = new InfoJsonGameMetadata(created, created, "0.1.0") };
        await ReplaceAsync(game);
        return game;
    }

    private Task ReplaceAsync(InfoJsonGame game) =>
        _mirror.ReplaceAsync(new MirrorEntry(game, $"gb/{game.Title[0]}/{game.Title}.zip", 100, When.UtcDateTime), default);

    private async Task<Guid> NewCollectionAsync(string name = "Mine", int? userId = null)
    {
        var collection = new Collection(Guid.NewGuid(), userId ?? _alice, name, null, null, null, T0, T0);
        await _collections.InsertAsync(collection, default);
        return collection.Id;
    }

    private async Task<long> CountAsync(Guid collectionId) => (await _collections.GetAsync(collectionId, _alice, default))!.GameCount;

    private async Task<IReadOnlyList<string>> TitlesAsync(GameQuery query) =>
        (await _games.ListAsync(query, default)).Items.Select(i => i.DisplayTitle).ToList();

    private async Task<DateTime[]> AddedAtAsync(Guid collectionId, Guid gameId)
    {
        await using var connection = new MySqlConnection(_cs);
        return (await connection.QueryAsync<DateTime>(
            "SELECT added_at FROM collection_games WHERE collection_id = @collectionId AND game_id = @gameId",
            new { collectionId, gameId })).ToArray();
    }

    [Test]
    public async Task Add_IsIdempotent_SkipsUnknownIds_AndReturnsTheNewCount()
    {
        var tetris = await AddGameAsync("Tetris");
        var zelda = await AddGameAsync("Zelda");
        var id = await NewCollectionAsync();

        Assert.That(await _collections.AddGamesAsync(id, [tetris.GameId, Guid.NewGuid()], T0, default), Is.EqualTo(1));
        Assert.That(await _collections.AddGamesAsync(id, [tetris.GameId, zelda.GameId], T0, default), Is.EqualTo(1));
        Assert.That(await _collections.AddGamesAsync(id, [tetris.GameId, zelda.GameId], T0, default), Is.EqualTo(0));
        Assert.That(await _collections.AddGamesAsync(id, [], T0, default), Is.EqualTo(0));
        Assert.That(await CountAsync(id), Is.EqualTo(2));
    }

    [Test]
    public async Task Remove_ReportsWhetherTheGameWasAMember()
    {
        var tetris = await AddGameAsync("Tetris");
        var id = await NewCollectionAsync();
        await _collections.AddGamesAsync(id, [tetris.GameId], T0, default);

        Assert.That(await _collections.RemoveGameAsync(id, tetris.GameId, default), Is.True);
        Assert.That(await _collections.RemoveGameAsync(id, tetris.GameId, default), Is.False);
        Assert.That(await CountAsync(id), Is.Zero);
    }

    [Test]
    public async Task Memberships_SurviveAReApply_AndADeletedGameReappearsWhenItComesBack_B17()
    {
        var tetris = await AddGameAsync("Tetris");
        var id = await NewCollectionAsync();
        await _collections.AddGamesAsync(id, [tetris.GameId], T0, default);

        await ReplaceAsync(tetris with { Rating = 5 });
        Assert.That(await CountAsync(id), Is.EqualTo(1), "re-applied through ApplyAsync");

        await _mirror.DeleteAsync(tetris.GameId, default);
        Assert.That(await CountAsync(id), Is.Zero, "the orphan isn't counted");
        Assert.That(await TitlesAsync(new GameQuery { CollectionId = id }), Is.Empty);
        Assert.That(await AddedAtAsync(id, tetris.GameId), Has.Length.EqualTo(1), "the row itself stays");

        await ReplaceAsync(tetris);
        Assert.That(await CountAsync(id), Is.EqualTo(1));
        Assert.That(await TitlesAsync(new GameQuery { CollectionId = id }), Is.EqualTo(new[] { "Tetris" }));
    }

    [Test]
    public async Task ListForGame_IsTheOwnersCollectionsByName()
    {
        var tetris = await AddGameAsync("Tetris");
        var b = await NewCollectionAsync("b");
        var a = await NewCollectionAsync("A");
        await NewCollectionAsync("without");
        var bobs = await NewCollectionAsync("bobs", _bob);
        foreach (var id in new[] { b, a, bobs })
            await _collections.AddGamesAsync(id, [tetris.GameId], T0, default);

        Assert.That(await _collections.ListForGameAsync(tetris.GameId, _alice, default),
            Is.EqualTo(new[] { new CollectionRef(a, "A"), new CollectionRef(b, "b") }));
    }

    [Test]
    public async Task Move_KeepsOneEntryWithTheEarlierAddedAt_InEveryUsersCollections_B18()
    {
        var source = await AddGameAsync("Source");
        var target = await AddGameAsync("Target");
        var both = await NewCollectionAsync("both");
        var sourceOnly = await NewCollectionAsync("source only");
        var targetEarlier = await NewCollectionAsync("target earlier");
        var bobs = await NewCollectionAsync("bobs", _bob);
        await _collections.AddGamesAsync(both, [source.GameId], T0, default);
        await _collections.AddGamesAsync(both, [target.GameId], T0.AddHours(1), default);
        await _collections.AddGamesAsync(sourceOnly, [source.GameId], T0.AddHours(2), default);
        await _collections.AddGamesAsync(targetEarlier, [target.GameId], T0, default);
        await _collections.AddGamesAsync(targetEarlier, [source.GameId], T0.AddHours(3), default);
        await _collections.AddGamesAsync(bobs, [source.GameId], T0, default);

        await _collections.MoveGameAsync(source.GameId, target.GameId, default);

        foreach (var id in new[] { both, sourceOnly, targetEarlier, bobs })
            Assert.That(await AddedAtAsync(id, source.GameId), Is.Empty);
        Assert.That(await AddedAtAsync(both, target.GameId), Is.EqualTo(new[] { T0 }));
        Assert.That(await AddedAtAsync(sourceOnly, target.GameId), Is.EqualTo(new[] { T0.AddHours(2) }));
        Assert.That(await AddedAtAsync(targetEarlier, target.GameId), Is.EqualTo(new[] { T0 }));
        Assert.That(await AddedAtAsync(bobs, target.GameId), Is.EqualTo(new[] { T0 }));
    }

    [Test]
    public async Task CollectionFilter_KeepsOtherFilters_AndAddedSortsByAddedAt()
    {
        var old = await AddGameAsync("Old", addedMinutes: 0);
        var mid = await AddGameAsync("Mid", addedMinutes: 1);
        var recent = await AddGameAsync("Recent", addedMinutes: 2);
        await AddGameAsync("Outside", addedMinutes: 3);
        var id = await NewCollectionAsync();
        await _collections.AddGamesAsync(id, [recent.GameId], T0, default);
        await _collections.AddGamesAsync(id, [old.GameId], T0.AddMinutes(1), default);
        await _collections.AddGamesAsync(id, [mid.GameId], T0.AddMinutes(2), default);

        Assert.That(await TitlesAsync(new GameQuery { CollectionId = id }), Is.EqualTo(new[] { "Mid", "Old", "Recent" }));
        Assert.That(await TitlesAsync(new GameQuery { CollectionId = id, Sort = GameSort.Added, Descending = true }),
            Is.EqualTo(new[] { "Mid", "Old", "Recent" }));
        Assert.That(await TitlesAsync(new GameQuery { CollectionId = id, Sort = GameSort.Added }),
            Is.EqualTo(new[] { "Recent", "Old", "Mid" }));
        Assert.That(await TitlesAsync(new GameQuery { CollectionId = id, Search = "re" }), Is.EqualTo(new[] { "Recent" }));
        Assert.That((await _games.ListAsync(new GameQuery { CollectionId = id, PageSize = 2 }, default)).Total, Is.EqualTo(3));
        Assert.That(await TitlesAsync(new GameQuery { CollectionId = Guid.NewGuid() }), Is.Empty);
    }

    [Test]
    public async Task NotInCollection_ExcludesMembers_KeepsOtherFilters_AndIgnoresOrphans_P1()
    {
        var alpha = await AddGameAsync("Alpha");
        await AddGameAsync("Bravo");
        await AddGameAsync("Brick");
        var gone = await AddGameAsync("Gone");
        var id = await NewCollectionAsync();
        var other = await NewCollectionAsync("Other");
        await _collections.AddGamesAsync(id, [alpha.GameId, gone.GameId], T0, default);
        await _collections.AddGamesAsync(other, [alpha.GameId], T0, default);
        await _mirror.DeleteAsync(gone.GameId, default); // its membership row stays as an orphan

        Assert.That(await TitlesAsync(new GameQuery { NotInCollectionId = id }), Is.EqualTo(new[] { "Bravo", "Brick" }));
        Assert.That(await TitlesAsync(new GameQuery { NotInCollectionId = id, Search = "bri" }), Is.EqualTo(new[] { "Brick" }));
        Assert.That(await TitlesAsync(new GameQuery { NotInCollectionId = id, System = "gb" }), Is.EqualTo(new[] { "Bravo", "Brick" }));
        Assert.That(await TitlesAsync(new GameQuery { NotInCollectionId = id, System = "gbc" }), Is.Empty);
        Assert.That((await _games.ListAsync(new GameQuery { NotInCollectionId = id, PageSize = 1 }, default)).Total, Is.EqualTo(2));
        Assert.That(await TitlesAsync(new GameQuery { CollectionId = id, NotInCollectionId = id }), Is.Empty);
        Assert.That(await TitlesAsync(new GameQuery { CollectionId = other, NotInCollectionId = id }), Is.Empty, "alpha is in both");
        Assert.That(await TitlesAsync(new GameQuery { NotInCollectionId = Guid.NewGuid() }), Is.EqualTo(new[] { "Alpha", "Bravo", "Brick" }));
    }
}
