using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MySqlConnector;
using RnRomManager.Core.Discovery;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>B2–B3 live discovery indexes against MariaDB.</summary>
[Category("Integration")]
public partial class DiscoveryRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private string _cs = null!;
    private DiscoveryRepository _repository = null!;
    private MySqlConnectionFactory _factory = null!;
    private FakeTimeProvider _time = null!;

    [SetUp]
    public async Task SetUp()
    {
        _cs = await MariaDbServer.CreateDatabaseAsync();
        var factory = new MySqlConnectionFactory(_cs);
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _time = new FakeTimeProvider(T0);
        _repository = new DiscoveryRepository(factory, new GameRepository(factory), _time);
        _factory = factory;
    }

    /// <summary>Inserts a game whose arrays are raw JSON, so tests can store anything MariaDB accepts.</summary>
    private async Task<Guid> InsertGameAsync(string system, string title, string publishers = "[]", string genres = "[]")
    {
        var id = Guid.NewGuid();
        await using var connection = new MySqlConnection(_cs);
        await connection.ExecuteAsync("""
            INSERT INTO games (id, system_id, title, display_title, zip_path, bucket, developers, publishers, genres,
                               zip_size_bytes, zip_mtime_utc, info_schema_version, created_at, updated_at)
            VALUES (@id, @system, @title, @title, @zip, 'T', '[]', @publishers, @genres, 1, @T0, 2, @T0, @T0)
            """, new { id, system, title, zip = $"{system}/T/{id}.zip", publishers, genres, T0 });
        return id;
    }

    // Unicode whitespace beyond ASCII: no-break, em, ideographic spaces and the line separator.
    private static readonly string Nbsp = ((char)0x00A0).ToString();
    private static readonly string EmSpace = ((char)0x2003).ToString();
    private static readonly string IdeographicSpace = ((char)0x3000).ToString();
    private static readonly string LineSeparator = ((char)0x2028).ToString();

    private static string Json(params string[] values) => System.Text.Json.JsonSerializer.Serialize(values);

    private static string Key(string normalized) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    private async Task<List<DiscoveryGroup>> AllAsync(DiscoveryKind kind) =>
        (await _repository.ListGroupsAsync(kind, 1, 200, null, default)).Items.ToList();

    [Test]
    public async Task NoGames_IsAnEmptyPage()
    {
        var page = await _repository.ListGroupsAsync(DiscoveryKind.Publishers, 1, 48, null, default);

        Assert.That(page.Items, Is.Empty);
        Assert.That((page.PageNumber, page.PageSize, page.Total), Is.EqualTo((1, 48, 0L)));
    }

    [Test]
    public async Task EmptyNullAndNonStringEntries_AreIgnored()
    {
        await InsertGameAsync("gb", "Empty", publishers: "[]");
        await InsertGameAsync("gb", "Null document", publishers: "null");
        await InsertGameAsync("gb", "Object", publishers: """{"name":"Nintendo"}""");
        await InsertGameAsync("gb", "Odd entries", publishers:
            """[null, 42, true, ["Sega"], {"a":"b"}, "", "   ", """ + Json(Nbsp + IdeographicSpace + LineSeparator)[1..]);

        Assert.That(await AllAsync(DiscoveryKind.Publishers), Is.Empty);
    }

    [Test]
    public async Task UnicodeWhitespaceAndCase_FoldIntoOneGroup_LabelledByTheBinarySmallestSpelling()
    {
        await InsertGameAsync("gb", "A", publishers: Json("Nintendo"));
        await InsertGameAsync("nes", "B", publishers: Json(" nintendo\t"));
        await InsertGameAsync("snes", "C", publishers: Json(Nbsp + "NINTENDO" + IdeographicSpace + "\n"));
        await InsertGameAsync("n64", "D", publishers: Json(EmSpace + "NinTendo" + LineSeparator));

        var group = (await AllAsync(DiscoveryKind.Publishers)).Single();

        Assert.That(group, Is.EqualTo(new DiscoveryGroup(Key("nintendo"), "NINTENDO", 4)));
    }

    [Test]
    public async Task AccentsPunctuationAndInnerSpaces_StayDistinct()
    {
        await InsertGameAsync("gb", "A", publishers: Json("Café", "CAFÉ", "Cafe", "Ca-fe", "Ca fe", "Straße", "STRASSE"));

        var groups = await AllAsync(DiscoveryKind.Publishers);

        Assert.That(groups.Select(g => (g.Name, g.GameCount)), Is.EqualTo(new[]
        {
            ("Ca fe", 1L), ("Ca-fe", 1L), ("Cafe", 1L), ("CAFÉ", 1L), ("STRASSE", 1L), ("Straße", 1L),
        }), "sorted by normalized bytes: 'ca fe' < 'ca-fe' < 'cafe' < 'café' < 'strasse' < 'straße'");
        Assert.That(groups.Select(g => g.Key), Is.EqualTo(new[] { "ca fe", "ca-fe", "cafe", "café", "strasse", "straße" }.Select(Key)));
    }

    [Test]
    public async Task NonAsciiCase_UsesUnicodeLowercase()
    {
        await InsertGameAsync("gb", "A", publishers: Json("ǅ", "Ǆ"));
        await InsertGameAsync("gb", "B", publishers: Json("ΣΟΦΊΑ"));
        await InsertGameAsync("gb", "C", publishers: Json("σοφία"));

        var groups = await AllAsync(DiscoveryKind.Publishers);

        Assert.That(groups.Select(g => (g.Key, g.Name, g.GameCount)), Is.EquivalentTo(new[]
        {
            (Key("ǆ"), "Ǆ", 1L),
            (Key("σοφία"), "ΣΟΦΊΑ", 2L),
        }));
    }

    [Test]
    public async Task MultipleValues_PutAGameInEachGroup_AndDuplicatesCountOnce()
    {
        await InsertGameAsync("gb", "Both", publishers: Json("Nintendo", "Game Freak", "nintendo", "NINTENDO "));
        await InsertGameAsync("gba", "One", publishers: Json("Nintendo"));

        var groups = await AllAsync(DiscoveryKind.Publishers);

        Assert.That(groups.Select(g => (g.Name, g.GameCount)), Is.EqualTo(new[] { ("Game Freak", 1L), ("NINTENDO", 2L) }));
    }

    [Test]
    public async Task LongValues_AreGroupedWhole_AndTheNameIsCutTo255WithAnEllipsis()
    {
        var shared = new string('x', 300);
        await InsertGameAsync("gb", "A", publishers: Json(shared + "a"));
        await InsertGameAsync("gb", "B", publishers: Json(shared + "b"));
        await InsertGameAsync("gb", "C", publishers: Json(shared.ToUpperInvariant() + "B"));
        var exact = new string('é', 255);
        await InsertGameAsync("gb", "D", publishers: Json(exact));

        var groups = await AllAsync(DiscoveryKind.Publishers);

        Assert.That(groups.Select(g => (g.Key, g.GameCount)), Is.EqualTo(new[]
        {
            (Key(shared + "a"), 1L), (Key(shared + "b"), 2L), (Key(exact), 1L),
        }), "values differing only after 300 characters stay apart; keys hash the full value");
        Assert.That(groups[0].Name, Is.EqualTo(new string('x', 254) + "…"));
        Assert.That(groups[1].Name, Is.EqualTo(new string('X', 254) + "…"));
        Assert.That(groups[2].Name, Is.EqualTo(exact), "255 characters fit uncut");
    }

    [Test]
    public async Task EscapedAndLiteralJson_AreTheSameValue()
    {
        // The same value stored as a JSON \u escape and as a literal character.
        await InsertGameAsync("gb", "A", publishers: @"[""Caf\" + "u00e9" + @"""]");
        await InsertGameAsync("gb", "B", publishers: Json("Caf" + (char)0x00E9));

        Assert.That((await AllAsync(DiscoveryKind.Publishers)).Single(), Is.EqualTo(new DiscoveryGroup(Key("café"), "Café", 2)));
    }

    [Test]
    public async Task Genres_AreIndexedFromTheGenresColumnOnly()
    {
        await InsertGameAsync("gb", "A", publishers: Json("Nintendo"), genres: Json("Puzzle", "Action"));
        await InsertGameAsync("megadrive", "B", publishers: Json("Sega"), genres: Json(" action "));

        var genres = await AllAsync(DiscoveryKind.Genres);
        var publishers = await AllAsync(DiscoveryKind.Publishers);

        Assert.That(genres, Is.EqualTo(new[] { new DiscoveryGroup(Key("action"), "Action", 2), new DiscoveryGroup(Key("puzzle"), "Puzzle", 1) }));
        Assert.That(publishers.Select(g => g.Name), Is.EqualTo(new[] { "Nintendo", "Sega" }));
    }

    [TestCase(DiscoveryKind.Publishers)]
    [TestCase(DiscoveryKind.Genres)]
    public async Task Paging_IsStableAndCoversEveryGroupOnce(DiscoveryKind kind)
    {
        var names = Enumerable.Range(0, 23).Select(i => $"Group {i:D2}").ToList();
        foreach (var (name, i) in names.Select((n, i) => (n, i)))
        {
            var json = Json(name, names[(i + 1) % names.Count]);
            await InsertGameAsync(i % 2 == 0 ? "gb" : "nes", $"Game {i}",
                publishers: kind == DiscoveryKind.Publishers ? json : "[]", genres: kind == DiscoveryKind.Genres ? json : "[]");
        }

        var seen = new List<DiscoveryGroup>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await _repository.ListGroupsAsync(kind, page, 10, null, default);
            Assert.That(result.Total, Is.EqualTo(23));
            Assert.That(result.Items, Has.Count.EqualTo(page < 3 ? 10 : 3));
            Assert.That((await _repository.ListGroupsAsync(kind, page, 10, null, default)).Items, Is.EqualTo(result.Items), "repeatable");
            seen.AddRange(result.Items);
        }

        Assert.That(seen.Select(g => g.Name), Is.EqualTo(names));
        Assert.That(seen.Select(g => g.GameCount), Is.All.EqualTo(2));
    }

    [Test]
    public async Task Q_FiltersByNameIgnoringCase_BeforePaging_KeysUnchanged()
    {
        await InsertGameAsync("gb", "A", publishers: Json("Nintendo", "Sega"));
        await InsertGameAsync("nes", "B", publishers: Json("SEGA Toys", "Konami"));

        var first = await _repository.ListGroupsAsync(DiscoveryKind.Publishers, 1, 1, " sega ", default);
        var second = await _repository.ListGroupsAsync(DiscoveryKind.Publishers, 2, 1, "sega", default);

        Assert.That(first.Total, Is.EqualTo(2));
        Assert.That(first.Items.Concat(second.Items).Select(g => g.Name), Is.EqualTo(new[] { "Sega", "SEGA Toys" }));
        Assert.That(first.Items[0].Key, Is.EqualTo(Key("sega")));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task Q_Blank_IsAbsent(string? q)
    {
        await InsertGameAsync("gb", "A", publishers: Json("Nintendo", "Sega"));

        Assert.That((await _repository.ListGroupsAsync(DiscoveryKind.Publishers, 1, 48, q, default)).Total, Is.EqualTo(2));
    }

    [Test]
    public async Task PagePastTheEnd_IsEmpty_WithTheTotal()
    {
        await InsertGameAsync("gb", "A", publishers: Json("Nintendo", "Sega"));

        var page = await _repository.ListGroupsAsync(DiscoveryKind.Publishers, 5, 10, null, default);

        Assert.That(page.Items, Is.Empty);
        Assert.That(page.Total, Is.EqualTo(2));
    }
}
