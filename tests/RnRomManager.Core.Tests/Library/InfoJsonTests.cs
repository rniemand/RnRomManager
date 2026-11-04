using System.Text;
using System.Text.Json.Nodes;
using NSubstitute;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec §6.2 schema, validation rules, upgrades; §9.3 size limit.</summary>
public class InfoJsonTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private InfoJsonReader _reader = null!;

    [SetUp]
    public void SetUp() => _reader = Reader();

    private static InfoJsonReader Reader(params IInfoJsonUpgrader[] upgraders)
    {
        ISystemHandler[] handlers = [Handler("gb"), Handler("gbc")];
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns(handlers);
        return new InfoJsonReader(upgraders, registry);
    }

    private static ISystemHandler Handler(string id)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.Id.Returns(id);
        return handler;
    }

    private static InfoJsonRom Rom(string fileName, bool isPrimary = true) => new()
    {
        FileName = fileName,
        OriginalFileName = "tetris.gb",
        BaseTitle = "Tetris",
        Regions = ["World"],
        Languages = [],
        Revision = "Rev 1",
        Flags = [],
        IsPrimary = isPrimary,
        Identification = new InfoJsonIdentification(IdentificationMethod.Dat, "Nintendo - Game Boy", "2026.10.07", "Tetris (World) (Rev 1)"),
        Header = new JsonObject { ["title"] = "TETRIS", ["logoValid"] = true, ["romSizeBytes"] = 32768 },
        Metadata = new InfoJsonRomMetadata(32768, "46df91ad", new string('a', 32), new string('b', 40), When, When, "/app/ingest/gb/tetris.gb"),
    };

    private static InfoJsonImage Image(string fileName, ImageType type) => new()
    {
        FileName = fileName,
        Type = type,
        Source = InfoJsonImage.SourceDefault,
        SourceUrl = null,
        IsDefault = true,
        SizeBytes = 7156,
        Sha1 = new string('c', 40),
        Width = 512,
        Height = 512,
        DateAdded = When,
    };

    private static InfoJsonGame Game() => new()
    {
        SchemaVersion = 1,
        GameId = Guid.Parse("6f1f6c3e-8a54-4f39-9d2b-0d2a7c1e9b11"),
        System = "gb",
        Title = "Tetris",
        DisplayTitle = "Tetris",
        ReleaseYear = 1989,
        Developers = ["Nintendo"],
        Publishers = ["Nintendo"],
        Genres = [],
        Description = null,
        Roms = [Rom("Tetris (World) (Rev 1).gb")],
        Images = [Image("boxart-01.png", ImageType.BoxArt)],
        Metadata = new InfoJsonGameMetadata(When, When, "0.1.0"),
    };

    private InfoJsonReadResult ReadJson(string json) => _reader.Read(Encoding.UTF8.GetBytes(json));

    private InfoJsonReadResult ReadEdited(Action<JsonObject> edit)
    {
        var document = JsonNode.Parse(InfoJsonSerializer.Serialize(Game()))!.AsObject();
        edit(document);
        return ReadJson(document.ToJsonString());
    }

    private static string? RuleOf(InfoJsonReadResult result) =>
        result.Kind == InfoJsonReadResult.Outcome.Invalid ? result.Rule : $"not invalid ({result.Kind})";

    [Test]
    public void RoundTrip_GivesTheSameGame()
    {
        var game = Game();

        var result = _reader.Read(InfoJsonSerializer.Serialize(game));

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        var read = result.Game!;
        Assert.That(read with { Roms = [], Images = [], Developers = [], Publishers = [], Genres = [] },
            Is.EqualTo(game with { Roms = [], Images = [], Developers = [], Publishers = [], Genres = [] }));
        Assert.That(read.Roms[0] with { Header = null, Regions = [], Languages = [], Flags = [] },
            Is.EqualTo(game.Roms[0] with { Header = null, Regions = [], Languages = [], Flags = [] }));
        Assert.That(read.Roms[0].Header!.ToJsonString(), Is.EqualTo(game.Roms[0].Header!.ToJsonString()));
        Assert.That(read.Images[0], Is.EqualTo(game.Images[0]));
        Assert.That(read.Developers, Is.EqualTo(game.Developers));
    }

    [Test]
    public void Serialize_IsCamelCaseIndentedWithNullsAndEnumNames_NoBom()
    {
        var bytes = InfoJsonSerializer.Serialize(Game());
        var text = Encoding.UTF8.GetString(bytes);

        Assert.That(bytes[0], Is.EqualTo((byte)'{'), "no BOM");
        Assert.That(text, Does.Contain("\n  \"schemaVersion\": 1"));
        Assert.That(text, Does.Contain("\"description\": null"));
        Assert.That(text, Does.Contain("\"method\": \"Dat\""));
        Assert.That(text, Does.Contain("\"type\": \"BoxArt\""));
        Assert.That(text, Does.Contain("\"gameId\": \"6f1f6c3e-8a54-4f39-9d2b-0d2a7c1e9b11\""));
    }

    [Test]
    public void UnknownProperties_AreIgnored()
    {
        var result = ReadEdited(d =>
        {
            d["futureField"] = "x";
            d["roms"]![0]!["extra"] = new JsonObject { ["a"] = 1 };
        });

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        Assert.That(Encoding.UTF8.GetString(InfoJsonSerializer.Serialize(result.Game!)), Does.Not.Contain("futureField"), "dropped on the next write");
    }

    [Test]
    public void OptionalFields_MayBeAbsent()
    {
        var result = ReadEdited(d =>
        {
            d.Remove("releaseYear");
            d.Remove("description");
            var rom = d["roms"]![0]!.AsObject();
            rom.Remove("revision");
            rom.Remove("header");
            rom["identification"] = new JsonObject { ["method"] = "Manual" };
            d["images"]![0]!.AsObject().Remove("sourceUrl");
        });

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
    }

    [Test]
    public void BomIsTolerated()
    {
        var result = _reader.Read([0xEF, 0xBB, 0xBF, .. InfoJsonSerializer.Serialize(Game())]);

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
    }

    [Test]
    public void NewerSchema_IsDetected_NotParsedFurther()
    {
        var result = ReadJson("""{ "schemaVersion": 2, "anything": "goes" }""");

        Assert.That((result.Kind, result.FileVersion), Is.EqualTo((InfoJsonReadResult.Outcome.NewerSchema, (int?)2)));
    }

    [Test]
    public void UpgraderChain_RunsForOlderVersions()
    {
        var upgrader = Substitute.For<IInfoJsonUpgrader>();
        upgrader.FromVersion.Returns(0);
        upgrader.When(u => u.Upgrade(Arg.Any<JsonObject>())).Do(c => c.Arg<JsonObject>()["genres"] = new JsonArray("Puzzle"));
        var reader = Reader(upgrader);
        var v0 = JsonNode.Parse(InfoJsonSerializer.Serialize(Game()))!.AsObject();
        v0["schemaVersion"] = 0;
        v0.Remove("genres"); // a "v0" without genres; the fake upgrader adds them

        var result = reader.Read(Encoding.UTF8.GetBytes(v0.ToJsonString()));

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        Assert.That((result.FileVersion, result.Game!.SchemaVersion), Is.EqualTo(((int?)0, 1)));
        Assert.That(result.Game.Genres, Is.EqualTo(new[] { "Puzzle" }));
        upgrader.Received(1).Upgrade(Arg.Any<JsonObject>());
    }

    [Test]
    public void OlderVersionWithoutAnUpgrader_IsInvalid()
    {
        Assert.That(RuleOf(ReadEdited(d => d["schemaVersion"] = 0)), Does.Contain("no upgrader from schema v0"));
    }

    [Test]
    public void TooLarge_IsInvalid()
    {
        var huge = new byte[InfoJsonReader.MaxBytes + 1];

        Assert.That(RuleOf(_reader.Read(huge)), Does.Contain("larger than"));
    }

    [TestCase("not json")]
    [TestCase("[1, 2]")]
    [TestCase("")]
    public void NotAJsonObject_IsInvalid(string text) =>
        Assert.That(RuleOf(ReadJson(text)), Does.StartWith("not valid UTF-8 JSON"));

    [Test]
    public void InvalidUtf8_IsInvalid()
    {
        Assert.That(RuleOf(_reader.Read([(byte)'{', 0xC3, 0x28, (byte)'}'])), Does.StartWith("not valid UTF-8 JSON"));
    }

    [TestCase("\"1\"")]
    [TestCase("1.5")]
    [TestCase("null")]
    public void SchemaVersionNotAnInteger_IsInvalid(string value) =>
        Assert.That(RuleOf(ReadEdited(d => d["schemaVersion"] = JsonNode.Parse(value))), Does.Contain("schemaVersion"));

    private static IEnumerable<TestCaseData> ShapeErrors()
    {
        yield return new TestCaseData((Action<JsonObject>)(d => d.Remove("title"))).SetName("MissingRequired title");
        yield return new TestCaseData((Action<JsonObject>)(d => d.Remove("developers"))).SetName("MissingRequired developers");
        yield return new TestCaseData((Action<JsonObject>)(d => d["title"] = null)).SetName("NullRequired title");
        yield return new TestCaseData((Action<JsonObject>)(d => d["releaseYear"] = "1989")).SetName("WrongType releaseYear");
        yield return new TestCaseData((Action<JsonObject>)(d => d["gameId"] = "not-a-guid")).SetName("WrongType gameId");
        yield return new TestCaseData((Action<JsonObject>)(d => d["roms"]![0]!.AsObject().Remove("isPrimary"))).SetName("MissingRequired rom isPrimary");
        yield return new TestCaseData((Action<JsonObject>)(d => d["roms"]![0]!["metadata"]!.AsObject().Remove("sha1"))).SetName("MissingRequired rom sha1");
        yield return new TestCaseData((Action<JsonObject>)(d => d["roms"]![0]!["identification"]!["method"] = "Guess")).SetName("UnknownEnum method");
        yield return new TestCaseData((Action<JsonObject>)(d => d["roms"]![0]!["identification"]!["method"] = 0)).SetName("IntegerEnum method");
        yield return new TestCaseData((Action<JsonObject>)(d => d["images"]![0]!.AsObject().Remove("width"))).SetName("MissingRequired image width");
        yield return new TestCaseData((Action<JsonObject>)(d => d.Remove("metadata"))).SetName("MissingRequired metadata");
    }

    [TestCaseSource(nameof(ShapeErrors))]
    public void ShapeErrors_AreInvalid(Action<JsonObject> edit) =>
        Assert.That(RuleOf(ReadEdited(edit)), Does.StartWith("missing or wrongly typed field"));

    private static IEnumerable<TestCaseData> RuleErrors()
    {
        static TestCaseData Case(string name, Func<InfoJsonGame, InfoJsonGame> edit, string rule) =>
            new TestCaseData(edit, rule).SetName(name);

        yield return Case("EmptyGuid", g => g with { GameId = Guid.Empty }, "gameId");
        yield return Case("UnknownSystem", g => g with { System = "nes" }, "system 'nes'");
        yield return Case("NoRoms", g => g with { Roms = [] }, "roms must not be empty");
        yield return Case("NoPrimary", g => g with { Roms = [Rom("a.gb", false), Rom("b.gb", false)] }, "exactly one rom must be isPrimary");
        yield return Case("TwoPrimaries", g => g with { Roms = [Rom("a.gb"), Rom("b.gb")] }, "exactly one rom must be isPrimary");
        yield return Case("DuplicateRomNameIgnoringCase", g => g with { Roms = [Rom("A.gb"), Rom("a.GB", false)] }, "not unique");
        yield return Case("SlashInRomName", g => g with { Roms = [Rom("sub/a.gb")] }, "plain name");
        yield return Case("BackslashInRomName", g => g with { Roms = [Rom("sub\\a.gb")] }, "plain name");
        yield return Case("DotDotInImageName", g => g with { Images = [Image("..png", ImageType.BoxArt)] }, "plain name");
        yield return Case("DuplicateImageName", g => g with { Images = [Image("x.png", ImageType.BoxArt), Image("X.PNG", ImageType.Snap)] }, "not unique");
        yield return Case("UppercaseSha1", g => g with { Roms = [Rom("a.gb") with { Metadata = Rom("a.gb").Metadata with { Sha1 = new string('B', 40) } }] }, "lowercase hex");
        yield return Case("ShortCrc", g => g with { Roms = [Rom("a.gb") with { Metadata = Rom("a.gb").Metadata with { Crc32 = "abc" } }] }, "lowercase hex");
        yield return Case("BadImageSha1", g => g with { Images = [Image("x.png", ImageType.BoxArt) with { Sha1 = "zz" }] }, "sha1 must be lowercase hex");
        yield return Case("UnknownImageSource", g => g with { Images = [Image("x.png", ImageType.BoxArt) with { Source = "web" }] }, "source must be");
        yield return Case("TwoBoxArts", g => g with { Images = [Image("a.png", ImageType.BoxArt), Image("b.png", ImageType.BoxArt)] }, "at most one BoxArt");
        yield return Case("TwoSnaps", g => g with { Images = [Image("a.png", ImageType.BoxArt), Image("b.png", ImageType.Snap), Image("c.png", ImageType.Snap)] }, "at most one Snap");
    }

    [TestCaseSource(nameof(RuleErrors))]
    public void ValidationRules_ReportTheFailingRule(Func<InfoJsonGame, InfoJsonGame> edit, string rule) =>
        Assert.That(RuleOf(_reader.Read(InfoJsonSerializer.Serialize(edit(Game())))), Does.Contain(rule));

    [Test]
    public void FirstFailingRuleWins()
    {
        var game = Game() with { System = "nes", Roms = [] };

        Assert.That(RuleOf(_reader.Read(InfoJsonSerializer.Serialize(game))), Does.Contain("system 'nes'"));
    }

    [Test]
    public void NoBoxArt_IsNotInvalid_ItIsMissingBoxArtForIntegrity()
    {
        var result = _reader.Read(InfoJsonSerializer.Serialize(Game() with { Images = [Image("snap-01.png", ImageType.Snap)] }));

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
    }
}
