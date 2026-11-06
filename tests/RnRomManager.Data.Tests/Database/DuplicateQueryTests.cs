using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Reports;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>RP8: the duplicates query's SQL-side games key, against MariaDB.</summary>
[Category("Integration")]
public class DuplicateQueryTests
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

    private async Task<Guid> Add(string title)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with
        {
            Metadata = new InfoJsonGameMetadata(When, When, "0.1.0"),
        };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await ((ILibraryMirror)new LibraryMirror(_factory)).ReplaceAsync(new MirrorEntry(game, $"gb/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    [Test]
    public async Task SqlGamesKey_EqualsDuplicateGroupsGamesKey()
    {
        var ids = new[] { await Add("Quest A"), await Add("Quest B"), await Add("Quest C") };
        await using (var connection = _factory.Create())
            await connection.ExecuteAsync("UPDATE roms SET base_title = 'Quest'");

        var page = await _reports.ListDuplicatesAsync(null, 1, 25, default);

        var group = page.Items.Single();
        Assert.That(group.GamesKey, Is.EqualTo(DuplicateGroups.GamesKey(ids)));
        Assert.That(group.GamesKey, Is.EqualTo(DuplicateGroups.GamesKey(ids.Reverse())));
        Assert.That(group.Games.Select(g => g.Id), Is.EquivalentTo(ids));
    }
}
