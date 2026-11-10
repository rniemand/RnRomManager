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
        string? displayTitle = null, int? rating = null, bool favourite = false, string[]? developers = null,
        string[]? publishers = null, string[]? genres = null, params string[][] releases)
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
            Rating = rating,
            Favourite = favourite,
            Developers = developers ?? [],
            Publishers = publishers ?? [],
            Genres = genres ?? [],
            Images = boxArt
                ? [new InfoJsonImage { FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = InfoJsonImage.SourceDefault, IsDefault = true, IsCover = true, SizeBytes = 10, Sha1 = new string('a', 40), Width = 512, Height = 512, DateAdded = When }]
                : [],
            Metadata = new InfoJsonGameMetadata(created, created, "0.1.0"),
        };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await _mirror.ReplaceAsync(new MirrorEntry(game, $"{system}/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    /// <summary>Replaces <paramref name="id"/>'s images: a real or default box art (or none) and optionally a snap.</summary>
    private async Task SetImages(Guid id, bool? defaultBoxArt, bool snap)
    {
        var row = (await _games.GetAsync(id, default))!;
        static InfoJsonImage Image(ImageType type, bool isDefault) => new()
        {
            FileName = type == ImageType.BoxArt ? "boxart-01.png" : "snap-01.png", Type = type,
            Source = isDefault ? InfoJsonImage.SourceDefault : InfoJsonImage.SourceLibretro, IsDefault = isDefault, IsCover = type == ImageType.BoxArt,
            SizeBytes = 10, Sha1 = new string('b', 40), Width = 1, Height = 1, DateAdded = When,
        };
        var roms = (await _roms.ListByGameAsync(id, default)).Select((r, i) => Rom(r.FileName, RomBytes(1000 + ++_seed), isPrimary: i == 0)).ToArray();
        var game = Game(row.Title, roms) with
        {
            GameId = id,
            Images = [.. defaultBoxArt is { } isDefault ? [Image(ImageType.BoxArt, isDefault)] : Array.Empty<InfoJsonImage>(),
                .. snap ? [Image(ImageType.Snap, false)] : Array.Empty<InfoJsonImage>()],
        };
        await _mirror.ReplaceAsync(new MirrorEntry(game, row.ZipPath, 100, When.UtcDateTime), default);
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
    public async Task Search_EveryWordMustMatch_InAnyOrder_EachWordEscaped()
    {
        await Add("Legend of Zelda, The - Link's Awakening", displayTitle: "The Legend of Zelda - Link's Awakening");
        await Add("Legend of Zelda, The", displayTitle: "The Legend of Zelda");
        await Add("100% Pure Zelda");
        await Add("1000 Pure Zelda");

        Assert.That(await Titles(new GameQuery { Search = "awakening zelda" }), Is.EqualTo(new[] { "The Legend of Zelda - Link's Awakening" }));
        Assert.That(await Titles(new GameQuery { Search = "zelda legend" }),
            Is.EqualTo(new[] { "The Legend of Zelda", "The Legend of Zelda - Link's Awakening" }));
        Assert.That(await Titles(new GameQuery { Search = "zelda 0%" }), Is.EqualTo(new[] { "100% Pure Zelda" }));
        Assert.That(await Titles(new GameQuery { Search = "zelda missing" }), Is.Empty);
    }

    [Test]
    public async Task Sort_Relevance_ExactThenPrefixThenRest_OnEitherTitle()
    {
        await Add("Quest, The", displayTitle: "The Quest");
        await Add("Quest for Glory");
        await Add("Alpha Quest");
        await Add("The Quest Returns");
        await Add("Zeta");

        Assert.That(await Titles(new GameQuery { Search = "the quest", Sort = GameSort.Relevance }),
            Is.EqualTo(new[] { "The Quest", "The Quest Returns" }), "display_title exact, then prefix");
        Assert.That(await Titles(new GameQuery { Search = "Quest, The", Sort = GameSort.Relevance }), Is.EqualTo(new[] { "The Quest" }), "title exact");
        Assert.That(await Titles(new GameQuery { Search = "  quest  ", Sort = GameSort.Relevance, Descending = true }),
            Is.EqualTo(new[] { "Quest for Glory", "The Quest", "Alpha Quest", "The Quest Returns" }),
            "prefix on title (Quest for Glory / Quest, The: the collation ignores the comma), then the rest by title; dir ignored");
        Assert.That(await Titles(new GameQuery { Search = "the   quest", Sort = GameSort.Relevance }), Is.EqualTo(new[] { "The Quest", "The Quest Returns" }),
            "whitespace collapsed");
        Assert.That(await Titles(new GameQuery { Sort = GameSort.Relevance }),
            Is.EqualTo(new[] { "Alpha Quest", "Quest for Glory", "The Quest", "The Quest Returns", "Zeta" }), "no q: by title");
    }

    [Test]
    public async Task Sort_Rating_UnratedLastInBothDirections_TiesByTitle()
    {
        await Add("Unrated");
        await Add("Bee", rating: 3);
        await Add("Ant", rating: 3);
        await Add("Top", rating: 5);

        Assert.That(await Titles(new GameQuery { Sort = GameSort.Rating, Descending = true }), Is.EqualTo(new[] { "Top", "Ant", "Bee", "Unrated" }));
        Assert.That(await Titles(new GameQuery { Sort = GameSort.Rating }), Is.EqualTo(new[] { "Ant", "Bee", "Top", "Unrated" }));
    }

    private async Task<GameFacets> Facets(GameQuery query) => await _games.ListFacetsAsync(query, default);

    [Test]
    public async Task Facets_EachIgnoresItsOwnFilter_AndNarrowsTheOthers()
    {
        await Add("Tetris", releases: [["USA", "Europe"], ["Japan"]]);
        await Add("Tennis", releases: [["USA"]]);
        await Add("Alleyway", favourite: true, developers: ["Nintendo"], releases: [["Japan"]]);
        await Add("Toki", system: "gbc", releases: [["Europe"]]);

        var all = await Facets(new GameQuery());
        Assert.That(all.Systems, Is.EqualTo(new Dictionary<string, int> { ["gb"] = 3, ["gbc"] = 1 }));
        Assert.That(all.Letters, Is.EqualTo(new Dictionary<string, int> { ["A"] = 1, ["T"] = 3 }));
        Assert.That(all.Regions, Is.EqualTo(new Dictionary<string, int> { ["Europe"] = 2, ["Japan"] = 2, ["USA"] = 2 }), "a multi-region game counts once per region");

        var gb = await Facets(new GameQuery { System = "gb" });
        Assert.That(gb.Systems, Is.EqualTo(all.Systems));
        Assert.That(gb.Letters, Is.EqualTo(new Dictionary<string, int> { ["A"] = 1, ["T"] = 2 }));
        Assert.That(gb.Regions, Is.EqualTo(new Dictionary<string, int> { ["Europe"] = 1, ["Japan"] = 2, ["USA"] = 2 }));

        var usa = await Facets(new GameQuery { Region = "USA" });
        Assert.That(usa.Regions, Is.EqualTo(all.Regions));
        Assert.That(usa.Systems, Is.EqualTo(new Dictionary<string, int> { ["gb"] = 2 }), "zero counts are absent");
        Assert.That(usa.Letters, Is.EqualTo(new Dictionary<string, int> { ["T"] = 2 }));

        var a = await Facets(new GameQuery { Letter = "A" });
        Assert.That(a.Letters, Is.EqualTo(all.Letters));
        Assert.That((a.Systems, a.Regions), Is.EqualTo((new Dictionary<string, int> { ["gb"] = 1 }, new Dictionary<string, int> { ["Japan"] = 1 })));

        var search = await Facets(new GameQuery { Search = "nintendo" });
        Assert.That((search.Systems, search.Letters), Is.EqualTo((new Dictionary<string, int> { ["gb"] = 1 }, new Dictionary<string, int> { ["A"] = 1 })));

        var favourite = await Facets(new GameQuery { Favourite = true, System = "gbc" });
        Assert.That(favourite.Systems, Is.EqualTo(new Dictionary<string, int> { ["gb"] = 1 }));
        Assert.That((favourite.Letters, favourite.Regions), Is.EqualTo((new Dictionary<string, int>(), new Dictionary<string, int>())));
    }

    [Test]
    public async Task Favourite_FiltersTrueOrFalse()
    {
        await Add("Loved", favourite: true);
        await Add("Plain");

        Assert.That(await Titles(new GameQuery { Favourite = true }), Is.EqualTo(new[] { "Loved" }));
        Assert.That(await Titles(new GameQuery { Favourite = false }), Is.EqualTo(new[] { "Plain" }));
        Assert.That(await Titles(new GameQuery()), Is.EqualTo(new[] { "Loved", "Plain" }));
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

    [Test]
    public async Task MediaRetryCandidates_DefaultOrMissingBoxArtOrNoSnap_OrderedByTitle()
    {
        await SetImages(await Add("Complete"), defaultBoxArt: false, snap: true);
        await SetImages(await Add("Default Art"), defaultBoxArt: true, snap: true);
        await SetImages(await Add("Bare"), defaultBoxArt: null, snap: true);
        await SetImages(await Add("Real No Snap"), defaultBoxArt: false, snap: false);
        await Add("Alpha Default");

        var candidates = await _games.ListMediaRetryCandidatesAsync(default);

        Assert.That(candidates.Select(c => c.Title), Is.EqualTo(new[] { "Alpha Default", "Bare", "Default Art", "Real No Snap" }));
    }

    [Test]
    public async Task Search_MatchesDevelopersPublishersAndGenres_IgnoringCase()
    {
        await Add("Alpha", developers: ["Bullet-Proof Software"]);
        await Add("Beta", publishers: ["Nintendo"]);
        await Add("Gamma", genres: ["Puzzle"]);
        await Add("Delta");

        Assert.That(await Titles(new GameQuery { Search = "bullet" }), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(await Titles(new GameQuery { Search = "nintendo" }), Is.EqualTo(new[] { "Beta" }), "case-insensitive on a JSON column");
        Assert.That(await Titles(new GameQuery { Search = "PUZZLE" }), Is.EqualTo(new[] { "Gamma" }));
    }

    [Test]
    public async Task Search_EveryWordMustMatchSomeField()
    {
        await Add("Tetris", publishers: ["Nintendo"]);
        await Add("Tetris Plus", publishers: ["Jaleco"]);

        Assert.That(await Titles(new GameQuery { Search = "tetris nintendo" }), Is.EqualTo(new[] { "Tetris" }));
        Assert.That(await Titles(new GameQuery { Search = "tetris sega" }), Is.Empty);
    }

    [Test]
    public async Task Sort_Relevance_ScoresTitleExactPrefixWordThenDeveloperPublisherThenGenre()
    {
        await Add("Mario", genres: ["Mario"]);                 // exact 100 + prefix 60 + title 40 + genre 10
        await Add("Mario Land");                               // prefix 60 + title 40
        await Add("Super Mario");                              // title 40
        await Add("Kart", developers: ["Mario Team"]);         // developer 20
        await Add("Bros", publishers: ["Mario Publishing"]);   // publisher 20
        await Add("Party", genres: ["Mario-like"]);            // genre 10

        Assert.That(await Titles(new GameQuery { Search = "mario", Sort = GameSort.Relevance }),
            Is.EqualTo(new[] { "Mario", "Mario Land", "Super Mario", "Bros", "Kart", "Party" }), "equal scores by title");
    }

    [Test]
    public async Task OtherSorts_AreUnchanged_OverTheWiderMatchSet()
    {
        await Add("Zeta", publishers: ["Acme"]);
        await Add("Acme Racer");
        await Add("Other");

        Assert.That(await Titles(new GameQuery { Search = "acme" }), Is.EqualTo(new[] { "Acme Racer", "Zeta" }));
        Assert.That(await Titles(new GameQuery { Search = "acme", Descending = true }), Is.EqualTo(new[] { "Zeta", "Acme Racer" }));
    }

    [Test]
    public async Task ListItem_MatchedOn_OnlyWhenNoWordIsInTheTitle()
    {
        await Add("Tetris", developers: ["Bullet-Proof"], publishers: ["Nintendo"]);

        Assert.That((await _games.ListAsync(new GameQuery { Search = "nintendo" }, default)).Items.Single().MatchedOn,
            Is.EqualTo(new SearchMatch("publisher", "Nintendo")));
        Assert.That((await _games.ListAsync(new GameQuery { Search = "tetris nintendo" }, default)).Items.Single().MatchedOn, Is.Null);
        Assert.That((await _games.ListAsync(new GameQuery(), default)).Items.Single().MatchedOn, Is.Null);
    }
}
