using System.Text;
using System.Text.Json.Nodes;
using NSubstitute;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

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
        ISystemHandler[] handlers = [Handler("gb"), Handler("gbc"), new ExternalTestHandler()];
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns(handlers);
        return new InfoJsonReader([new InfoJsonUpgraderV1(), new InfoJsonUpgraderV2(), new InfoJsonUpgraderV3(), .. upgraders], registry);
    }

    private static ISystemHandler Handler(string id)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.EmbedRomsInZip.Returns(true);
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
        IsDefault = type == ImageType.BoxArt,
        IsCover = type == ImageType.BoxArt,
        SizeBytes = 7156,
        Sha1 = new string('c', 40),
        Width = 512,
        Height = 512,
        DateAdded = When,
    };

    private static InfoJsonGame Game() => new()
    {
        SchemaVersion = 4,
        GameId = Guid.Parse("6f1f6c3e-8a54-4f39-9d2b-0d2a7c1e9b11"),
        System = "gb",
        Title = "Tetris",
        DisplayTitle = "Tetris",
        ReleaseYear = 1989,
        Developers = ["Nintendo"],
        Publishers = ["Nintendo"],
        Genres = [],
        Description = null,
        Rating = 4,
        Favourite = true,
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
        Assert.That(text, Does.Contain("\n  \"schemaVersion\": 4"));
        Assert.That(text, Does.Contain("\"externalFileName\": null"));
        Assert.That(text, Does.Contain("\"isCover\": true"));
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
        var result = ReadJson("""{ "schemaVersion": 5, "anything": "goes" }""");

        Assert.That((result.Kind, result.FileVersion), Is.EqualTo((InfoJsonReadResult.Outcome.NewerSchema, (int?)5)));
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
        Assert.That((result.FileVersion, result.Game!.SchemaVersion), Is.EqualTo(((int?)0, 4)));
        Assert.That(result.Game.Genres, Is.EqualTo(new[] { "Puzzle" }));
        upgrader.Received(1).Upgrade(Arg.Any<JsonObject>());
    }

    [Test]
    public void V1File_ReadsAsV4_NotRatedNotFavourite()
    {
        var result = ReadEdited(d =>
        {
            d["schemaVersion"] = 1;
            d.Remove("rating");
            d.Remove("favourite");
            RemoveIsCover(d);
            RemoveExternalFileName(d);
        });

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        Assert.That((result.FileVersion, result.Game!.SchemaVersion, result.Game.Rating, result.Game.Favourite),
            Is.EqualTo(((int?)1, 4, (int?)null, false)));
        Assert.That(result.Game.Roms[0].ExternalFileName, Is.Null);
    }

    [Test]
    public void V2_WithoutFavourite_IsInvalid()
    {
        Assert.That(RuleOf(ReadEdited(d => d.Remove("favourite"))), Does.Contain("missing or wrongly typed"));
    }

    [Test]
    public void V2_NullRating_IsValid()
    {
        var result = ReadEdited(d => d["rating"] = null);

        Assert.That((result.Kind, result.Game?.Rating), Is.EqualTo((InfoJsonReadResult.Outcome.Ok, (int?)null)), result.Rule);
    }

    [TestCase(0)]
    [TestCase(6)]
    public void RatingOutOfRange_IsInvalid(int rating)
    {
        Assert.That(RuleOf(ReadEdited(d => d["rating"] = rating)), Is.EqualTo("rating must be null or 1-5"));
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
        yield return Case("NoCover", g => g with { Images = [Image("a.png", ImageType.BoxArt) with { IsCover = false }] }, "exactly one image must be isCover");
        yield return Case("TwoCovers", g => g with { Images = [Image("a.png", ImageType.BoxArt), Image("b.png", ImageType.Snap) with { IsCover = true }] }, "exactly one image must be isCover");
        yield return Case("TwoDefaults", g => g with { Images = [Image("a.png", ImageType.BoxArt), Image("b.png", ImageType.BoxArt) with { IsCover = false }] }, "at most one isDefault");
        yield return Case("DefaultSnap", g => g with { Images = [Image("a.png", ImageType.BoxArt) with { IsDefault = false }, Image("b.png", ImageType.Snap) with { IsDefault = true }] }, "isDefault image must be a BoxArt");
        yield return Case("TooManyImages", g => g with { Images = [Image("a.png", ImageType.BoxArt), .. Enumerable.Range(1, 20).Select(n => Image($"s{n}.png", ImageType.Snap))] }, "at most 20 images");
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
    public void NoBoxArt_OrNoImages_IsNotInvalid()
    {
        var snapCover = _reader.Read(InfoJsonSerializer.Serialize(Game() with { Images = [Image("snap-01.png", ImageType.Snap) with { IsCover = true }] }));
        var none = _reader.Read(InfoJsonSerializer.Serialize(Game() with { Images = [] }));

        Assert.That(snapCover.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), snapCover.Rule);
        Assert.That(none.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), none.Rule);
    }

    [Test]
    public void V3_AllowsSeveralBoxArtsAndSnaps_JpegNames_AndUploadSource()
    {
        var game = Game() with
        {
            Images =
            [
                Image("boxart-01.png", ImageType.BoxArt),
                Image("boxart-02.jpg", ImageType.BoxArt) with { IsDefault = false, IsCover = false, Source = InfoJsonImage.SourceUpload },
                Image("snap-01.png", ImageType.Snap) with { IsDefault = false, Source = InfoJsonImage.SourceLibretro },
                Image("snap-02.jpg", ImageType.Snap) with { IsDefault = false, Source = InfoJsonImage.SourceUpload },
            ],
        };

        var result = _reader.Read(InfoJsonSerializer.Serialize(game));

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
    }

    private static void RemoveIsCover(JsonObject document)
    {
        foreach (var image in document["images"]!.AsArray().OfType<JsonObject>())
            image.Remove("isCover");
    }

    private InfoJsonReadResult ReadV2(params InfoJsonImage[] images) => ReadEdited(d =>
    {
        d["schemaVersion"] = 2;
        d["images"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(images, InfoJsonSerializer.Options));
        RemoveIsCover(d);
        RemoveExternalFileName(d);
    });

    [Test]
    public void V2Upgrade_BoxArtBecomesTheCover()
    {
        var result = ReadV2(Image("snap-01.png", ImageType.Snap), Image("boxart-01.png", ImageType.BoxArt));

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        Assert.That(result.Game!.Images.Select(i => (i.FileName, i.IsCover)), Is.EqualTo(new[] { ("snap-01.png", false), ("boxart-01.png", true) }));
        Assert.That((result.FileVersion, result.Game.SchemaVersion, result.Game.Roms[0].ExternalFileName), Is.EqualTo(((int?)2, 4, (string?)null)));
    }

    [Test]
    public void V2Upgrade_WithoutBoxArt_TheFirstImageIsTheCover()
    {
        var result = ReadV2(Image("snap-01.png", ImageType.Snap), Image("title-01.png", ImageType.TitleScreen));

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        Assert.That(result.Game!.Images.Select(i => i.IsCover), Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public void V2Upgrade_WithNoImages_StaysValid()
    {
        var result = ReadV2();

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        Assert.That(result.Game!.Images, Is.Empty);
    }

    [Test]
    public void V3_WithoutIsCover_IsInvalid()
    {
        Assert.That(RuleOf(ReadEdited(RemoveIsCover)), Does.Contain("missing or wrongly typed"));
    }

    private static void RemoveExternalFileName(JsonObject document)
    {
        foreach (var rom in document["roms"]!.AsArray().OfType<JsonObject>())
            rom.Remove("externalFileName");
    }

    [Test]
    public void V3File_ReadsAsV4_WithNullExternalFileName()
    {
        var result = ReadEdited(d =>
        {
            d["schemaVersion"] = 3;
            RemoveExternalFileName(d);
        });

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        Assert.That((result.FileVersion, result.Game!.SchemaVersion, result.Game.Roms[0].ExternalFileName), Is.EqualTo(((int?)3, 4, (string?)null)));
    }

    [Test]
    public void V3Upgrade_LeavesShapesItCannotUpgradeForTheTypedRead()
    {
        var notAList = ReadEdited(d =>
        {
            d["schemaVersion"] = 3;
            d["roms"] = "none";
        });
        var missing = ReadEdited(d =>
        {
            d["schemaVersion"] = 3;
            d.Remove("roms");
        });

        Assert.That(RuleOf(notAList), Does.StartWith("missing or wrongly typed field"));
        Assert.That(RuleOf(missing), Does.StartWith("missing or wrongly typed field"));
    }

    [TestCase(3, "roms")]
    [TestCase(4, "roms")]
    [TestCase(3, "images")]
    [TestCase(4, "images")]
    public void NullListElement_IsInvalid_NotAnException(int version, string list)
    {
        InfoJsonReadResult? result = null;

        Assert.DoesNotThrow(() => result = ReadEdited(d =>
        {
            d["schemaVersion"] = version;
            if (version == 3)
                RemoveExternalFileName(d);
            d[list]!.AsArray().Add(null);
        }));

        Assert.That(result!.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Invalid));
    }

    private const string ExtRomName = "Orbit Test [0100000000010000][v0].nsp";

    private static InfoJsonRom ExtRom(string? pointer, string fileName = ExtRomName, bool isPrimary = true, char hash = 'b') =>
        Rom(fileName, isPrimary) with
        {
            ExternalFileName = pointer,
            Metadata = Rom(fileName).Metadata with { Sha1 = new string(hash, 40) },
        };

    private static InfoJsonGame ExtGame(params InfoJsonRom[] roms) => Game() with { System = ExternalTestHandler.SystemId, Roms = roms };

    private InfoJsonReadResult ReadGame(InfoJsonGame game) => _reader.Read(InfoJsonSerializer.Serialize(game));

    [Test]
    public void ExternalRom_RoundTripsItsPointer()
    {
        var result = ReadGame(ExtGame(ExtRom("Orbit Test." + ExtRomName)));

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        Assert.That(result.Game!.Roms[0].ExternalFileName, Is.EqualTo("Orbit Test." + ExtRomName));
        Assert.That(Encoding.UTF8.GetString(InfoJsonSerializer.Serialize(result.Game)), Does.Contain("\"externalFileName\": \"Orbit Test.Orbit Test [0100000000010000][v0].nsp\""));
    }

    private static IEnumerable<TestCaseData> ValidPointers()
    {
        yield return new TestCaseData("Oh...Sir!!.Oh\u2026Sir!! [0100000000010000][v0].nsp").SetName("ValidPointer dots inside the name");
        yield return new TestCaseData(new string('a', 251) + ".nsp").SetName("ValidPointer 255 ASCII bytes");
        yield return new TestCaseData(new string('\u00E9', 125) + "a.nsp").SetName("ValidPointer 255 multibyte bytes");
        yield return new TestCaseData("Game.DUMP.XCI").SetName("ValidPointer extension in any case");
        yield return new TestCaseData("COM0.nsp").SetName("ValidPointer COM0 is not a device");
        yield return new TestCaseData("CONSOLE.nsp").SetName("ValidPointer CONSOLE is not a device");
    }

    [TestCaseSource(nameof(ValidPointers))]
    public void ExternalFileName_PortableRomFileName_IsValid(string pointer)
    {
        var result = ReadGame(ExtGame(ExtRom(pointer)));

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
    }

    private static IEnumerable<TestCaseData> InvalidPointers()
    {
        static TestCaseData Case(string name, string pointer, string rule) => new TestCaseData(pointer, rule).SetName("InvalidPointer " + name);

        yield return Case("parent path", "../x", "plain, portable file name");
        yield return Case("dot dot", "..", "plain, portable file name");
        yield return Case("dot", ".", "plain, portable file name");
        yield return Case("empty", "", "plain, portable file name");
        yield return Case("backslash", "sub\\x.nsp", "plain, portable file name");
        yield return Case("control character", "x\u0001.nsp", "plain, portable file name");
        yield return Case("stream", "file:stream.nsp", "plain, portable file name");
        yield return Case("NUL", "NUL.nsp", "plain, portable file name");
        yield return Case("NUL with space", "NUL .nsp", "plain, portable file name");
        yield return Case("COM1 with space", "COM1 .nsp", "plain, portable file name");
        yield return Case("CONIN$", "CONIN$.nsp", "plain, portable file name");
        yield return Case("CONOUT$", "conout$.nsp", "plain, portable file name");
        yield return Case("superscript COM", "COM\u00B9.nsp", "plain, portable file name");
        yield return Case("a ZIP", "Other.zip", "must end with a ROM extension");
        yield return Case("a temp file", "x.nsp.tmp", "must end with a ROM extension");
        yield return Case("256 ASCII bytes", new string('a', 252) + ".nsp", "at most 255 UTF-8 bytes");
        yield return Case("256 multibyte bytes", new string('\u00E9', 126) + ".nsp", "at most 255 UTF-8 bytes");
    }

    [TestCaseSource(nameof(InvalidPointers))]
    public void ExternalFileName_ThatCouldLeaveTheFolderOrIsNotARom_IsInvalid(string pointer, string rule) =>
        Assert.That(RuleOf(ReadGame(ExtGame(ExtRom(pointer)))), Does.Contain("externalFileName").And.Contain(rule));

    [Test]
    public void ExternalSystem_WithoutAPointer_IsInvalid() =>
        Assert.That(RuleOf(ReadGame(ExtGame(ExtRom(null)))), Does.Contain("externalFileName is required"));

    [Test]
    public void EmbeddedSystem_WithAPointer_IsInvalid() =>
        Assert.That(RuleOf(ReadGame(Game() with { Roms = [Rom("Tetris.gb") with { ExternalFileName = "Tetris.Tetris.gb" }] })),
            Does.Contain("externalFileName must be null"));

    [Test]
    public void ExternalFileName_DuplicateInAnyCase_IsInvalid()
    {
        var game = ExtGame(ExtRom("Game.A.nsp"), ExtRom("game.a.NSP", "Orbit Test [0100000000010800][v1].nsp", isPrimary: false, hash: 'c'));

        Assert.That(RuleOf(ReadGame(game)), Does.Contain("externalFileName is not unique"));
    }

    [TestCase("bad:stream.nsp", "must not contain")]
    [TestCase("NUL.nsp", "Windows device name")]
    [TestCase("game.bin", "must end with a ROM extension")]
    public void ExternalRom_LogicalFileNameMustBePortableWithARomExtension(string fileName, string rule) =>
        Assert.That(RuleOf(ReadGame(ExtGame(ExtRom("Game.x.nsp", fileName)))), Does.Contain("rom fileName").And.Contain(rule));

    [Test]
    public void ExternalRom_LogicalFileNameOver255Scalars_IsInvalid()
    {
        // Astral characters: 252 scalars are 504 UTF-16 units, so the limit counts scalars, not chars.
        var fits = string.Concat(Enumerable.Repeat("\U0001F600", 251)) + ".nsp";
        var tooLong = string.Concat(Enumerable.Repeat("\U0001F600", 252)) + ".nsp";

        Assert.That(ReadGame(ExtGame(ExtRom("Game.x.nsp", fits))).Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok));
        Assert.That(RuleOf(ReadGame(ExtGame(ExtRom("Game.x.nsp", tooLong)))), Does.Contain("at most 255 characters"));
    }

    [TestCase("bad:stream.gb")]
    [TestCase("NUL.gb")]
    [TestCase("game.bin")]
    public void EmbeddedRom_LogicalFileNameRules_AreUnchanged(string fileName) =>
        Assert.That(ReadGame(Game() with { Roms = [Rom(fileName)] }).Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok));

    [Test]
    public void ExternalRom_SizeOverTheHandlerLimit_IsInvalid_EvenWithAValidPointer()
    {
        var max = new ExternalTestHandler().MaxRomSizeBytes;
        InfoJsonGame Sized(long size) => ExtGame(ExtRom("Game.x.nsp") with { Metadata = ExtRom("Game.x.nsp").Metadata with { SizeBytes = size } });

        Assert.That(ReadGame(Sized(max)).Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok));
        Assert.That(RuleOf(ReadGame(Sized(max + 1))), Does.Contain("sizeBytes is over the system's limit"));
    }

    [Test]
    public void TitleDbMethod_AndTitleDbImageSource_AreAccepted()
    {
        var game = ExtGame(ExtRom("Game.x.nsp") with { Identification = new InfoJsonIdentification(IdentificationMethod.TitleDb, "titledb", "2026.11.10", "Orbit Test [0100000000010000]") }) with
        {
            Images = [Image("boxart-01.jpg", ImageType.BoxArt) with { IsDefault = false, Source = InfoJsonImage.SourceTitleDb, SourceUrl = "https://img-eshop.cdn.nintendo.net/i/x.jpg" }],
        };

        var result = ReadGame(game);

        Assert.That(result.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), result.Rule);
        Assert.That((result.Game!.Roms[0].Identification.Method, result.Game.Images[0].Source), Is.EqualTo((IdentificationMethod.TitleDb, "titledb")));
        Assert.That(Encoding.UTF8.GetString(InfoJsonSerializer.Serialize(result.Game)), Does.Contain("\"method\": \"TitleDb\""));
    }
}
