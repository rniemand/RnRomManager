using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Reports;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Missing Metadata M1–M3: the report query and the next game, against MariaDB.</summary>
[Category("Integration")]
public class MissingMetadataQueryTests
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

    private static readonly string[] Dev = ["Dev Co"];
    private static readonly string[] Pub = ["Pub Co"];

    private async Task<Guid> Add(
        string title,
        string system = "gb",
        int? year = 1990,
        string[]? developers = null,
        string[]? publishers = null)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with
        {
            System = system, ReleaseYear = year, Developers = developers ?? Dev, Publishers = publishers ?? Pub,
        };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await ((ILibraryMirror)new LibraryMirror(_factory)).ReplaceAsync(
            new MirrorEntry(game, $"{system}/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    private Task<Core.Database.Page<MissingMetadataItem>> List(
        string? system = null,
        MissingMetadataField missing = MissingMetadataField.Any,
        string? q = null,
        MissingMetadataSort sort = MissingMetadataSort.Name,
        bool descending = false,
        int page = 1,
        int pageSize = 50) =>
        _reports.ListMissingMetadataAsync(system, missing, q, sort, descending, page, pageSize, default);

    [Test]
    public async Task EachMissingRule_IsListed_CompleteGamesAreNot_ListsAsStored()
    {
        await Add("Complete");
        var noYear = await Add("No Year", year: null);
        var noDev = await Add("No Dev", developers: []);
        var noPub = await Add("No Pub", publishers: []);

        var page = await List();

        Assert.That(page.Items.Select(i => i.Id), Is.EqualTo(new[] { noDev, noPub, noYear }), "title order");
        Assert.That(page.Total, Is.EqualTo(3));
        var item = page.Items.Single(i => i.Id == noYear);
        Assert.That((item.DisplayTitle, item.SystemId, item.ReleaseYear, item.Developers, item.Publishers),
            Is.EqualTo(("No Year", "gb", (int?)null, (IReadOnlyList<string>)Dev, (IReadOnlyList<string>)Pub)));
        Assert.That(page.Items.Single(i => i.Id == noDev).Developers, Is.Empty);
    }

    [Test]
    public async Task MissingFilter_SelectsOneField()
    {
        var noYear = await Add("No Year", year: null);
        var noDev = await Add("No Dev", developers: []);
        var noPub = await Add("No Pub", publishers: []);

        Assert.That((await List(missing: MissingMetadataField.Year)).Items.Select(i => i.Id), Is.EqualTo(new[] { noYear }));
        Assert.That((await List(missing: MissingMetadataField.Developer)).Items.Select(i => i.Id), Is.EqualTo(new[] { noDev }));
        Assert.That((await List(missing: MissingMetadataField.Publisher)).Items.Select(i => i.Id), Is.EqualTo(new[] { noPub }));
    }

    [Test]
    public async Task SystemAndTitleFilters_LikeWildcardsAreLiteral()
    {
        var gb = await Add("Alpha 100%", year: null);
        await Add("Alpha 100x", "nes", year: null);
        var under = await Add("A_B", year: null);
        await Add("AxB", year: null);

        Assert.That((await List(system: "gb", q: "Alpha")).Items.Select(i => i.Id), Is.EqualTo(new[] { gb }));
        Assert.That((await List(q: "100%")).Items.Select(i => i.Id), Is.EqualTo(new[] { gb }));
        Assert.That((await List(q: "A_B")).Items.Select(i => i.Id), Is.EqualTo(new[] { under }));
    }

    [Test]
    public async Task EachSortKey_BothDirections_NullYearsLast()
    {
        var a = await Add("Alpha", "nes", 1995, developers: []);
        var b = await Add("Bravo", "gb", null);
        var c = await Add("Charlie", "gb", 1990, publishers: []);
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
            ids.Add(await Add(title, year: null));

        var second = await List(page: 2, pageSize: 2);

        Assert.That((second.Items.Select(i => i.Id), second.Total), Is.EqualTo((new[] { ids[2] }, 3L)));
    }

    [Test]
    public async Task Next_FollowsTitleOrder_Wraps_AndKeepsAFixedAnchorsPlace()
    {
        var a = await Add("Alpha", year: null);
        await Add("Bravo");
        var c = await Add("Charlie", developers: []);

        Assert.That(await _reports.NextMissingMetadataAsync(null, default), Is.EqualTo(new MissingImageSuccessor(a, false)));
        Assert.That(await _reports.NextMissingMetadataAsync(a, default), Is.EqualTo(new MissingImageSuccessor(c, false)));
        Assert.That(await _reports.NextMissingMetadataAsync(c, default), Is.EqualTo(new MissingImageSuccessor(a, false)), "wraps");
        Assert.That(await _reports.NextMissingMetadataAsync(Guid.NewGuid(), default), Is.EqualTo(new MissingImageSuccessor(a, false)));
    }

    [Test]
    public async Task Next_CurrentIsOnly_AndNone()
    {
        Assert.That(await _reports.NextMissingMetadataAsync(null, default), Is.EqualTo(new MissingImageSuccessor(null, false)));
        var only = await Add("Only", year: null);
        Assert.That(await _reports.NextMissingMetadataAsync(only, default), Is.EqualTo(new MissingImageSuccessor(null, true)));
        var complete = await Add("Complete");
        Assert.That(await _reports.NextMissingMetadataAsync(complete, default), Is.EqualTo(new MissingImageSuccessor(only, false)));
    }
}
