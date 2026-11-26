using System.Diagnostics;
using Dapper;
using MySqlConnector;
using RnRomManager.Core.Database;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Data.Repositories;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>B2 selected-group game pages, group lookups and the index lifetime.</summary>
public partial class DiscoveryRepositoryTests
{
    private int _seed;

    private ILibraryMirror Mirror => new LibraryMirror(_factory);

    private GameRepository Games => new(_factory);

    /// <summary>Mirrors a real game (release, cover) so the page goes through the normal <see cref="GameListItem"/> projection.</summary>
    private async Task<InfoJsonGame> MirrorAsync(
        string title,
        string system = "gb",
        string? displayTitle = null,
        string[]? publishers = null,
        string[]? genres = null)
    {
        var game = Game(title, Rom($"{title} {system}.gb", RomBytes(++_seed))) with
        {
            System = system,
            DisplayTitle = displayTitle ?? title,
            Publishers = publishers ?? [],
            Genres = genres ?? [],
        };
        await ReplaceAsync(game);
        return game;
    }

    private async Task ReplaceAsync(InfoJsonGame game)
    {
        var name = FileNameSanitizer.Sanitize(game.Title) + ".zip";
        await Mirror.ReplaceAsync(new MirrorEntry(game, $"{game.System}/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
    }

    private Task<Page<GameListItem>?> SelectAsync(
        DiscoveryKind kind,
        string key,
        int page = 1,
        int pageSize = 48) =>
        _repository.ListGamesAsync(kind, key, page, pageSize, default);

    private async Task<List<string>?> SelectedTitlesAsync(DiscoveryKind kind, string key) =>
        (await SelectAsync(kind, key))?.Items.Select(g => $"{g.DisplayTitle} ({g.SystemId})").ToList();

    private void Expire() => _time.Advance(IDiscoveryRepository.IndexLifetime);

    [Test]
    public async Task Selection_IsExact_AcrossSystems_SortedByDisplayTitleSystemAndId()
    {
        await MirrorAsync("Tetris", "gb", publishers: ["Nintendo"]);
        await MirrorAsync("Tetris DX", "gbc", displayTitle: "Tetris", publishers: [" NINTENDO\t"]);
        await MirrorAsync("Mario", "nes", publishers: ["nintendo", "Nintendo", "Hudson"]);
        await MirrorAsync("Zelda", "snes", publishers: ["Nintendo of America"]);
        await MirrorAsync("Sonic", "megadrive", publishers: ["Sega"]);

        var page = await SelectAsync(DiscoveryKind.Publishers, Key("nintendo"));

        Assert.That(page!.Items.Select(g => $"{g.DisplayTitle} ({g.SystemId})"),
            Is.EqualTo(new[] { "Mario (nes)", "Tetris (gb)", "Tetris (gbc)" }), "each matched game once, other values excluded");
        Assert.That((page.PageNumber, page.PageSize, page.Total), Is.EqualTo((1, 48, 3L)));
    }

    [Test]
    public async Task Selection_IsCaseInsensitive_ButAccentSensitive()
    {
        await MirrorAsync("A", publishers: ["Café"]);
        await MirrorAsync("B", publishers: ["CAFÉ"]);
        await MirrorAsync("C", publishers: ["Cafe"]);

        Assert.That(await SelectedTitlesAsync(DiscoveryKind.Publishers, Key("café")), Is.EqualTo(new[] { "A (gb)", "B (gb)" }));
        Assert.That(await SelectedTitlesAsync(DiscoveryKind.Publishers, Key("cafe")), Is.EqualTo(new[] { "C (gb)" }));
    }

    [Test]
    public async Task Selection_UsesTheKindsOwnColumn()
    {
        await MirrorAsync("A", publishers: ["Action"], genres: ["Puzzle"]);
        await MirrorAsync("B", publishers: ["Puzzle"], genres: ["Action"]);

        Assert.That(await SelectedTitlesAsync(DiscoveryKind.Genres, Key("action")), Is.EqualTo(new[] { "B (gb)" }));
        Assert.That(await SelectedTitlesAsync(DiscoveryKind.Publishers, Key("action")), Is.EqualTo(new[] { "A (gb)" }));
    }

    [Test]
    public async Task UnknownGroup_IsNull_IncludingTheEmptyStringsKey()
    {
        await MirrorAsync("A", publishers: ["", "   ", "Nintendo"]);

        Assert.That(await SelectAsync(DiscoveryKind.Publishers, Key("")), Is.Null, "blank entries are no group");
        Assert.That(await _repository.GetGroupAsync(DiscoveryKind.Publishers, Key(""), default), Is.Null);
        Assert.That(await SelectAsync(DiscoveryKind.Publishers, Key("sega")), Is.Null);
        Assert.That(await SelectAsync(DiscoveryKind.Genres, Key("nintendo")), Is.Null);
    }

    [Test]
    public async Task Selection_ProjectsTheSameRowsAsTheLibraryList()
    {
        await MirrorAsync("Tetris", "gb", publishers: ["Nintendo"]);
        await MirrorAsync("Mario", "nes", publishers: ["Nintendo"]);

        var selected = (await SelectAsync(DiscoveryKind.Publishers, Key("nintendo")))!.Items;
        var library = (await Games.ListAsync(new GameQuery(), default)).Items;

        Assert.That(selected, Is.EquivalentTo(library).Using<GameListItem>((a, b) =>
            a.Id == b.Id && a.SystemId == b.SystemId && a.RomCount == b.RomCount && a.Regions.SequenceEqual(b.Regions)
            && a.BoxArtImageId == b.BoxArtImageId && a.PrimarySizeBytes == b.PrimarySizeBytes && a.PrimaryRomId == b.PrimaryRomId));
    }

    [Test]
    public async Task Selection_Pages_WithAStableOrder()
    {
        for (var i = 0; i < 7; i++)
            await MirrorAsync($"Game {i}", i % 2 == 0 ? "gb" : "nes", displayTitle: "Same title", publishers: ["Konami"]);

        var pages = new List<GameListItem>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await SelectAsync(DiscoveryKind.Publishers, Key("konami"), page, 3);
            Assert.That(result!.Total, Is.EqualTo(7));
            Assert.That(result.Items, Has.Count.EqualTo(page switch { 3 => 1, 4 => 0, _ => 3 }));
            pages.AddRange(result.Items);
        }

        Assert.That(pages.Select(g => g.Id).Distinct().Count(), Is.EqualTo(7));
        Assert.That(pages.Select(g => g.Id), Is.EqualTo(pages
            .OrderBy(g => g.SystemId, StringComparer.Ordinal).ThenBy(g => g.Id.ToString(), StringComparer.Ordinal).Select(g => g.Id)),
            "equal display titles fall back to system, then id");
    }

    [Test]
    public async Task RenameAndRemoval_ShowOnceTheIndexExpires()
    {
        var tetris = await MirrorAsync("Tetris", "gb", publishers: ["Nintendo"]);
        var mario = await MirrorAsync("Mario", "nes", publishers: ["Nintendo"]);
        Assert.That(await SelectedTitlesAsync(DiscoveryKind.Publishers, Key("nintendo")), Is.EqualTo(new[] { "Mario (nes)", "Tetris (gb)" }));

        await ReplaceAsync(tetris with { Publishers = ["Bullet-Proof Software"] });
        _time.Advance(IDiscoveryRepository.IndexLifetime - TimeSpan.FromMilliseconds(1));
        Assert.That(await _repository.GetGroupAsync(DiscoveryKind.Publishers, Key("bullet-proof software"), default), Is.Null,
            "the index is reused within its lifetime");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That(await SelectedTitlesAsync(DiscoveryKind.Publishers, Key("nintendo")), Is.EqualTo(new[] { "Mario (nes)" }));
        Assert.That(await SelectedTitlesAsync(DiscoveryKind.Publishers, Key("bullet-proof software")), Is.EqualTo(new[] { "Tetris (gb)" }));
        Assert.That(await _repository.GetGroupAsync(DiscoveryKind.Publishers, Key("nintendo"), default),
            Is.EqualTo(new DiscoveryGroup(Key("nintendo"), "Nintendo", 1)));

        await Mirror.DeleteAsync(mario.GameId, default);
        var stale = await SelectAsync(DiscoveryKind.Publishers, Key("nintendo"));
        Assert.That(stale!.Items, Is.Empty, "a deleted game drops out of its page at once");
        Assert.That(stale.Total, Is.EqualTo(1), "the count follows the index");

        Expire();
        Assert.That(await SelectAsync(DiscoveryKind.Publishers, Key("nintendo")), Is.Null, "a selection that disappeared");
        Assert.That(await _repository.GetGroupAsync(DiscoveryKind.Publishers, Key("nintendo"), default), Is.Null);
    }

    [Test]
    public async Task Invalidate_DropsEveryCachedIndex_AtOnce()
    {
        await MirrorAsync("A", publishers: ["Nintendo"], genres: ["Puzzle"]);
        Assert.That((await AllAsync(DiscoveryKind.Publishers)).Select(g => g.Name), Is.EqualTo(new[] { "Nintendo" }));
        Assert.That((await AllAsync(DiscoveryKind.Genres)).Select(g => g.Name), Is.EqualTo(new[] { "Puzzle" }));
        await MirrorAsync("B", publishers: ["Sega"], genres: ["Action"]);

        _repository.Invalidate();

        Assert.That((await AllAsync(DiscoveryKind.Publishers)).Select(g => g.Name), Is.EqualTo(new[] { "Nintendo", "Sega" }), "within the lifetime");
        Assert.That((await AllAsync(DiscoveryKind.Genres)).Select(g => g.Name), Is.EqualTo(new[] { "Action", "Puzzle" }));
    }

    [Test]
    public async Task Invalidate_DiscardsAnInFlightBuildsResult()
    {
        await MirrorAsync("A", publishers: ["Nintendo"]);
        var inFlight = AllAsync(DiscoveryKind.Publishers); // may or may not see B: it was started first

        _repository.Invalidate();
        await MirrorAsync("B", publishers: ["Sega"]);
        await inFlight;

        Assert.That((await AllAsync(DiscoveryKind.Publishers)).Select(g => g.Name), Is.EqualTo(new[] { "Nintendo", "Sega" }),
            "the next read builds again instead of reusing the in-flight result");
    }

    [Test]
    public async Task Kinds_HaveSeparateIndexes()
    {
        await MirrorAsync("A", publishers: ["Nintendo"], genres: ["Puzzle"]);
        Assert.That((await AllAsync(DiscoveryKind.Publishers)).Select(g => g.Name), Is.EqualTo(new[] { "Nintendo" }));

        _time.Advance(TimeSpan.FromSeconds(30));
        await MirrorAsync("B", publishers: ["Sega"], genres: ["Action"]);
        Assert.That((await AllAsync(DiscoveryKind.Genres)).Select(g => g.Name), Is.EqualTo(new[] { "Action", "Puzzle" }), "built now");

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.That((await AllAsync(DiscoveryKind.Publishers)).Select(g => g.Name), Is.EqualTo(new[] { "Nintendo", "Sega" }), "expired");
        Assert.That((await AllAsync(DiscoveryKind.Genres)).Select(g => g.Name), Is.EqualTo(new[] { "Action", "Puzzle" }));
    }

    [Test]
    public async Task ConcurrentCallers_ShareOneBuild_AndACancelledCallerDoesNotCancelIt()
    {
        await MirrorAsync("A", publishers: ["Nintendo"]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var first = _repository.ListGroupsAsync(DiscoveryKind.Publishers, 1, 48, null, DiscoveryGroupSort.Name, false, cancelled.Token);
        var others = Enumerable.Range(0, 5).Select(_ => _repository.ListGroupsAsync(DiscoveryKind.Publishers, 1, 48, null, DiscoveryGroupSort.Name, false, default)).ToList();

        Assert.That(async () => await first, Throws.InstanceOf<OperationCanceledException>());
        foreach (var other in others)
            Assert.That((await other).Items.Single().Name, Is.EqualTo("Nintendo"));
    }

    [Test]
    public async Task GetGroup_MatchesItsListEntry()
    {
        await MirrorAsync("A", publishers: ["Square", "square "]);
        await MirrorAsync("B", publishers: ["SQUARE"]);
        await MirrorAsync("C", publishers: ["Enix"]);

        var listed = (await _repository.ListGroupsAsync(DiscoveryKind.Publishers, 1, 48, null, DiscoveryGroupSort.Name, false, default)).Items;

        foreach (var group in listed)
            Assert.That(await _repository.GetGroupAsync(DiscoveryKind.Publishers, group.Key, default), Is.EqualTo(group));
        Assert.That(listed.Single(g => g.Key == Key("square")), Is.EqualTo(new DiscoveryGroup(Key("square"), "SQUARE", 2)));
        Assert.That(await _repository.GetGroupAsync(DiscoveryKind.Genres, Key("square"), default), Is.Null);
    }

    /// <summary>
    /// The Risks budget: with 50,000 synthetic games, a group page and a group's game page each take under 500 ms
    /// (median of 5 warm runs). Explicit because seeding takes a while. A live scan took 0.7–1.5 s here, hence the
    /// cached index; the cold build is printed for reference.
    /// </summary>
    [Test, Explicit("Timing: seeds 50,000 games")]
    public async Task Budget_50000Games_PagesUnder500Milliseconds()
    {
        await using (var connection = new MySqlConnection(_cs))
        {
            await connection.ExecuteAsync("""
                INSERT INTO games (id, system_id, title, display_title, zip_path, bucket, developers, publishers, genres,
                                   zip_size_bytes, zip_mtime_utc, info_schema_version, created_at, updated_at)
                SELECT UUID(), ELT(1 + seq % 5, 'gb', 'nes', 'snes', 'megadrive', 'psx'), CONCAT('Game ', seq), CONCAT('Game ', seq),
                       CONCAT('x/G/Game ', seq, '.zip'), 'G', '["Dev"]',
                       JSON_ARRAY(CONCAT('Publisher ', seq % 2000), CONCAT(' publisher ', (seq * 7) % 2000), 'Shared Publisher Name'),
                       JSON_ARRAY(CONCAT('Genre ', seq % 40), 'Action'),
                       1, @T0, 2, @T0, @T0
                FROM seq_1_to_50000
                """, new { T0 }, commandTimeout: 600);
            await connection.ExecuteAsync("""
                INSERT INTO roms (game_id, system_id, file_name, original_file_name, base_title, regions, languages, flags,
                                  region_key, is_primary, id_method, size_bytes, crc32, md5, sha1, date_added, date_modified)
                SELECT id, system_id, CONCAT(title, '.bin'), CONCAT(title, '.bin'), title, '["USA"]', '[]', '[]', '|USA|', 1, 'Dat',
                       1024, LEFT(MD5(id), 8), MD5(id), SHA1(id), @T0, @T0
                FROM games
                """, new { T0 }, commandTimeout: 600);
            await connection.ExecuteAsync("ANALYZE TABLE games, roms");
        }

        async Task<double> MedianMs(Func<Task> run)
        {
            await run();
            var times = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                await run();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            return times.Order().ElementAt(2);
        }

        var cold = Stopwatch.StartNew();
        await _repository.ListGroupsAsync(DiscoveryKind.Publishers, 1, 48, null, DiscoveryGroupSort.Name, false, default);
        var build = cold.Elapsed.TotalMilliseconds;

        var groups = await MedianMs(() => _repository.ListGroupsAsync(DiscoveryKind.Publishers, 1, 48, null, DiscoveryGroupSort.Name, false, default));
        var deepGroups = await MedianMs(() => _repository.ListGroupsAsync(DiscoveryKind.Publishers, 40, 48, null, DiscoveryGroupSort.Name, false, default));
        var genres = await MedianMs(() => _repository.ListGroupsAsync(DiscoveryKind.Genres, 1, 48, null, DiscoveryGroupSort.Name, false, default));
        var games = await MedianMs(() => SelectAsync(DiscoveryKind.Publishers, Key("publisher 7")));
        var bigGames = await MedianMs(() => SelectAsync(DiscoveryKind.Publishers, Key("shared publisher name"), 500));

        TestContext.Out.WriteLine(
            $"cold publisher build {build:F0} ms; warm: groups {groups:F0} ms, deep groups {deepGroups:F0} ms, genres {genres:F0} ms, " +
            $"games {games:F0} ms, page 500 of a 50,000-game group {bigGames:F0} ms");
        Assert.That((await SelectAsync(DiscoveryKind.Publishers, Key("shared publisher name")))!.Total, Is.EqualTo(50_000));
        Assert.Multiple(() =>
        {
            Assert.That(groups, Is.LessThan(500), "group page");
            Assert.That(deepGroups, Is.LessThan(500), "deep group page");
            Assert.That(genres, Is.LessThan(500), "genre page");
            Assert.That(games, Is.LessThan(500), "group game page");
            Assert.That(bigGames, Is.LessThan(500), "deep page of a group holding every game");
        });
    }
}
