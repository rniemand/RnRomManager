using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using RnRomManager.Core.Bundles;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Bundle Creator B2/B4 <c>bundle_queues</c> against MariaDB.</summary>
[Category("Integration")]
public class BundleQueueRepositoryTests
{
    private static readonly DateTime T0 = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560);

    private string _cs = null!;
    private BundleQueueRepository _repository = null!;
    private int _alice;
    private int _bob;

    [SetUp]
    public async Task SetUp()
    {
        _cs = await MariaDbServer.CreateDatabaseAsync();
        var factory = new MySqlConnectionFactory(_cs);
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _repository = new BundleQueueRepository(factory);
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

    private static readonly Guid Game = Guid.Parse("8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f");
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static BundleEntry MainFile(string title, string file) => new(Game, Sha, null, title, file);

    private static BundleEntry SetFile(string title, string file, string memberName) =>
        new(Game, Sha, new BundleMember("fedcba9876543210fedcba9876543210fedcba98", memberName), title, file);

    private async Task<BundleQueue> CreateAsync(string name, int? userId = null, IReadOnlyList<BundleEntry>? entries = null)
    {
        var write = await _repository.CreateAsync(userId ?? _alice, Guid.NewGuid(), name, entries ?? [MainFile("Game", "game.zip")], T0, default);
        Assert.That(write.Status, Is.EqualTo(BundleQueueWriteStatus.Ok));
        return write.Queue!;
    }

    [Test]
    public async Task Create_ThenGet_RoundTripsEntriesInOrder_WithNonAsciiLabels()
    {
        BundleEntry[] entries =
        [
            SetFile("Disc Runner (USA)", "Disc Runner (USA) (Track 2).bin", "Disc Runner (USA) (Track 2).bin"),
            MainFile("Pokémon – Édition Rouge", "Pokémon – Édition Rouge.gb"),
            MainFile("ゲーム", "ゲーム.zip"),
        ];

        var created = await CreateAsync("Mine", entries: entries);
        var loaded = await _repository.GetAsync(created.Id, _alice, default);

        Assert.That(created.Revision, Is.EqualTo(1));
        Assert.That(loaded, Is.Not.Null);
        Assert.That((loaded!.Id, loaded.Name, loaded.Revision), Is.EqualTo((created.Id, "Mine", 1)));
        Assert.That(loaded.Entries, Is.EqualTo(entries));

        await using var connection = new MySqlConnection(_cs);
        var stored = await connection.ExecuteScalarAsync<string>("SELECT entries FROM bundle_queues WHERE id = @id", new { id = created.Id.ToString() });
        Assert.That(stored, Does.Contain("Pokémon – Édition Rouge").And.Contain("ゲーム").And.Not.Contain("\\u"));
    }

    [Test]
    public async Task List_IsTheOwnersOnly_SortedByName_WithCounts()
    {
        await CreateAsync("b", entries: [MainFile("a", "a.zip"), MainFile("b", "b.zip")]);
        await CreateAsync("A");
        await CreateAsync("other", _bob);

        var list = await _repository.ListAsync(_alice, default);

        Assert.That(list.Select(q => (q.Name, q.Revision, q.ItemCount)), Is.EqualTo(new[] { ("A", 1, 1), ("b", 1, 2) }));
    }

    [Test]
    public async Task AnotherUsersQueue_CannotBeReadReplacedOrDeleted()
    {
        var queue = await CreateAsync("Mine");

        Assert.That(await _repository.GetAsync(queue.Id, _bob, default), Is.Null);
        Assert.That((await _repository.ReplaceAsync(queue.Id, _bob, "Mine", [], 1, T0, default)).Status, Is.EqualTo(BundleQueueWriteStatus.NotFound));
        Assert.That(await _repository.DeleteAsync(queue.Id, _bob, 1, default), Is.EqualTo(BundleQueueWriteStatus.NotFound));
        Assert.That(await _repository.GetAsync(queue.Id, _alice, default), Is.EqualTo(queue).Using<BundleQueue>(SameSnapshot));
        Assert.That(await _repository.GetAsync(Guid.NewGuid(), _alice, default), Is.Null);
    }

    [Test]
    public async Task Name_IsUniquePerUser_IgnoringCase_ButNotAccents()
    {
        await CreateAsync("Pokemon");

        Assert.That((await _repository.CreateAsync(_alice, Guid.NewGuid(), "POKEMON", [], T0, default)).Status, Is.EqualTo(BundleQueueWriteStatus.NameTaken));
        await CreateAsync("Pokémon");
        await CreateAsync("pokemon", _bob);
    }

    [Test]
    public async Task Create_NameConflict_IsReportedBeforeTheLimit()
    {
        for (var i = 0; i < BundleQueueLimits.MaxQueuesPerUser; i++) await CreateAsync($"q{i}", entries: []);

        Assert.That((await _repository.CreateAsync(_alice, Guid.NewGuid(), "Q0", [], T0, default)).Status, Is.EqualTo(BundleQueueWriteStatus.NameTaken));
        Assert.That((await _repository.CreateAsync(_alice, Guid.NewGuid(), "new", [], T0, default)).Status, Is.EqualTo(BundleQueueWriteStatus.Limit));
        await CreateAsync("new", _bob);
    }

    [Test]
    public async Task ConcurrentCreates_AtTheLimit_OnlyOneSucceeds()
    {
        for (var i = 0; i < BundleQueueLimits.MaxQueuesPerUser - 1; i++) await CreateAsync($"q{i}", entries: []);

        var writes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Task.Run(() => _repository.CreateAsync(_alice, Guid.NewGuid(), $"race{i}", [], T0, default))));

        Assert.That(writes.Count(w => w.Status == BundleQueueWriteStatus.Ok), Is.EqualTo(1));
        Assert.That(writes.Count(w => w.Status == BundleQueueWriteStatus.Limit), Is.EqualTo(5));
        Assert.That((await _repository.ListAsync(_alice, default)).Count, Is.EqualTo(BundleQueueLimits.MaxQueuesPerUser));
    }

    [Test]
    public async Task ConcurrentCreates_WithOneName_OnlyOneSucceeds()
    {
        var writes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => _repository.CreateAsync(_alice, Guid.NewGuid(), "Same", [], T0, default))));

        Assert.That(writes.Count(w => w.Status == BundleQueueWriteStatus.Ok), Is.EqualTo(1));
        Assert.That(writes.Count(w => w.Status == BundleQueueWriteStatus.NameTaken), Is.EqualTo(5));
    }

    [Test]
    public async Task Create_ForAMissingUser_IsNotFound()
    {
        Assert.That((await _repository.CreateAsync(_bob + 1000, Guid.NewGuid(), "x", [], T0, default)).Status, Is.EqualTo(BundleQueueWriteStatus.NotFound));
    }

    [Test]
    public async Task Replace_AtTheRevision_WritesEverything_AndIncrementsIt()
    {
        var queue = await CreateAsync("Old");
        BundleEntry[] entries = [MainFile("b", "b.zip"), MainFile("a", "a.zip")];

        var write = await _repository.ReplaceAsync(queue.Id, _alice, "New", entries, 1, T0.AddHours(1), default);
        var again = await _repository.ReplaceAsync(queue.Id, _alice, "New", entries, 2, T0.AddHours(2), default);

        Assert.That(write.Status, Is.EqualTo(BundleQueueWriteStatus.Ok));
        Assert.That(write.Queue!.Revision, Is.EqualTo(2));
        Assert.That(again.Queue!.Revision, Is.EqualTo(3), "an identical write still finds the row");
        var loaded = await _repository.GetAsync(queue.Id, _alice, default);
        Assert.That((loaded!.Name, loaded.Revision), Is.EqualTo(("New", 3)));
        Assert.That(loaded.Entries, Is.EqualTo(entries));
        Assert.That((await _repository.ListAsync(_alice, default)).Single().ItemCount, Is.EqualTo(2));
    }

    [Test]
    public async Task Replace_WithAStaleRevision_IsChanged_ThenChecksTheName()
    {
        await CreateAsync("Taken");
        var queue = await CreateAsync("Mine");
        await _repository.ReplaceAsync(queue.Id, _alice, "Mine", [], 1, T0, default);

        Assert.That((await _repository.ReplaceAsync(queue.Id, _alice, "TAKEN", [], 1, T0, default)).Status, Is.EqualTo(BundleQueueWriteStatus.Changed),
            "revision before name");
        Assert.That((await _repository.ReplaceAsync(queue.Id, _alice, "TAKEN", [], 2, T0, default)).Status, Is.EqualTo(BundleQueueWriteStatus.NameTaken));
        Assert.That((await _repository.ReplaceAsync(queue.Id, _alice, "MINE", [], 2, T0, default)).Status, Is.EqualTo(BundleQueueWriteStatus.Ok),
            "its own name in another case is no conflict");
    }

    [Test]
    public async Task ConcurrentStaleReplaces_CannotOverwriteANewerSnapshot()
    {
        var queue = await CreateAsync("Mine");

        var writes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Task.Run(() => _repository.ReplaceAsync(queue.Id, _alice, "Mine", [MainFile($"t{i}", $"f{i}.zip")], 1, T0, default))));

        var winner = writes.Single(w => w.Status == BundleQueueWriteStatus.Ok);
        Assert.That(writes.Count(w => w.Status == BundleQueueWriteStatus.Changed), Is.EqualTo(5));
        var loaded = await _repository.GetAsync(queue.Id, _alice, default);
        Assert.That(loaded!.Revision, Is.EqualTo(2));
        Assert.That(loaded.Entries, Is.EqualTo(winner.Queue!.Entries));
    }

    [Test]
    public async Task Delete_NeedsTheCurrentRevision()
    {
        var queue = await CreateAsync("Mine");
        await _repository.ReplaceAsync(queue.Id, _alice, "Mine", [], 1, T0, default);

        Assert.That(await _repository.DeleteAsync(queue.Id, _alice, 1, default), Is.EqualTo(BundleQueueWriteStatus.Changed));
        Assert.That(await _repository.GetAsync(queue.Id, _alice, default), Is.Not.Null);
        Assert.That(await _repository.DeleteAsync(queue.Id, _alice, 2, default), Is.EqualTo(BundleQueueWriteStatus.Ok));
        Assert.That(await _repository.GetAsync(queue.Id, _alice, default), Is.Null);
        Assert.That(await _repository.DeleteAsync(queue.Id, _alice, 2, default), Is.EqualTo(BundleQueueWriteStatus.NotFound));
    }

    [Test]
    public async Task DeletingTheUser_CascadesItsQueues()
    {
        var queue = await CreateAsync("Mine");

        await using (var connection = new MySqlConnection(_cs))
            await connection.ExecuteAsync("DELETE FROM users WHERE id = @_alice", new { _alice });

        Assert.That(await _repository.GetAsync(queue.Id, _alice, default), Is.Null);
    }

    private static bool SameSnapshot(BundleQueue a, BundleQueue b) =>
        a.Id == b.Id && a.Name == b.Name && a.Revision == b.Revision && a.Entries.SequenceEqual(b.Entries);
}
