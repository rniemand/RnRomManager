using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Reports;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Multiple Releases B15/B17: the report query and the max release count, against MariaDB.</summary>
[Category("Integration")]
public class MultipleReleasesQueryTests
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

    /// <summary>Mirrors a game with one release per flag list (an empty list is a plain release) and the given ZIP size.</summary>
    private async Task<Guid> Add(string title, string system, long zipSize, params string[][] flags)
    {
        var roms = flags.Select((f, i) => Rom($"{title} {i}.gb", RomBytes(++_seed), isPrimary: i == 0) with { Flags = f }).ToArray();
        var game = Game(title, roms) with { System = system };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await ((ILibraryMirror)new LibraryMirror(_factory)).ReplaceAsync(
            new MirrorEntry(game, $"{system}/{Bucket.For(name)}/{name}", zipSize, When.UtcDateTime), default);
        return game.GameId;
    }

    private static readonly string[] Plain = [];
    private static readonly string[] Update = ["Update"];
    private static readonly string[] Dlc = ["DLC"];

    private Task<Core.Database.Page<MultipleReleaseItem>> List(int min = 2, MultipleReleaseSort sort = MultipleReleaseSort.Releases,
        bool descending = true, int page = 1, int pageSize = 50, string? system = null) =>
        _reports.ListMultipleReleasesAsync(system, min, sort, descending, page, pageSize, default);

    /// <summary>Release editing B12: the System filter narrows the list, its total and the max release count.</summary>
    [Test]
    public async Task SystemFilter_NarrowsTheListAndTheMax()
    {
        var gb = await Add("Gb Game", "gb", 100, Plain, Plain);
        await Add("Nes Game", "nes", 100, Plain, Plain, Plain);

        var filtered = await List(system: "gb");

        Assert.That((filtered.Items.Select(i => i.Id), filtered.Total), Is.EqualTo((new[] { gb }, 1L)));
        Assert.That(await _reports.MaxReleaseCountAsync("gb", default), Is.EqualTo(2));
        Assert.That(await _reports.MaxReleaseCountAsync("nes", default), Is.EqualTo(3));
        Assert.That(await _reports.MaxReleaseCountAsync("snes", default), Is.Zero);
        Assert.That((await List()).Total, Is.EqualTo(2));
    }

    [Test]
    public async Task UpdatesAndDlcAreCountedApart_AndTheThresholdApplies()
    {
        var mixed = await Add("Mixed", "gb", 100, Plain, Plain, Dlc, Update, Update);
        await Add("Base With Dlc", "gb", 100, Plain, Dlc);
        await Add("Single With Updates", "gb", 100, Plain, Update, Update);
        var three = await Add("Three", "gb", 100, Plain, Plain, Plain);

        var atTwo = await List(min: 2);
        var atThree = await List(min: 3);

        Assert.That(atTwo.Items.Select(i => (i.Id, i.ReleaseCount, i.UpdateCount)),
            Is.EqualTo(new[] { (three, 3, 0), (mixed, 2, 2) }));
        Assert.That(atTwo.Total, Is.EqualTo(2));
        Assert.That(atThree.Items.Select(i => i.Id), Is.EqualTo(new[] { three }));
        Assert.That(atTwo.Items[0].DisplayTitle, Is.EqualTo("Three"));
        Assert.That(atTwo.Items[0].SystemId, Is.EqualTo("gb"));
    }

    [Test]
    public async Task Size_IsTheZipPlusExternalRomsAndTheirSetFiles()
    {
        var id = await Add("Disc Game", "gb", 100, Plain, Update);
        await using (var connection = _factory.Create())
        {
            await connection.ExecuteAsync(
                "UPDATE roms SET external_file_name = file_name, size_bytes = 1000 WHERE game_id = @id", new { id });
            var rom = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM roms WHERE game_id = @id ORDER BY id LIMIT 1", new { id });
            await connection.ExecuteAsync("""
                INSERT INTO rom_files (rom_id, game_id, ordinal, file_name, external_file_name, original_file_name, size_bytes, crc32, md5, sha1)
                VALUES (@rom, @id, 1, 't1.bin', 't1.bin', 't1.bin', 3000000000, '00000000', REPEAT('0', 32), REPEAT('0', 40))
                """, new { rom, id });
        }

        var item = (await List(min: 1)).Items.Single();

        Assert.That(item.Bytes, Is.EqualTo(100 + 1000 + 1000 + 3_000_000_000L));
    }

    [Test]
    public async Task EachSortKey_BothDirections_TieBreakersAscending()
    {
        // Equal counts and sizes so every key's tie-breakers show.
        var b = await Add("Bravo", "nes", 300, Plain, Plain, Update);
        var a = await Add("Alpha", "nes", 300, Plain, Plain, Update);
        var c = await Add("Charlie", "gb", 100, Plain, Plain, Plain);
        var a2 = await Add("Alpha", "gb", 200, Plain, Plain);

        async Task<Guid[]> Ids(MultipleReleaseSort sort, bool descending) =>
            (await List(sort: sort, descending: descending)).Items.Select(i => i.Id).ToArray();
        // Same title "Alpha": name ties break on system_id, then id.
        var alphaGbFirst = new[] { a2, a };

        Assert.Multiple(async () =>
        {
            Assert.That(await Ids(MultipleReleaseSort.Name, false), Is.EqualTo(alphaGbFirst.Append(b).Append(c)));
            Assert.That(await Ids(MultipleReleaseSort.Name, true), Is.EqualTo(new[] { c, b }.Concat(alphaGbFirst)));
            Assert.That(await Ids(MultipleReleaseSort.System, false), Is.EqualTo(new[] { a2, c, a, b }));
            Assert.That(await Ids(MultipleReleaseSort.System, true), Is.EqualTo(new[] { a, b, a2, c }));
            Assert.That(await Ids(MultipleReleaseSort.Releases, true), Is.EqualTo(new[] { c }.Concat(Tied(a, a2, b))));
            Assert.That(await Ids(MultipleReleaseSort.Releases, false), Is.EqualTo(Tied(a, a2, b).Append(c)));
            Assert.That(await Ids(MultipleReleaseSort.Updates, true), Is.EqualTo(Tied(a, b).Concat(Tied(a2, c))));
            Assert.That(await Ids(MultipleReleaseSort.Updates, false), Is.EqualTo(Tied(a2, c).Concat(Tied(a, b))));
            Assert.That(await Ids(MultipleReleaseSort.Size, true), Is.EqualTo(Tied(a, b).Append(a2).Append(c)));
            Assert.That(await Ids(MultipleReleaseSort.Size, false), Is.EqualTo(new[] { c, a2 }.Concat(Tied(a, b))));
        });

        // Ties on a numeric key: display_title, then id (the two "Alpha" games by id).
        IEnumerable<Guid> Tied(params Guid[] ids)
        {
            var titles = new Dictionary<Guid, string> { [a] = "Alpha", [a2] = "Alpha", [b] = "Bravo", [c] = "Charlie" };
            return ids.OrderBy(id => titles[id], StringComparer.Ordinal).ThenBy(id => id.ToString("D"), StringComparer.Ordinal);
        }
    }

    [Test]
    public async Task Paging_KeepsTheTotal()
    {
        foreach (var title in new[] { "A", "B", "C" })
            await Add(title, "gb", 100, Plain, Plain);
        await Add("Solo", "gb", 100, Plain);

        var second = await List(sort: MultipleReleaseSort.Name, descending: false, page: 2, pageSize: 2);

        Assert.That(second.Total, Is.EqualTo(3));
        Assert.That(second.Items.Select(i => i.DisplayTitle), Is.EqualTo(new[] { "C" }));
    }

    [Test]
    public async Task MaxReleaseCount_IsZeroWhenEmpty_ElseTheHighestReleaseCount()
    {
        Assert.That(await _reports.MaxReleaseCountAsync(null, default), Is.Zero);

        await Add("One", "gb", 100, Plain, Update, Update, Update);
        await Add("Two", "gb", 100, Plain, Plain, Dlc);

        Assert.That(await _reports.MaxReleaseCountAsync(null, default), Is.EqualTo(2));
    }

    [Test]
    public async Task BaseWithDlc_IsNotAMultipleReleaseGame()
    {
        await Add("Base With Dlc", "gb", 100, Plain, Dlc);

        Assert.That((await List(min: 2)).Total, Is.Zero);
        Assert.That(await _reports.MaxReleaseCountAsync(null, default), Is.EqualTo(1));
    }
}
