using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using RnRomManager.Core.Collections;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>B1–B7 <c>collections</c> against MariaDB.</summary>
[Category("Integration")]
public class CollectionRepositoryTests
{
    private static readonly DateTime T0 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560);

    private string _cs = null!;
    private CollectionRepository _repository = null!;
    private int _alice;
    private int _bob;

    [SetUp]
    public async Task SetUp()
    {
        _cs = await MariaDbServer.CreateDatabaseAsync();
        var factory = new MySqlConnectionFactory(_cs);
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _repository = new CollectionRepository(factory);
        _alice = await InsertUserAsync("alice");
        _bob = await InsertUserAsync("bob");
    }

    private async Task<int> InsertUserAsync(string username)
    {
        await using var connection = new MySqlConnection(_cs);
        return await connection.ExecuteScalarAsync<int>(
            "INSERT INTO users (username, password_hash, failed_login_count, security_stamp, created_at, updated_at) " +
            "VALUES (@username, 'hash', 0, REPEAT('a', 32), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)); SELECT LAST_INSERT_ID();", new { username });
    }

    private Collection New(string name, int? userId = null) =>
        new(Guid.NewGuid(), userId ?? _alice, name, null, null, null, T0, T0);

    private async Task<Collection> InsertAsync(string name, int? userId = null)
    {
        var collection = New(name, userId);
        await _repository.InsertAsync(collection, default);
        return collection;
    }

    [Test]
    public async Task Insert_ThenGet_RoundTripsEveryField()
    {
        var collection = New("Pokémon") with { Notes = "# Gotta\ncatch", CoverFormat = "png", CoverUpdatedAt = T0.AddDays(1), UpdatedAt = T0.AddHours(1) };

        await _repository.InsertAsync(collection, default);

        Assert.That(await _repository.GetAsync(collection.Id, _alice, default), Is.EqualTo(collection));
    }

    [Test]
    public async Task Get_AnotherUsersCollection_IsNull()
    {
        var collection = await InsertAsync("Mine");

        Assert.That(await _repository.GetAsync(collection.Id, _bob, default), Is.Null);
        Assert.That(await _repository.GetAsync(Guid.NewGuid(), _alice, default), Is.Null);
    }

    [Test]
    public async Task Update_WritesEveryField_OnlyForTheOwner()
    {
        var collection = await InsertAsync("Old");
        var updated = collection with { Name = "New", Notes = "n", CoverFormat = "jpg", CoverUpdatedAt = T0.AddDays(2), UpdatedAt = T0.AddDays(2) };

        Assert.That(await _repository.UpdateAsync(updated with { UserId = _bob }, default), Is.False);
        Assert.That(await _repository.GetAsync(collection.Id, _alice, default), Is.EqualTo(collection));
        Assert.That(await _repository.UpdateAsync(updated, default), Is.True);
        Assert.That(await _repository.UpdateAsync(updated, default), Is.True, "an identical write still finds the row");
        Assert.That(await _repository.GetAsync(collection.Id, _alice, default), Is.EqualTo(updated));
    }

    [Test]
    public async Task Delete_OnlyForTheOwner()
    {
        var collection = await InsertAsync("Mine");

        Assert.That(await _repository.DeleteAsync(collection.Id, _bob, default), Is.False);
        Assert.That(await _repository.DeleteAsync(collection.Id, _alice, default), Is.True);
        Assert.That(await _repository.GetAsync(collection.Id, _alice, default), Is.Null);
        Assert.That(await _repository.DeleteAsync(collection.Id, _alice, default), Is.False);
    }

    [Test]
    public async Task Name_IsUniquePerUser_IgnoringCase_ButNotAccents()
    {
        await InsertAsync("Pokemon");

        await Assert.ThrowsAsync<CollectionNameTakenException>(() => InsertAsync("pokemon"));
        await Assert.DoesNotThrowAsync(() => InsertAsync("Pokémon"));
        await Assert.DoesNotThrowAsync(() => InsertAsync("pokemon", _bob));
    }

    [Test]
    public async Task Update_ToATakenName_Throws()
    {
        await InsertAsync("Taken");
        var other = await InsertAsync("Other");

        await Assert.ThrowsAsync<CollectionNameTakenException>(() => _repository.UpdateAsync(other with { Name = "TAKEN" }, default));
    }

    [Test]
    public async Task List_IsTheOwnersOnly_SortedByName_AndPaged()
    {
        await InsertAsync("b");
        await InsertAsync("C");
        await InsertAsync("a");
        await InsertAsync("other", _bob);

        var first = await _repository.ListAsync(_alice, [], 1, 2, default);
        var second = await _repository.ListAsync(_alice, [], 2, 2, default);

        Assert.That(first.Items.Select(c => c.Name), Is.EqualTo(new[] { "a", "b" }));
        Assert.That(second.Items.Select(c => c.Name), Is.EqualTo(new[] { "C" }));
        Assert.That((first.Total, first.PageNumber, first.PageSize), Is.EqualTo((3L, 1, 2)));
    }

    [Test]
    public async Task List_MatchesEveryWord_AccentInsensitively_WithLiteralWildcards()
    {
        await InsertAsync("Pokémon Red");
        await InsertAsync("Pokemon Blue");
        await InsertAsync("100% done");
        await InsertAsync("100 done");
        await InsertAsync("a_b");
        await InsertAsync("axb");

        async Task<string[]> Names(params string[] words) =>
            (await _repository.ListAsync(_alice, words, 1, 50, default)).Items.Select(c => c.Name).ToArray();

        Assert.That(await Names("pokemon"), Is.EqualTo(new[] { "Pokemon Blue", "Pokémon Red" }));
        Assert.That(await Names("red", "POKEMON"), Is.EqualTo(new[] { "Pokémon Red" }));
        Assert.That(await Names("%"), Is.EqualTo(new[] { "100% done" }));
        Assert.That(await Names("_"), Is.EqualTo(new[] { "a_b" }));
        Assert.That((await _repository.ListAsync(_alice, ["pokemon"], 1, 50, default)).Total, Is.EqualTo(2));
    }
}
