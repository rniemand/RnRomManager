using RnRomManager.Core.Dats;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Spec T4 (title id and kind) and T5 (release naming) as pure rules.</summary>
public class TitleDbMatchTests
{
    private const string Base = "0100F2C0115B6000";
    private const string Update = "0100F2C0115B6800";
    private const string Delta = "0100F2C0115B6C00";
    private const string Dlc1 = "0100F2C0115B7001";
    private const string Dlc2000 = "0100F2C0115B77D0";
    private const string Dlc2000Patch = "0100F2C0115B7FD0";
    private const string UnnamedDlc = "0100F2C0115B7002";

    private static TitleDbRow Row(string id, string name, params string[] languages) => new(id, name, null, null, null, [], languages, null, null);

    private static readonly Dictionary<string, TitleDbRow> Store = new[]
    {
        Row(Base, "The Legend of Zelda™: Tears of the Kingdom", "en", "fr", "EN", "En"),
        Row(Dlc1, "Zelda: Expansion Pass", "ja"),
        Row(Dlc2000, "Zelda: Final Pack", "de"),
        Row("0100000000010000", "Oh...Sir! The Insult Simulator", "en"),
        Row("0100000000020000", "Baldo\nThe guardian owls"),
    }.ToDictionary(r => r.TitleId);

    private static TitleDbMatch? Match(string id, string fileName = "game.nsp") => TitleDb.Match(id, fileName, Store.GetValueOrDefault);

    private static Dictionary<string, object?> Header(string id) => new() { ["titleId"] = id };

    [TestCase(null, "Game [0100f2c0115b6000][v0].nsp", Base, TestName = "TitleId from the file name, upper-cased")]
    [TestCase("0100f2c0115b6000", "Game.nsp", Base, TestName = "TitleId from the header field")]
    [TestCase("0100F2C0115B6800", "Game [0100F2C0115B6000].nsp", Update, TestName = "TitleId the header wins over the file name")]
    [TestCase("0400000000001000", "Game [0100F2C0115B6000].nsp", null, TestName = "TitleId a Switch 2 header means none")]
    [TestCase(null, "Game [0200000000001000].nsp", null, TestName = "TitleId a 0200 name is ignored")]
    [TestCase(null, "Game [0400000000001000].nsp", null, TestName = "TitleId a 0400 name is ignored")]
    [TestCase(null, "Game [0400000000001000][0100F2C0115B6000].nsp", Base, TestName = "TitleId the first Switch id in the name")]
    [TestCase(null, "Game.nsp", null, TestName = "TitleId none")]
    [TestCase(null, "Game [0100F2C0115B6000][UPD][1.0.1].nsp", Update, TestName = "TitleId B7 a base id in the name with [UPD] is the update")]
    [TestCase(null, "Game [0100f2c0115b6000][upd].nsp", Update, TestName = "TitleId B7 [upd] matches ignoring case")]
    [TestCase(null, "Game [0100F2C0115B6800][UPD].nsp", Update, TestName = "TitleId B7 an update id stays")]
    [TestCase(null, "Game [0100F2C0115B7001][UPD].nsp", Dlc1, TestName = "TitleId B7 a DLC id is not changed")]
    [TestCase(null, "Game [0100F2C0115B6000] UPD.nsp", Base, TestName = "TitleId B7 needs the brackets")]
    [TestCase("0100F2C0115B6000", "Game [UPD].nsp", Base, TestName = "TitleId B7 a header id ignores [UPD]")]
    public void TitleIdFrom(string? header, string fileName, string? expected) =>
        Assert.That(TitleDb.TitleIdFrom(header is null ? new Dictionary<string, object?>() : Header(header), fileName), Is.EqualTo(expected));

