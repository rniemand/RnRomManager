using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Release editing B14: the Newly Added Games query, against MariaDB.</summary>
[Category("Integration")]
public class NewlyAddedGamesQueryTests
{
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

    /// <summary>Mirrors a game created <paramref name="minutes"/> after a fixed time, with one release per flag list.</summary>
    private async Task<Guid> Add(
        string title,
        string system,
        int minutes,
        params string[][] flags)
    {
        if (flags.Length == 0)
            flags = [[]];
        var roms = flags.Select((f, i) => Rom($"{title} {i}.gb", RomBytes(++_seed), isPrimary: i == 0) with { Flags = f }).ToArray();
        var game = Game(title, roms) with { System = system };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await ((ILibraryMirror)new LibraryMirror(_factory)).ReplaceAsync(
            new MirrorEntry(game, $"{system}/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        await using var connection = _factory.Create();
        await connection.ExecuteAsync("UPDATE games SET created_at = @at WHERE id = @id",
            new { at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutes), id = game.GameId });
        return game.GameId;
    }

    [Test]
    public async Task TenNewestPerSystem_NewestFirst_SystemsById()
    {
        var gb = new List<Guid>();
        for (var i = 0; i < 12; i++)
            gb.Add(await Add($"Gb {i:00}", "gb", i));
        var nes = await Add("Nes", "nes", 100, [], ["Update"], ["DLC"], []);

        var rows = await _reports.ListNewlyAddedGamesAsync(null, 10, default);

        Assert.That(rows.Select(r => r.Id), Is.EqualTo(Enumerable.Range(2, 10).Reverse().Select(i => gb[i]).Append(nes)));
        var last = rows[^1];
        Assert.That((last.DisplayTitle, last.SystemId, last.ReleaseCount, last.AddedAt),
            Is.EqualTo(("Nes", "nes", 2, new DateTime(2026, 1, 1, 1, 40, 0, DateTimeKind.Utc))));
    }

    [Test]
    public async Task Ties_BreakByIdDescending()
    {
        var a = await Add("A", "gb", 5);
        var b = await Add("B", "gb", 5);

        var rows = await _reports.ListNewlyAddedGamesAsync(null, 10, default);

        Assert.That(rows.Select(r => r.Id), Is.EqualTo(new[] { a, b }.OrderByDescending(g => g.ToString(), StringComparer.Ordinal)));
    }

    [Test]
    public async Task OneSystem_AndEmpty()
    {
        Assert.That(await _reports.ListNewlyAddedGamesAsync(null, 10, default), Is.Empty);
        await Add("Gb", "gb", 1);
        var nes = await Add("Nes", "nes", 2);

        Assert.That((await _reports.ListNewlyAddedGamesAsync("nes", 10, default)).Select(r => r.Id), Is.EqualTo(new[] { nes }));
        Assert.That(await _reports.ListNewlyAddedGamesAsync("snes", 10, default), Is.Empty);
    }
}
