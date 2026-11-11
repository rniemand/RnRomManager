using System.Text;
using System.Text.Json;
using RnRomManager.Core.Dats;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Spec T2 (upstream parse, compact file), T3 GameName, T5 base title and the T8 CDN URL rule.</summary>
public class TitleDbTests
{
    private const string Cdn = "https://img-eshop.cdn.nintendo.net/i/";

    private static Stream Utf8(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static Task<IReadOnlyList<TitleDbRow>> ParseAsync(string json) => TitleDb.ParseUpstreamAsync(Utf8(json), default);

    private static string Entry(string? id, string? name = "Game", string extra = "") =>
        $$"""{ "id": {{JsonSerializer.Serialize(id)}}, "name": {{JsonSerializer.Serialize(name)}}, "size": 90958725120, "version": null{{extra}} }""";

    /// <summary>Shaped like <c>US.en.json</c>: an object keyed by nsuId.</summary>
    private static readonly string Fixture = $$"""
        {
          "70010000000001": {{Entry(null, "No id")}},
          "70010000000002": {{Entry("0400000000001000", "Switch 2 game")}},
          "70010000000003": {{Entry("0100000000002000\n", "Trailing line feed")}},
          "70010000000004": {{Entry("0100000000003000", null)}},
          "70010000000005": {{Entry("0100aaaa00004000", "The Legend of Zelda™: Tears of the Kingdom", $$"""
              , "publisher": "Nintendo", "developer": null, "releaseDate": 20170303,
              "category": ["Action", " ", null, "Adventure"], "languages": ["en", "fr", "en", "", null],
              "iconUrl": "{{Cdn}}icon.jpg", "frontBoxArt": null,
              "screenshots": ["{{Cdn}}shot1.jpg", "{{Cdn}}shot2.jpg"], "unknownField": { "x": 1 }
              """)}},
          "70010000000006": {{Entry("0100AAAA00004000", "Repeat keeps first")}},
          "70010000000007": {{Entry("0100000000005000", "Year only", $$"""
              , "releaseDate": 2020, "category": null, "languages": null,
              "iconUrl": "https://example.com/icon.jpg", "screenshots": null
              """)}},
          "70010000000008": {{Entry("0100000000006000", "™®©")}},
          "70010000000009": {{Entry("0100000000007000", new string('x', 300))}},
          "70010000000010": {{Entry("0100000000008000", "Long URL", $$"""
              , "releaseDate": 2147483647, "iconUrl": "{{Cdn}}{{new string('a', 1100)}}.jpg"
              """)}},
          "70010000000011": null
        }
        """;

    [Test]
    public async Task Upstream_KeepsUsableSwitchEntries_FirstOfARepeat()
    {
        var rows = await ParseAsync(Fixture);

        Assert.That(rows.Select(r => r.TitleId), Is.EqualTo(new[] { "0100AAAA00004000", "0100000000005000", "0100000000008000" }));
        var zelda = rows[0];
        Assert.That(zelda.Name, Is.EqualTo("The Legend of Zelda™: Tears of the Kingdom"));
        Assert.That(zelda.Publisher, Is.EqualTo("Nintendo"));
        Assert.That(zelda.Developer, Is.Null);
        Assert.That(zelda.ReleaseYear, Is.EqualTo(2017));
        Assert.That(zelda.Genres, Is.EqualTo(new[] { "Action", "Adventure" }));
        Assert.That(zelda.Languages, Is.EqualTo(new[] { "en", "fr" }));
        Assert.That(zelda.BoxArtUrl, Is.EqualTo(Cdn + "icon.jpg"));
        Assert.That(zelda.SnapUrl, Is.EqualTo(Cdn + "shot1.jpg"));

        var yearOnly = rows[1];
        Assert.That(yearOnly.ReleaseYear, Is.EqualTo(2020));
        Assert.That(yearOnly.Genres, Is.Empty);
        Assert.That(yearOnly.Languages, Is.Empty);
        Assert.That(yearOnly.BoxArtUrl, Is.Null, "not the Nintendo CDN");
        Assert.That(yearOnly.SnapUrl, Is.Null);

        Assert.That(rows[2].ReleaseYear, Is.Null, "Int32.MaxValue isn't a date");
        Assert.That(rows[2].BoxArtUrl, Is.Null, "over 1024 characters");
    }

    [Test]
    public async Task Upstream_FrontBoxArt_IsTheFallback()
    {
        var rows = await ParseAsync($$"""{ "1": {{Entry("0100000000001000", "Game", $", \"iconUrl\": null, \"frontBoxArt\": \"{Cdn}front.jpg\"")}} }""");

        Assert.That(rows.Single().BoxArtUrl, Is.EqualTo(Cdn + "front.jpg"));
    }

    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("\"text\"")]
    public void Upstream_NotAnObject_Fails(string json) =>
        Assert.ThrowsAsync<JsonException>(() => ParseAsync(json));