    [TestCase(Base, Update)]
    [TestCase(Update, Update)]
    [TestCase(Dlc1, null)]
    [TestCase(Delta, null)]
    [TestCase(Dlc2000Patch, null)]
    public void UpdateIdOf_BaseOrUpdate_B8(string id, string? expected) => Assert.That(TitleDb.UpdateIdOf(id), Is.EqualTo(expected));

    [Test]
    public void TitleIdFrom_NoHeaderAtAll_UsesTheFileName() =>
        Assert.That(TitleDb.TitleIdFrom(null, $"x [{Base}].xci"), Is.EqualTo(Base));

    [TestCase(Base, TitleKind.Base)]
    [TestCase(Update, TitleKind.Update)]
    [TestCase(Dlc1, TitleKind.Dlc)]
    [TestCase(UnnamedDlc, TitleKind.Dlc)]
    [TestCase(Delta, TitleKind.Dlc)]
    [TestCase(Dlc2000Patch, TitleKind.Dlc)]
    public void Kind_FromTheId(string id, TitleKind kind) => Assert.That(Match(id)!.Kind, Is.EqualTo(kind));

    [TestCase("0100F2C0115B6400", TestName = "NoMatch base + 0x400")]
    [TestCase("0100F2C0115B7000", TestName = "NoMatch base + 0x1000")]
    [TestCase("0100F2C0115B77D1", TestName = "NoMatch past DLC 2000")]
    [TestCase("0100F2C0115B7800", TestName = "NoMatch patch of DLC 0")]
    [TestCase("0100AAAA00000000", TestName = "NoMatch base missing")]
    [TestCase("0100AAAA00000800", TestName = "NoMatch update of a missing base")]
    [TestCase("0100AAAA00001001", TestName = "NoMatch DLC of a missing base")]
    [TestCase("0400F2C0115B6000", TestName = "NoMatch not a Switch id")]
    public void NoMatch(string id) => Assert.That(Match(id), Is.Null);

    [Test]
    public void Base_IsNamedAndGroupedByItsTitle()
    {
        var match = Match(Base.ToLowerInvariant(), $"Zelda [{Base}][v0].XCI")!;

        Assert.That(match.BaseTitle, Is.EqualTo("Legend of Zelda, The - Tears of the Kingdom"));
        Assert.That(match.GameName, Is.EqualTo($"Legend of Zelda, The - Tears of the Kingdom [{Base}]"));
        Assert.That(match.RomFileName, Is.EqualTo($"The Legend of Zelda - Tears of the Kingdom [{Base}][v0].xci"), "™ gone, ': ' → ' - ', extension kept");
        Assert.That((match.Revision, match.Flags), Is.EqualTo(((string?)null, (IReadOnlyList<string>)[])));
        Assert.That(match.Languages, Is.EqualTo(new[] { "En", "Fr", "EN" }), "only the first letter is upper-cased, then distinct: en/En merge, EN stays");
    }

    [Test]
    public void Update_WithoutAnEntry_UsesTheBaseName()
    {
        var match = Match(Update, "zelda [v65536].nsp")!;

        Assert.That(match.RomFileName, Is.EqualTo($"The Legend of Zelda - Tears of the Kingdom [{Update}][v65536].nsp"));
        Assert.That((match.Revision, match.Flags), Is.EqualTo(("v65536", (IReadOnlyList<string>)["Update"])));
        Assert.That(match.GameName, Is.EqualTo(Match(Base)!.GameName), "the base's datGameName");
        Assert.That(match.Languages, Is.EqualTo(new[] { "En", "Fr", "EN" }));
    }

    [Test]
    public void Update_WithAVersionTag_NamesTheVersion_B6()
    {
        var match = Match(Update, "DREDGE[UPD][1.0.1].nsp")!;

        Assert.That(match.RomFileName, Is.EqualTo($"The Legend of Zelda - Tears of the Kingdom [{Update}][v0][1.0.1].nsp"));
        Assert.That(Match(Update, "zelda.nsp")!.RomFileName, Is.EqualTo($"The Legend of Zelda - Tears of the Kingdom [{Update}][v0].nsp"), "no tag, T5 as is");
    }

