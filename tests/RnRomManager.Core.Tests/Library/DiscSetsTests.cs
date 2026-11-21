using RnRomManager.Core.Library;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Multi-disc M1–M2: which releases form a disc set, and the set's playlist name.</summary>
public class DiscSetsTests
{
    private static long _nextId = 1;

    private static RomRow Rom(string fileName, string[] flags, string[]? regions = null, string[]? languages = null,
        string? revision = null, string? version = null, string baseTitle = "Game") => new()
    {
        Id = _nextId++, GameId = Guid.Empty, SystemId = "psx", FileName = fileName, OriginalFileName = fileName,
        BaseTitle = baseTitle, Regions = regions ?? ["USA"], Languages = languages ?? [], Flags = flags,
        RegionKey = "", Revision = revision, Version = version, IdMethod = IdentificationMethod.Dat,
        SizeBytes = 1, Crc32 = "00000000", Md5 = new string('0', 32), Sha1 = new string('0', 40),
        DateAdded = default, DateModified = default,
    };

    private static RomFileRow File(RomRow rom, int ordinal, string fileName) => new()
    {
        RomId = rom.Id, GameId = Guid.Empty, RomSha1 = rom.Sha1, Ordinal = ordinal, FileName = fileName,
        ExternalFileName = fileName, OriginalFileName = fileName, SizeBytes = 1, Crc32 = "00000000",
        Md5 = new string('0', 32), Sha1 = new string('0', 40),
    };

    private static readonly ILookup<long, RomFileRow> NoFiles = Array.Empty<RomFileRow>().ToLookup(f => f.RomId);

    private static RomRow Disc(int n, string ext = ".chd", string region = "USA", string[]? extra = null) =>
        Rom($"Game ({region}) (Disc {n}){ext}", [$"Disc {n}", .. extra ?? []], [region]);

    [Test]
    public void ThreeDiscs_WithAMultiTrackCueDiscOne_AreOneSet_InDiscOrder_NamedFromTheCue()
    {
        var one = Rom("Game (USA) (Disc 1) (Track 1).bin", ["Disc 1"]);
        var two = Disc(2);
        var three = Disc(3);
        var files = new[] { File(one, 0, "Game (USA) (Disc 1).cue"), File(one, 1, "Game (USA) (Disc 1) (Track 2).bin") }.ToLookup(f => f.RomId);

        var sets = DiscSets.Find([three, one, two], files);

        Assert.That(sets, Has.Count.EqualTo(1));
        Assert.That(sets[0].Discs, Is.EqualTo(new[] { one, two, three }));
        Assert.That(sets[0].Name, Is.EqualTo("Game (USA).m3u"));
    }

    [Test]
    public void AStandaloneDiscOne_NamesTheSetFromItsFileName_IgnoringTheTokensCase()
    {
        var one = Rom("Game (USA) (DISC 1).iso", ["disc 01"]);

        var sets = DiscSets.Find([one, Disc(2, ".img")], NoFiles);

        Assert.That(sets.Single().Name, Is.EqualTo("Game (USA).m3u"));
    }

    [Test]
    public void ANameWithoutTheToken_KeepsItsStem()
    {
        var one = Rom("Mystery Disc A.chd", ["Disc 1"]);

        Assert.That(DiscSets.Find([one, Disc(2)], NoFiles).Single().Name, Is.EqualTo("Mystery Disc A.m3u"));
    }

    [Test]
    public void EightDiscs_AreASet_AndNineAreNot()
    {
        Assert.That(DiscSets.Find([.. Enumerable.Range(1, 8).Select(n => Disc(n))], NoFiles).Single().Discs, Has.Count.EqualTo(8));
        Assert.That(DiscSets.Find([.. Enumerable.Range(1, 9).Select(n => Disc(n))], NoFiles), Is.Empty);
    }

    [Test]
    public void AGap_ADuplicate_OrASingleDisc_IsNoSet()
    {
        Assert.That(DiscSets.Find([Disc(1), Disc(3)], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find([Disc(1), Disc(2), Disc(2, ".bin")], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find([Disc(1)], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find([Disc(2), Disc(3)], NoFiles), Is.Empty);
    }

    [TestCase(".pbp")]
    [TestCase(".nrg")]
    [TestCase(".cue")]
    public void AMemberWhoseMainFileIsNotABinIsoChdOrImg_SpoilsTheSet(string ext)
    {
        Assert.That(DiscSets.Find([Disc(1), Disc(2, ext)], NoFiles), Is.Empty);
    }

    [Test]
    public void TwoRegions_AreTwoSets()
    {
        var sets = DiscSets.Find([Disc(1), Disc(2), Disc(1, region: "Europe"), Disc(2, region: "Europe")], NoFiles);

        Assert.That(sets.Select(s => s.Name), Is.EquivalentTo(new[] { "Game (USA).m3u", "Game (Europe).m3u" }));
    }

    [Test]
    public void DifferingLanguages_Revision_Version_OtherFlagsOrTitle_SplitTheGroup()
    {
        var one = Disc(1);
        Assert.That(DiscSets.Find([one, Rom("b.chd", ["Disc 2"], languages: ["En"])], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find([one, Rom("b.chd", ["Disc 2"], revision: "1")], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find([one, Rom("b.chd", ["Disc 2"], version: "1.1")], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find([one, Disc(2, extra: ["Demo"])], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find([one, Rom("b.chd", ["Disc 2"], baseTitle: "Other")], NoFiles), Is.Empty);
    }

    [Test]
    public void OtherFlags_AreKeptInTheKey_WhenBothDiscsHaveThem()
    {
        Assert.That(DiscSets.Find([Disc(1, extra: ["Demo"]), Disc(2, extra: ["Demo"])], NoFiles), Has.Count.EqualTo(1));
    }

    [Test]
    public void AReleaseWithTwoDiscFlags_IsNoMember()
    {
        Assert.That(DiscSets.Find([Disc(1), Rom("b.chd", ["Disc 2", "Disc 3"])], NoFiles), Is.Empty);
    }

    [TestCase(new[] { "Disc 2" }, 2)]
    [TestCase(new[] { "Demo", "disc 07" }, 7)]
    [TestCase(new[] { "Disc" }, null)]
    [TestCase(new[] { "Disc 2a" }, null)]
    [TestCase(new string[0], null)]
    public void DiscNumber_FollowsTheFlagRule(string[] flags, int? expected)
    {
        Assert.That(DiscSets.DiscNumber(flags), Is.EqualTo(expected));
    }
}