    [Test]
    public async Task Upstream_EmptyObject_IsEmpty() => Assert.That(await ParseAsync("{}"), Is.Empty);

    [TestCase(Cdn + "a.jpg", true)]
    [TestCase("https://IMG-ESHOP.CDN.NINTENDO.NET/a.jpg", true)]
    [TestCase("https://img-eshop.cdn.nintendo.net:443/a.jpg", true)]
    [TestCase("http://img-eshop.cdn.nintendo.net/a.jpg", false)]
    [TestCase("https://img-eshop.cdn.nintendo.net.evil.com/a.jpg", false)]
    [TestCase("https://user:pw@img-eshop.cdn.nintendo.net/a.jpg", false)]
    [TestCase("https://img-eshop.cdn.nintendo.net:8443/a.jpg", false)]
    [TestCase("/relative/a.jpg", false)]
    [TestCase("", false)]
    public void CdnUrl(string url, bool valid) => Assert.That(TitleDb.IsCdnUrl(url), Is.EqualTo(valid));

    [TestCase("The Legend of Zelda: Tears of the Kingdom", "Legend of Zelda, The - Tears of the Kingdom")]
    [TestCase("A Hat in Time", "Hat in Time, A")]
    [TestCase("An Untitled Story", "Untitled Story, An")]
    [TestCase("Theme Park™", "Theme Park")]
    [TestCase("Baldo\nThe guardian owls", "Baldo The guardian owls")]
    [TestCase("  Super®  Game:\n Deluxe ", "Super Game - Deluxe")]
    [TestCase("Oh...Sir! The Insult Simulator", "Oh...Sir! The Insult Simulator")]
    public void BaseTitle(string name, string expected) => Assert.That(TitleDb.BaseTitle(name), Is.EqualTo(expected));

    [Test]
    public void GameName_AppendsTheTitleId() =>
        Assert.That(TitleDb.GameName("The Game", "0100000000001000"), Is.EqualTo("Game, The [0100000000001000]"));

    private static readonly TitleDbRow Valid = new("0100000000001000", "Game", "Pub", null, 2017, ["Action"], ["en"], Cdn + "a.jpg", null);

    private static async Task<IReadOnlyList<TitleDbRow>> RoundTripAsync(IReadOnlyList<TitleDbRow> rows)
    {
        using var stream = new MemoryStream();
        await TitleDb.WriteCompactAsync(stream, rows, default);
        stream.Position = 0;
        return TitleDb.ReadCompact(stream);
    }

    [Test]
    public async Task Compact_RoundTrips_WithNullsWritten()
    {
        var empty = Valid with { TitleId = "0100000000002000", Publisher = null, ReleaseYear = null, Genres = [], Languages = [], BoxArtUrl = null };
        using var stream = new MemoryStream();
        await TitleDb.WriteCompactAsync(stream, [Valid, empty], default);
        var json = Encoding.UTF8.GetString(stream.ToArray());

        Assert.That(json, Does.StartWith("""[{"titleId":"0100000000001000","name":"Game","publisher":"Pub","developer":null,"releaseYear":2017,"genres":["Action"],"languages":["en"],"boxArtUrl":"""));
        var back = await RoundTripAsync([Valid, empty]);
        Assert.That(back[0], Is.EqualTo(Valid).Using<TitleDbRow>(SameRow));
        Assert.That(back[1], Is.EqualTo(empty).Using<TitleDbRow>(SameRow));
    }

    private static bool SameRow(TitleDbRow a, TitleDbRow b) =>
        a with { Genres = [], Languages = [] } == b with { Genres = [], Languages = [] }
        && a.Genres.SequenceEqual(b.Genres) && a.Languages.SequenceEqual(b.Languages);

    private static readonly string ValidJson =
        """{"titleId":"0100000000001000","name":"Game","publisher":null,"developer":null,"releaseYear":null,"genres":[],"languages":[],"boxArtUrl":null,"snapUrl":null}""";