    [Test]
    public void BaseAndDlc_WithAVersionTag_KeepTheT5Name_B6()
    {
        Assert.That(Match(Base, "zelda [1.0.1].nsp")!.RomFileName, Is.EqualTo($"The Legend of Zelda - Tears of the Kingdom [{Base}][v0].nsp"));
        Assert.That(Match(Dlc1, "zelda [1.0.1].nsp")!.RomFileName, Is.EqualTo($"Zelda - Expansion Pass [{Dlc1}][v0].nsp"));
    }

    [Test]
    public void NamedDlc_UsesItsOwnNameAndLanguages()
    {
        var match = Match(Dlc1)!;

        Assert.That(match.RomFileName, Is.EqualTo($"Zelda - Expansion Pass [{Dlc1}][v0].nsp"));
        Assert.That(match.Flags, Is.EqualTo(new[] { "DLC" }));
        Assert.That(match.Languages, Is.EqualTo(new[] { "Ja" }));
        Assert.That(match.BaseTitle, Is.EqualTo("Legend of Zelda, The - Tears of the Kingdom"));
    }

    [Test]
    public void UnnamedDlc_UsesTheBaseNameAndLanguages()
    {
        var match = Match(UnnamedDlc)!;

        Assert.That(match.NameEntry, Is.Null);
        Assert.That(match.RomFileName, Is.EqualTo($"The Legend of Zelda - Tears of the Kingdom (DLC) [{UnnamedDlc}][v0].nsp"));
        Assert.That(match.Languages, Is.EqualTo(new[] { "En", "Fr", "EN" }));
    }

    [Test]
    public void Dlc2000_AndItsDataPatch_ShareTheBaseAndTheDlcName()
    {
        var dlc = Match(Dlc2000)!;
        var patch = Match(Dlc2000Patch)!;

        Assert.That(patch.Base, Is.SameAs(dlc.Base));
        Assert.That(patch.NameEntry, Is.SameAs(dlc.NameEntry));
        Assert.That(patch.RomFileName, Is.EqualTo($"Zelda - Final Pack [{Dlc2000Patch}][v0].nsp"), "the patch keeps its own id");
        Assert.That(patch.Languages, Is.EqualTo(new[] { "De" }));
    }

    [Test]
    public void ApplicationDelta_IsADlcNamedFromTheBase()
    {
        var match = Match(Delta)!;

        Assert.That(match.Flags, Is.EqualTo(new[] { "DLC" }));
        Assert.That(match.RomFileName, Is.EqualTo($"The Legend of Zelda - Tears of the Kingdom (DLC) [{Delta}][v0].nsp"));
    }

    [Test]
    public void DotsBecomeAnEllipsisInTheFileName_ButNotInTheTitle()
    {
        var match = Match("0100000000010000")!;

        Assert.That(match.RomFileName, Is.EqualTo("Oh…Sir! The Insult Simulator [0100000000010000][v0].nsp"));
        Assert.That(match.BaseTitle, Is.EqualTo("Oh...Sir! The Insult Simulator"));
    }

    [Test]
    public void LineBreaksCollapse()
    {
        var match = Match("0100000000020000")!;

        Assert.That(match.BaseTitle, Is.EqualTo("Baldo The guardian owls"));
        Assert.That(match.Languages, Is.Empty);
    }

    [TestCase("game [v0007].nsp", "v7")]
    [TestCase("game [v4294967296].nsp", null)]
    [TestCase("game [V3].nsp", null)]
    [TestCase("game [v0].nsp", null)]
    public void Revision(string fileName, string? expected)
    {
        var match = Match(Base, fileName)!;

        Assert.That(match.Revision, Is.EqualTo(expected));
        if (expected is null)
            Assert.That(match.RomFileName, Does.Contain("[v0]"));
    }
}
