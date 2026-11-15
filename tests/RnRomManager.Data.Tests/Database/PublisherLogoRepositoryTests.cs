using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using RnRomManager.Core.Discovery;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>B5 <c>publisher_logos</c> against MariaDB.</summary>
[Category("Integration")]
public class PublisherLogoRepositoryTests
{
    private static readonly DateTime T0 = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560);

    private string _cs = null!;
    private PublisherLogoRepository _repository = null!;
    private int _alice;
    private int _bob;

    [SetUp]
    public async Task SetUp()
    {
        _cs = await MariaDbServer.CreateDatabaseAsync();
        var factory = new MySqlConnectionFactory(_cs);
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _repository = new PublisherLogoRepository(factory);
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

    private static string Key(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));

    private static void AssertLogo(PublisherLogo? actual, PublisherLogo expected)
    {
        Assert.That(actual, Is.Not.Null);
        Assert.That(actual!.Key, Is.EqualTo(expected.Key));
        Assert.That(actual.MediaType, Is.EqualTo(expected.MediaType));
        Assert.That(actual.Bytes, Is.EqualTo(expected.Bytes));
        Assert.That(actual.UpdatedBy, Is.EqualTo(expected.UpdatedBy));
        Assert.That(actual.UpdatedAt, Is.EqualTo(expected.UpdatedAt));
    }

    private async Task<long> RowCountAsync()
    {
        await using var connection = new MySqlConnection(_cs);
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM publisher_logos");
    }

    [Test]
    public async Task Upsert_ThenGet_RoundTripsEveryField()
    {
        var logo = new PublisherLogo(Key("nintendo"), "image/png", [0, 1, 2, 255], _alice, T0);

        await _repository.UpsertAsync(logo, default);

        AssertLogo(await _repository.GetAsync(logo.Key, default), logo);
    }

    [Test]
    public async Task Get_Missing_IsNull()
    {
        Assert.That(await _repository.GetAsync(Key("nobody"), default), Is.Null);
    }

    [Test]
    public async Task Upsert_Existing_ReplacesEveryColumnInOneRow()
    {
        var key = Key("sega");
        await _repository.UpsertAsync(new PublisherLogo(key, "image/png", [1, 2, 3], _alice, T0), default);
        var replacement = new PublisherLogo(key, "image/jpeg", [9, 8, 7, 6, 5], _bob, T0.AddHours(1));

        await _repository.UpsertAsync(replacement, default);

        AssertLogo(await _repository.GetAsync(key, default), replacement);
        Assert.That(await RowCountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Upsert_Concurrent_LeavesOneWritersCompleteRow()
    {
        var key = Key("capcom");
        var writers = Enumerable.Range(0, 8).Select(i => new PublisherLogo(
            key, i % 2 == 0 ? "image/png" : "image/jpeg", Enumerable.Repeat((byte)i, 1000 + i).ToArray(),
            i % 2 == 0 ? _alice : _bob, T0.AddSeconds(i))).ToList();

        await Task.WhenAll(writers.Select(w => _repository.UpsertAsync(w, default)));

        var stored = await _repository.GetAsync(key, default);
        Assert.That(stored, Is.Not.Null);
        var winner = writers.Single(w => w.UpdatedAt == stored!.UpdatedAt);
        AssertLogo(stored, winner);
        Assert.That(await RowCountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task UpsertAndDelete_TouchOnlyTheirKey()
    {
        var nintendo = new PublisherLogo(Key("nintendo"), "image/png", [1], _alice, T0);
        var sega = new PublisherLogo(Key("sega"), "image/jpeg", [2], _bob, T0);
        await _repository.UpsertAsync(nintendo, default);
        await _repository.UpsertAsync(sega, default);

        await _repository.UpsertAsync(nintendo with { Bytes = [3, 3], UpdatedAt = T0.AddDays(1) }, default);
        AssertLogo(await _repository.GetAsync(sega.Key, default), sega);

        Assert.That(await _repository.DeleteAsync(nintendo.Key, default), Is.True);
        Assert.That(await _repository.GetAsync(nintendo.Key, default), Is.Null);
        AssertLogo(await _repository.GetAsync(sega.Key, default), sega);
    }

    [Test]
    public async Task Delete_Missing_IsFalse()
    {
        Assert.That(await _repository.DeleteAsync(Key("konami"), default), Is.False);
    }

    [Test]
    public async Task DeletingTheUser_KeepsTheLogo_WithoutItsAuthor()
    {
        var logo = new PublisherLogo(Key("atari"), "image/png", [4, 5], _alice, T0);
        await _repository.UpsertAsync(logo, default);

        await using (var connection = new MySqlConnection(_cs))
            await connection.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id = _alice });

        AssertLogo(await _repository.GetAsync(logo.Key, default), logo with { UpdatedBy = null });
    }

    [Test]
    public async Task ListUpdatedAt_ReturnsOnlyStoredKeys()
    {
        await _repository.UpsertAsync(new PublisherLogo(Key("nintendo"), "image/png", [1], _alice, T0), default);
        await _repository.UpsertAsync(new PublisherLogo(Key("sega"), "image/png", [1], _alice, T0.AddMinutes(5)), default);
        await _repository.UpsertAsync(new PublisherLogo(Key("atari"), "image/png", [1], _alice, T0), default);

        var updated = await _repository.ListUpdatedAtAsync([Key("nintendo"), Key("sega"), Key("konami")], default);

        Assert.That(updated, Is.EquivalentTo(new Dictionary<string, DateTime> { [Key("nintendo")] = T0, [Key("sega")] = T0.AddMinutes(5) }));
        Assert.That(updated[Key("sega")].Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(await _repository.ListUpdatedAtAsync([], default), Is.Empty);
    }

    [Test]
    public async Task Upsert_MaximumSize_RoundTrips()
    {
        var logo = new PublisherLogo(Key("ubisoft"), "image/jpeg", RandomNumberGenerator.GetBytes(512 * 1024), _alice, T0);

        await _repository.UpsertAsync(logo, default);

        AssertLogo(await _repository.GetAsync(logo.Key, default), logo);
    }
}