    [TestCase("null", TestName = "Compact_Rejects JSON null")]
    [TestCase("{}", TestName = "Compact_Rejects an object")]
    [TestCase("[null]", TestName = "Compact_Rejects a null row")]
    [TestCase("""[{"titleId":"0100000000001000","name":"Game","developer":null,"releaseYear":null,"genres":[],"languages":[],"boxArtUrl":null,"snapUrl":null}]""",
        TestName = "Compact_Rejects a missing nullable property")]
    [TestCase("[" + "ROW" + "," + "ROW" + "]", TestName = "Compact_Rejects a duplicate title id")]
    [TestCase("ID:0100aaaa00001000", TestName = "Compact_Rejects a lower-case id")]
    [TestCase("ID:0400000000001000", TestName = "Compact_Rejects a Switch 2 id")]
    [TestCase("ID:0100000000001000\\n", TestName = "Compact_Rejects an id with a line feed")]
    [TestCase("NAME:\"™\"", TestName = "Compact_Rejects a mark-only name")]
    [TestCase("NAME:null", TestName = "Compact_Rejects a null name")]
    [TestCase("YEAR:999", TestName = "Compact_Rejects a year below 1000")]
    [TestCase("YEAR:10000", TestName = "Compact_Rejects a year above 9999")]
    [TestCase("GENRES:null", TestName = "Compact_Rejects null genres")]
    [TestCase("GENRES:[null]", TestName = "Compact_Rejects a null genre")]
    [TestCase("LANGUAGES:[1]", TestName = "Compact_Rejects a non-string language")]
    [TestCase("LANGUAGES:[\" \"]", TestName = "Compact_Rejects a blank language")]
    [TestCase("BOX:\"https://example.com/a.jpg\"", TestName = "Compact_Rejects a non-CDN URL")]
    public void Compact_Rejects(string shape)
    {
        var json = shape switch
        {
            _ when shape.StartsWith("ID:") => ValidJson.Replace("0100000000001000", shape[3..]),
            _ when shape.StartsWith("NAME:") => ValidJson.Replace("\"Game\"", shape[5..]),
            _ when shape.StartsWith("YEAR:") => ValidJson.Replace("\"releaseYear\":null", $"\"releaseYear\":{shape[5..]}"),
            _ when shape.StartsWith("GENRES:") => ValidJson.Replace("\"genres\":[]", $"\"genres\":{shape[7..]}"),
            _ when shape.StartsWith("LANGUAGES:") => ValidJson.Replace("\"languages\":[]", $"\"languages\":{shape[10..]}"),
            _ when shape.StartsWith("BOX:") => ValidJson.Replace("\"boxArtUrl\":null", $"\"boxArtUrl\":{shape[4..]}"),
            _ => shape.Replace("ROW", ValidJson),
        };
        if (shape.Contains(':') && !shape.StartsWith('['))
            json = $"[{json}]";

        Assert.Throws<JsonException>(() => TitleDb.ReadCompact(Utf8(json)));
    }

    [Test]
    public void Compact_AcceptsAnEmptyArray_AndAValidRow()
    {
        Assert.That(TitleDb.ReadCompact(Utf8("[]")), Is.Empty);
        Assert.That(TitleDb.ReadCompact(Utf8($"[{ValidJson}]")), Has.Count.EqualTo(1));
    }

    [TestCase("Oh...Sir! The Insult Simulator", 0u, ".NSP", "Oh…Sir! The Insult Simulator [0100000000001000][v0].nsp")]
    [TestCase("Zelda™: Tears\nof the Kingdom",65536u, ".xci", "Zelda - Tears of the Kingdom [0100000000001000][v65536].xci")]
    [TestCase("What? A/B", 0u, ".nsp", "What_ A_B [0100000000001000][v0].nsp")]
    public void RomFileName(string name, uint version, string extension, string expected) =>
        Assert.That(TitleDb.RomFileName(name, "0100000000001000", version, extension), Is.EqualTo(expected));

    [TestCase("Game [0100000000001800][v65536].nsp", 65536u)]
    [TestCase("Game [V5][v7].nsp", 7u)]
    [TestCase("Game [v4294967296].nsp", 0u)]
    [TestCase("Game [v007].nsp", 7u)]
    [TestCase("Game.nsp", 0u)]
    public void VersionFrom(string fileName, uint expected) => Assert.That(TitleDb.VersionFrom(fileName), Is.EqualTo(expected));
}
