using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Reports;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Missing IGDB IDs R1: the report query, against MariaDB.</summary>
[Category("Integration")]
public class MissingIgdbIdsQueryTests
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

    private async Task<Guid> Add(string title, string system = "gb", int? year = 1990, int? igdbId = null)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with { System = system, ReleaseYear = year, IgdbId = igdbId };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await ((ILibraryMirror)new LibraryMirror(_factory)).ReplaceAsync(
            new MirrorEntry(game, $"{system}/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    private Task<Core.Database.Page<MissingIgdbItem>> List(string? system = null, MissingIgdbStatus status = MissingIgdbStatus.Missing,
        string? q = null, MissingMetadataSort sort = MissingMetadataSort.Name, bool descending = false, int page = 1, int pageSize = 50) =>
        _reports.ListMissingIgdbIdsAsync(system, status, q, sort, descending, page, pageSize, default);

    [Test]
    public async Task Missing_ListsUnlinked_NotOnIgdb_ListsZero_LinkedNeither()
    {
        var unlinked = await Add("Unlinked", year: null);
        var notOn = await Add("Not On", igdbId: 0);
        await Add("Linked", igdbId: 1234);

        var missing = await List();
        var notOnIgdb = await List(status: MissingIgdbStatus.NotOnIgdb);

        Assert.That((missing.Items.Select(i => i.Id), missing.Total), Is.EqualTo((new[] { unlinked }, 1L)));
        Assert.That(missing.Items.Single(), Is.EqualTo(new MissingIgdbItem(unlinked, "Unlinked", "gb", null)));
        Assert.That((notOnIgdb.Items.Select(i => i.Id), notOnIgdb.Total), Is.EqualTo((new[] { notOn }, 1L)));
    }

    [Test]
    public async Task SystemAndTitleFilters_LikeWildcardsAreLiteral()
    {
        var gb = await Add("Alpha 100%");
        await Add("Alpha 100x", "nes");
        var under = await Add("A_B");
        await Add("AxB");

        Assert.That((await List(system: "gb", q: "Alpha")).Items.Select(i => i.Id), Is.EqualTo(new[] { gb }));
        Assert.That((await List(q: "100%")).Items.Select(i => i.Id), Is.EqualTo(new[] { gb }));
        Assert.That((await List(q: "A_B")).Items.Select(i => i.Id), Is.EqualTo(new[] { under }));
    }

    [Test]
    public async Task EachSortKey_BothDirections_NullYearsLast()
    {
        var a = await Add("Alpha", "nes", 1995);
        var b = await Add("Bravo", "gb", null);
        var c = await Add("Charlie", "gb", 1990);
        var d = await Add("Delta", "nes", null);

        async Task<Guid[]> Ids(MissingMetadataSort sort, bool desc) => [.. (await List(sort: sort, descending: desc)).Items.Select(i => i.Id)];

        Assert.That(await Ids(MissingMetadataSort.Name, false), Is.EqualTo(new[] { a, b, c, d }));
        Assert.That(await Ids(MissingMetadataSort.Name, true), Is.EqualTo(new[] { d, c, b, a }));
        Assert.That(await Ids(MissingMetadataSort.System, false), Is.EqualTo(new[] { b, c, a, d }));
        Assert.That(await Ids(MissingMetadataSort.System, true), Is.EqualTo(new[] { a, d, b, c }), "title tie-breaker stays ascending");
        Assert.That(await Ids(MissingMetadataSort.Year, false), Is.EqualTo(new[] { c, a, b, d }));
        Assert.That(await Ids(MissingMetadataSort.Year, true), Is.EqualTo(new[] { a, c, b, d }), "nulls last both ways");
    }

    [Test]
    public async Task Paging()
    {
        var ids = new List<Guid>();
        foreach (var title in new[] { "A", "B", "C" })
            ids.Add(await Add(title));

        var second = await List(page: 2, pageSize: 2);

        Assert.That((second.Items.Select(i => i.Id), second.Total), Is.EqualTo((new[] { ids[2] }, 3L)));
    }
}
