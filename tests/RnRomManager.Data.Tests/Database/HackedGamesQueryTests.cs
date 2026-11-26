using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Reports;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Release editing B13: the Games With Hacks query, against MariaDB.</summary>
[Category("Integration")]
public class HackedGamesQueryTests
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

    private async Task<Guid> Add(
        string title,
        string system,
        long zipSize,
        params string[][] flags)
    {
        var roms = flags.Select((f, i) => Rom($"{title} {i}.gb", RomBytes(++_seed), isPrimary: i == 0) with { Flags = f }).ToArray();
        var game = Game(title, roms) with { System = system };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await ((ILibraryMirror)new LibraryMirror(_factory)).ReplaceAsync(
            new MirrorEntry(game, $"{system}/{Bucket.For(name)}/{name}", zipSize, When.UtcDateTime), default);
        return game.GameId;
    }

    private static readonly string[] Plain = [];
    private static readonly string[] Hack = ["Hack"];
    private static readonly string[] HackUpdate = ["Hack", "Update"];
    private static readonly string[] Dlc = ["DLC"];

    private Task<Core.Database.Page<HackedGameItem>> List(
        HackedGameSort sort = HackedGameSort.Hacks,
        bool descending = true,
        string? system = null,
        int page = 1,
        int pageSize = 50) =>
        _reports.ListHackedGamesAsync(system, sort, descending, page, pageSize, default);

    [Test]
    public async Task CountsHacksAndReleases_UpdatesAndDlcAreNotReleases_UnhackedGamesAreLeftOut()
    {
        var alpha = await Add("Alpha", "gb", 100, Plain, Hack, HackUpdate, Dlc);
        await Add("Clean", "gb", 100, Plain, Plain);

        var page = await List();

        Assert.That(page.Items.Select(i => (i.Id, i.DisplayTitle, i.SystemId, i.HackCount, i.ReleaseCount, i.Bytes)),
            Is.EqualTo(new[] { (alpha, "Alpha", "gb", 2, 2, 100L) }));
        Assert.That(page.Total, Is.EqualTo(1));
    }

    [Test]
    public async Task EachSortKey_BothDirections_TiesByTitleThenId()
    {
        var a = await Add("Alpha", "nes", 300, Hack, Plain, Plain);
        var b = await Add("Bravo", "gb", 100, Hack, Hack);
        var c = await Add("Charlie", "gb", 200, Hack);

        async Task<Guid[]> Ids(HackedGameSort sort, bool descending) => [.. (await List(sort, descending)).Items.Select(i => i.Id)];

        Assert.Multiple(async () =>
        {
            Assert.That(await Ids(HackedGameSort.Name, false), Is.EqualTo(new[] { a, b, c }));
            Assert.That(await Ids(HackedGameSort.Name, true), Is.EqualTo(new[] { c, b, a }));
            Assert.That(await Ids(HackedGameSort.System, false), Is.EqualTo(new[] { b, c, a }));
            Assert.That(await Ids(HackedGameSort.System, true), Is.EqualTo(new[] { a, b, c }));
            Assert.That(await Ids(HackedGameSort.Hacks, true), Is.EqualTo(new[] { b, a, c }));
            Assert.That(await Ids(HackedGameSort.Hacks, false), Is.EqualTo(new[] { a, c, b }));
            Assert.That(await Ids(HackedGameSort.Releases, true), Is.EqualTo(new[] { a, b, c }));
            Assert.That(await Ids(HackedGameSort.Releases, false), Is.EqualTo(new[] { c, b, a }));
            Assert.That(await Ids(HackedGameSort.Size, true), Is.EqualTo(new[] { a, c, b }));
            Assert.That(await Ids(HackedGameSort.Size, false), Is.EqualTo(new[] { b, c, a }));
        });
    }

    [Test]
    public async Task SystemFilter_AndPaging()
    {
        await Add("Alpha", "nes", 100, Hack);
        var b = await Add("Bravo", "gb", 100, Hack);
        var c = await Add("Charlie", "gb", 100, Hack);

        var gb = await List(HackedGameSort.Name, false, "gb");
        var second = await List(HackedGameSort.Name, false, page: 2, pageSize: 2);

        Assert.That((gb.Items.Select(i => i.Id), gb.Total), Is.EqualTo((new[] { b, c }, 2L)));
        Assert.That((second.Items.Select(i => i.Id), second.Total), Is.EqualTo((new[] { c }, 3L)));
    }
}
