using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>UI polish B21: the Activity report's recently added releases, against MariaDB.</summary>
[Category("Integration")]
public class RecentlyAddedRomsTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
    private MySqlConnectionFactory _factory = null!;
    private ReportRepository _reports = null!;
    private int _seed;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(_factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _reports = new ReportRepository(_factory);
    }

    /// <summary>Mirrors a one-release game, then stamps its <c>date_added</c>; returns the game and ROM ids.</summary>
    private async Task<(Guid Game, long Rom)> Add(string title, DateTime addedAt)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with { Metadata = new InfoJsonGameMetadata(When, When, "0.1.0") };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await ((ILibraryMirror)new LibraryMirror(_factory)).ReplaceAsync(new MirrorEntry(game, $"gb/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        await using var connection = _factory.Create();
        await connection.ExecuteAsync("UPDATE roms SET date_added = @addedAt WHERE game_id = @id", new { addedAt, id = game.GameId });
        return (game.GameId, await connection.ExecuteScalarAsync<long>("SELECT id FROM roms WHERE game_id = @id", new { id = game.GameId }));
    }

    [Test]
    public async Task NewestFirst_TiesById_CappedAtTheLimit_WithTheReleaseTotal()
    {
        var old = await Add("Old", T0);
        var tieA = await Add("Tie A", T0.AddMinutes(5));
        var tieB = await Add("Tie B", T0.AddMinutes(5));
        var newest = await Add("Newest", T0.AddMinutes(9));
        await using (var connection = _factory.Create())
        {
            await connection.ExecuteAsync("UPDATE roms SET size_bytes = 1000 WHERE id = @Rom", new { newest.Rom });
            await connection.ExecuteAsync("""
                INSERT INTO rom_files (rom_id, game_id, ordinal, file_name, external_file_name, original_file_name, size_bytes, crc32, md5, sha1)
                VALUES (@Rom, @Game, 1, 't1.bin', 't1.bin', 't1.bin', 3000000000, '00000000', REPEAT('0', 32), REPEAT('0', 40)),
                       (@Rom, @Game, 2, 't2.bin', 't2.bin', 't2.bin', 5, '00000000', REPEAT('0', 32), REPEAT('0', 40))
                """, new { newest.Rom, newest.Game });
        }

        var all = await _reports.ListRecentlyAddedRomsAsync(20, default);
        var top = await _reports.ListRecentlyAddedRomsAsync(2, default);

        Assert.That(all.Select(r => r.RomId), Is.EqualTo(new[] { newest.Rom, tieB.Rom, tieA.Rom, old.Rom }));
        Assert.That(top.Select(r => r.RomId), Is.EqualTo(new[] { newest.Rom, tieB.Rom }));
        var first = all[0];
        Assert.That((first.GameId, first.GameTitle, first.FileName, first.System, first.SizeBytes, first.AddedAt),
            Is.EqualTo((newest.Game, "Newest", "Newest.gb", "gb", 3_000_001_005L, T0.AddMinutes(9))));
    }

    [Test]
    public async Task Empty_WhenNoReleases() =>
        Assert.That(await _reports.ListRecentlyAddedRomsAsync(20, default), Is.Empty);
}
