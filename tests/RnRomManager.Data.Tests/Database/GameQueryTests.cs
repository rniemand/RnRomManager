using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Spec §23 <c>/api/games</c> + <c>/api/systems</c> queries and §14.2 search escaping (S32), against MariaDB.</summary>
[Category("Integration")]
public class GameQueryTests
{
    private ILibraryMirror _mirror = null!;
    private GameRepository _games = null!;
    private RomRepository _roms = null!;
    private ImageRepository _images = null!;
    private int _seed;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _mirror = new LibraryMirror(factory);
        _games = new GameRepository(factory);
        _roms = new RomRepository(factory);
        _images = new ImageRepository(factory);
        _seed = 0;
    }

    /// <summary>Inserts a game whose releases have the given region lists; <paramref name="addedMinutes"/> orders <c>created_at</c>.</summary>
    private async Task<Guid> Add(string title, int? year = null, int addedMinutes = 0, string system = "gb", bool boxArt = true,
        string? displayTitle = null, params string[][] releases)
    {
        if (releases.Length == 0)
            releases = [["World"]];
        var roms = releases.Select((regions, i) => Rom($"{title} {i}.gb", RomBytes(++_seed), isPrimary: i == 0, regions: regions)).ToArray();
        var created = When.AddMinutes(addedMinutes);
        var game = Game(title, roms) with
        {
            System = system,
            DisplayTitle = displayTitle ?? title,
            ReleaseYear = year,
            Images = boxArt
                ? [new InfoJsonImage { FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = InfoJsonImage.SourceDefault, IsDefault = true, SizeBytes = 10, Sha1 = new string('a', 40), Width = 512, Height = 512, DateAdded = When }]
                : [],
            Metadata = new InfoJsonGameMetadata(created, created, "0.1.0"),
        };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await _mirror.ReplaceAsync(new MirrorEntry(game, $"{system}/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    private async Task<IReadOnlyList<string>> Titles(GameQuery query) =>
        (await _games.ListAsync(query, default)).Items.Select(i => i.DisplayTitle).ToList();

    [Test]
    public async Task Filters_BySystemLetterAndHashBucket()
    {
        await Add("Tetris");
        await Add("Tennis");
        await Add("4 in 1");
        await Add("Toki", system: "gbc");

        Assert.That(await Titles(new GameQuery { System = "gb" }), Is.EqualTo(new[] { "4 in 1", "Tennis", "Tetris" }));
        Assert.That(await Titles(new GameQuery { Letter = "T" }), Is.EqualTo(new[] { "Tennis", "Tetris", "Toki" }));
        Assert.That(await Titles(new GameQuery { Letter = "#" }), Is.EqualTo(new[] { "4 in 1" }));
        Assert.That(await Titles(new GameQuery { System = "gbc", Letter = "T" }), Is.EqualTo(new[] { "Toki" }));
    }

    [Test]
    public async Task Region_MatchesAnyRelease_WholeRegionsOnly()
    {
        await Add("Both", releases: [["Japan"], ["USA", "Europe"]]);
        await Add("Japan Only", releases: [["Japan"]]);
        await Add("None", releases: [[]]);

        Assert.That(await Titles(new GameQuery { Region = "Europe" }), Is.EqualTo(new[] { "Both" }));
        Assert.That(await Titles(new GameQuery { Region = "Japan" }), Is.EqualTo(new[] { "Both", "Japan Only" }));
        Assert.That(await Titles(new GameQuery { Region = "US" }), Is.Empty, "a partial region doesn't match");
        Assert.That(await Titles(new GameQuery { Region = "%" }), Is.Empty, "LIKE wildcards are escaped");
    }

    [Test]
    public async Task Search_MatchesDisplayTitleOrTitle_AndEscapesWildcards()
    {
        await Add("Legend of Zelda, The", displayTitle: "The Legend of Zelda");
        await Add("100% Pure");
        await Add("1000 Pure");
        await Add("A_B");
        await Add("AxB");
        await Add(@"Back\slash");

        Assert.That(await Titles(new GameQuery { Search = "the legend" }), Is.EqualTo(new[] { "The Legend of Zelda" }), "display_title");
        Assert.That(await Titles(new GameQuery { Search = "Zelda, The" }), Is.EqualTo(new[] { "The Legend of Zelda" }), "title");
        Assert.That(await Titles(new GameQuery { Search = "0%" }), Is.EqualTo(new[] { "100% Pure" }));
        Assert.That(await Titles(new GameQuery { Search = "A_B" }), Is.EqualTo(new[] { "A_B" }));
        Assert.That(await Titles(new GameQuery { Search = @"k\s" }), Is.EqualTo(new[] { @"Back\slash" }));
    }

    [Test]
    public async Task Sort_TitleAndAdded_BothDirections_TieBreakId()
    {
        var a1 = await Add("Same", addedMinutes: 2);
        var a2 = await Add("Same", addedMinutes: 2, displayTitle: "Same ", system: "gbc");
        await Add("Alpha", addedMinutes: 1);
        await Add("Zulu", addedMinutes: 3);
        var (first, second) = string.CompareOrdinal(a1.ToString(), a2.ToString()) < 0 ? ("Same", "Same ") : ("Same ", "Same");

        Assert.That(await Titles(new GameQuery()), Is.EqualTo(new[] { "Alpha", first, second, "Zulu" }));
        Assert.That(await Titles(new GameQuery { Descending = true }), Is.EqualTo(new[] { "Zulu", first, second, "Alpha" }), "id stays ascending");
        Assert.That(await Titles(new GameQuery { Sort = GameSort.Added, Descending = true }), Is.EqualTo(new[] { "Zulu", first, second, "Alpha" }));
        Assert.That(await Titles(new GameQuery { Sort = GameSort.Added }), Is.EqualTo(new[] { "Alpha", first, second, "Zulu" }));
    }

    [Test]
    public async Task Sort_Year_NullsLastInBothDirections()
    {
        await Add("Unknown");
        await Add("Old", year: 1989);
        await Add("New", year: 1998);

        Assert.That(await Titles(new GameQuery { Sort = GameSort.Year }), Is.EqualTo(new[] { "Old", "New", "Unknown" }));
        Assert.That(await Titles(new GameQuery { Sort = GameSort.Year, Descending = true }), Is.EqualTo(new[] { "New", "Old", "Unknown" }));
    }

    [Test]
    public async Task Paging_ReturnsSliceAndTotal_PastTheEndIsEmpty()
    {
        foreach (var title in new[] { "A", "B", "C", "D", "E" })
            await Add(title);

        var page = await _games.ListAsync(new GameQuery { Page = 2, PageSize = 2 }, default);
        Assert.That(page.Items.Select(i => i.DisplayTitle), Is.EqualTo(new[] { "C", "D" }));
        Assert.That((page.PageNumber, page.PageSize, page.Total), Is.EqualTo((2, 2, 5L)));

        var past = await _games.ListAsync(new GameQuery { Page = 4, PageSize = 2 }, default);
        Assert.That((past.Items.Count, past.Total), Is.EqualTo((0, 5L)));
    }

    [Test]
    public async Task ListItem_HasDistinctSortedRegions_RomCount_AndBoxArtId()
    {
        var id = await Add("Tetris", year: 1989, releases: [["World"], ["USA", "Europe"], ["Europe"]]);
        await Add("No Art", boxArt: false);

        var items = (await _games.ListAsync(new GameQuery(), default)).Items;
        var noArt = items.Single(i => i.DisplayTitle == "No Art");
        var tetris = items.Single(i => i.Id == id);
        var image = (await _images.ListByGameAsync(id, default)).Single();
        Assert.That(tetris.Regions, Is.EqualTo(new[] { "Europe", "USA", "World" }));
        Assert.That((tetris.SystemId, tetris.ReleaseYear, tetris.RomCount, tetris.BoxArtImageId), Is.EqualTo(("gb", (int?)1989, 3, (long?)image.Id)));
        Assert.That(noArt.BoxArtImageId, Is.Null);
    }

    [Test]
    public async Task SystemStats_CountGamesRomsAndDistinctRegions()
    {
        await Add("Tetris", releases: [["World"], ["Japan"]]);
        await Add("Tennis", releases: [["Japan", "USA"]]);
        await Add("Toki", system: "gbc", releases: [[]]);

        var stats = await _games.ListSystemStatsAsync(default);

        Assert.That(stats.Select(s => (s.SystemId, s.GameCount, s.RomCount)), Is.EqualTo(new[] { ("gb", 2, 3), ("gbc", 1, 1) }));
        Assert.That(stats[0].Regions, Is.EqualTo(new[] { "Japan", "USA", "World" }));
        Assert.That(stats[1].Regions, Is.Empty);
    }

    [Test]
    public async Task GetRomAndImage_ByGame_OtherGamesIdIsNull()
    {
        var a = await Add("Alpha");
        var b = await Add("Beta");
        var rom = (await _roms.ListByGameAsync(a, default)).Single();
        var image = (await _images.ListByGameAsync(a, default)).Single();

        Assert.That((await _roms.GetAsync(a, rom.Id, default))?.Sha1, Is.EqualTo(rom.Sha1));
        Assert.That((await _images.GetAsync(a, image.Id, default))?.FileName, Is.EqualTo("boxart-01.png"));
        Assert.That(await _roms.GetAsync(b, rom.Id, default), Is.Null);
        Assert.That(await _images.GetAsync(b, image.Id, default), Is.Null);
    }
}
