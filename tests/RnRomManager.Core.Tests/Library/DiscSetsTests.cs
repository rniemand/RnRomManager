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

        var sets = DiscSets.Find("psx", [three, one, two], files);

        Assert.That(sets, Has.Count.EqualTo(1));
        Assert.That(sets[0].Discs, Is.EqualTo(new[] { one, two, three }));
        Assert.That(sets[0].Name, Is.EqualTo("Game (USA).m3u"));
    }

    [Test]
    public void AStandaloneDiscOne_NamesTheSetFromItsFileName_IgnoringTheTokensCase()
    {
        var one = Rom("Game (USA) (DISC 1).iso", ["disc 01"]);

        var sets = DiscSets.Find("psx", [one, Disc(2, ".img")], NoFiles);

        Assert.That(sets.Single().Name, Is.EqualTo("Game (USA).m3u"));
    }

    [Test]
    public void ANameWithoutTheToken_KeepsItsStem()
    {
        var one = Rom("Mystery Disc A.chd", ["Disc 1"]);

        Assert.That(DiscSets.Find("psx", [one, Disc(2)], NoFiles).Single().Name, Is.EqualTo("Mystery Disc A.m3u"));
    }

    [Test]
    public void EightDiscs_AreASet_AndNineAreNot()
    {
        Assert.That(DiscSets.Find("psx", [.. Enumerable.Range(1, 8).Select(n => Disc(n))], NoFiles).Single().Discs, Has.Count.EqualTo(8));
        Assert.That(DiscSets.Find("psx", [.. Enumerable.Range(1, 9).Select(n => Disc(n))], NoFiles), Is.Empty);
    }

    [Test]
    public void AGap_ADuplicate_OrASingleDisc_IsNoSet()
    {
        Assert.That(DiscSets.Find("psx", [Disc(1), Disc(3)], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find("psx", [Disc(1), Disc(2), Disc(2, ".bin")], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find("psx", [Disc(1)], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find("psx", [Disc(2), Disc(3)], NoFiles), Is.Empty);
    }

    [TestCase(".pbp")]
    [TestCase(".nrg")]
    [TestCase(".cue")]
    public void AMemberWhoseMainFileIsNotABinIsoChdOrImg_SpoilsTheSet(string ext)
    {
        Assert.That(DiscSets.Find("psx", [Disc(1), Disc(2, ext)], NoFiles), Is.Empty);
    }

    [Test]
    public void TwoRegions_AreTwoSets()
    {
        var sets = DiscSets.Find("psx", [Disc(1), Disc(2), Disc(1, region: "Europe"), Disc(2, region: "Europe")], NoFiles);

        Assert.That(sets.Select(s => s.Name), Is.EquivalentTo(new[] { "Game (USA).m3u", "Game (Europe).m3u" }));
    }

    [Test]
    public void DifferingLanguages_Revision_Version_OtherFlagsOrTitle_SplitTheGroup()
    {
        var one = Disc(1);
        Assert.That(DiscSets.Find("psx", [one, Rom("b.chd", ["Disc 2"], languages: ["En"])], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find("psx", [one, Rom("b.chd", ["Disc 2"], revision: "1")], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find("psx", [one, Rom("b.chd", ["Disc 2"], version: "1.1")], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find("psx", [one, Disc(2, extra: ["Demo"])], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find("psx", [one, Rom("b.chd", ["Disc 2"], baseTitle: "Other")], NoFiles), Is.Empty);
    }

    [Test]
    public void OtherFlags_AreKeptInTheKey_WhenBothDiscsHaveThem()
    {
        Assert.That(DiscSets.Find("psx", [Disc(1, extra: ["Demo"]), Disc(2, extra: ["Demo"])], NoFiles), Has.Count.EqualTo(1));
    }

    [Test]
    public void AReleaseWithTwoDiscFlags_IsNoMember()
    {
        Assert.That(DiscSets.Find("psx", [Disc(1), Rom("b.chd", ["Disc 2", "Disc 3"])], NoFiles), Is.Empty);
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

    // Saturn and Sega CD S1: each system's limit and main-file extensions.

    [TestCase("psx", 8)]
    [TestCase("saturn", 5)]
    [TestCase("segacd", 4)]
    [TestCase("amiga", 20)]
    [TestCase("3do", 0)]
    [TestCase("gb", 0)]
    public void MaxDiscs_IsPerSystem(string systemId, int expected)
    {
        Assert.That(DiscSets.MaxDiscs(systemId), Is.EqualTo(expected));
    }

    [TestCase("saturn", 5)]
    [TestCase("segacd", 4)]
    public void SaturnAndSegaCd_TakeUpToTheirLimit_AndNoMore(string systemId, int limit)
    {
        Assert.That(DiscSets.Find(systemId, [.. Enumerable.Range(1, limit).Select(n => Disc(n))], NoFiles).Single().Discs, Has.Count.EqualTo(limit));
        Assert.That(DiscSets.Find(systemId, [.. Enumerable.Range(1, limit + 1).Select(n => Disc(n))], NoFiles), Is.Empty);
    }

    [TestCase("saturn", ".bin")]
    [TestCase("saturn", ".iso")]
    [TestCase("segacd", ".bin")]
    [TestCase("segacd", ".chd")]
    public void SaturnAndSegaCd_AcceptBinIsoAndChdDiscs(string systemId, string ext)
    {
        Assert.That(DiscSets.Find(systemId, [Disc(1, ext), Disc(2)], NoFiles), Has.Count.EqualTo(1));
    }

    [TestCase("saturn", ".img")]
    [TestCase("saturn", ".nrg")]
    [TestCase("segacd", ".img")]
    [TestCase("segacd", ".nrg")]
    public void AnImgOrNrgDisc_SpoilsASaturnOrSegaCdSet(string systemId, string ext)
    {
        Assert.That(DiscSets.Find(systemId, [Disc(1), Disc(2, ext)], NoFiles), Is.Empty);
    }

    [TestCase("3do")]
    [TestCase("gb")]
    public void AnotherSystem_HasNoSets(string systemId)
    {
        Assert.That(DiscSets.Find(systemId, [Disc(1), Disc(2)], NoFiles), Is.Empty);
    }

    // Amiga F1–F2: floppy disk sets from TOSEC file names.

    private static RomRow Disk(string fileName) => Rom(fileName, [], [], baseTitle: "Allo");

    private static string Tosec(int n, int m, string tags = "", string ext = ".adf") =>
        $"'Allo 'Allo! Cartoon Fun! (1993)(Alternative)(Disk {n} of {m}){tags}{ext}";

    [Test]
    public void Amiga_TwoDisks_DifferingOnlyInTags_AreOneSet_NamedFromDiskOne()
    {
        var one = Disk(Tosec(1, 2, "[cr CSL]"));
        var two = Disk(Tosec(2, 2));

        var sets = DiscSets.Find("amiga", [two, one], NoFiles);

        Assert.That(sets, Has.Count.EqualTo(1));
        Assert.That(sets[0].Discs, Is.EqualTo(new[] { one, two }));
        Assert.That(sets[0].Name, Is.EqualTo("'Allo 'Allo! Cartoon Fun! (1993)(Alternative)[cr CSL].m3u"));
    }

    [Test]
    public void Amiga_ThreeDisksOutOfOrder_ComeBackInDiskOrder()
    {
        var one = Disk(Tosec(1, 3));
        var two = Disk(Tosec(2, 3));
        var three = Disk(Tosec(3, 3));

        Assert.That(DiscSets.Find("amiga", [three, one, two], NoFiles).Single().Discs, Is.EqualTo(new[] { one, two, three }));
    }

    private static IEnumerable<TestCaseData> AmigaNonSets()
    {
        yield return new TestCaseData((object)new[] { Tosec(1, 3), Tosec(2, 3) }).SetName("Amiga_AMissingDisk_IsNoSet");
        yield return new TestCaseData((object)new[] { Tosec(1, 2, "[cr CSL]"), Tosec(1, 2, "[cr QTX]"), Tosec(2, 2) })
            .SetName("Amiga_TwoCandidatesForOneDisk_AreNoSet");
        yield return new TestCaseData((object)new[] { Tosec(1, 2) }).SetName("Amiga_ASingleDisk_IsNoSet");
        yield return new TestCaseData((object)new[] { Tosec(1, 2), Tosec(3, 2) }).SetName("Amiga_ANumberAboveTheTotal_IsNoSet");
        yield return new TestCaseData((object)new[] { Tosec(1, 2) + " (Disk 1 of 2).adf", Tosec(2, 2) }).SetName("Amiga_TwoTokensInOneName_AreNoSet");
        yield return new TestCaseData((object)new[] { Tosec(1, 2), Tosec(2, 2, ext: ".adz") }).SetName("Amiga_ANonAdfMember_IsNoSet");
    }

    [TestCaseSource(nameof(AmigaNonSets))]
    public void Amiga_GroupsThatAreNotSets(string[] fileNames)
    {
        Assert.That(DiscSets.Find("amiga", fileNames.Select(Disk).ToList(), NoFiles), Is.Empty);
    }

    [TestCase(20, true)]
    [TestCase(21, false)]
    public void Amiga_TakesUpToTwentyDisks(int total, bool isSet)
    {
        var disks = Enumerable.Range(1, total).Select(n => Disk(Tosec(n, total))).ToList();

        Assert.That(DiscSets.Find("amiga", disks, NoFiles), Has.Count.EqualTo(isSet ? 1 : 0));
    }

    [Test]
    public void Amiga_ZeroPaddedNumbers_AndTheTokensCase_AreIgnored()
    {
        var one = Disk("Game (1990)(Synth)(DISK 01 of 02).adf");
        var two = Disk("Game (1990)(Synth)(disk 2 OF 2).adf");

        Assert.That(DiscSets.Find("amiga", [one, two], NoFiles).Single().Name, Is.EqualTo("Game (1990)(Synth).m3u"));
    }

    [Test]
    public void Amiga_TwoPublishersInOneGame_AreTwoSets()
    {
        var a = new[] { Disk("Game (1990)(Synth)(Disk 1 of 2).adf"), Disk("Game (1990)(Synth)(Disk 2 of 2).adf") };
        var b = new[] { Disk("Game (1991)(Other)(Disk 1 of 2).adf"), Disk("Game (1991)(Other)(Disk 2 of 2).adf") };

        Assert.That(DiscSets.Find("amiga", [.. a, .. b], NoFiles).Select(s => s.Name),
            Is.EquivalentTo(new[] { "Game (1990)(Synth).m3u", "Game (1991)(Other).m3u" }));
    }

    [Test]
    public void Amiga_DiscFlagsAndOtherSystemsTokens_DontCross()
    {
        // A "(Disk n of m)" name on another system, or Disc flags on Amiga, makes no set.
        Assert.That(DiscSets.Find("psx", [Disk(Tosec(1, 2)), Disk(Tosec(2, 2))], NoFiles), Is.Empty);
        Assert.That(DiscSets.Find("amiga", [Disc(1, ".adf"), Disc(2, ".adf")], NoFiles), Is.Empty);
    }

    [TestCase("Game (1990)(Synth)(Disk 3 of 4).adf", 3)]
    [TestCase("Game (1990)(Synth)(disk 01 of 02)[cr].adf", 1)]
    [TestCase("Game (1990)(Synth)(Disk 3 of 2).adf", null)]
    [TestCase("Game (1990)(Synth)(Disk 0 of 2).adf", null)]
    [TestCase("Game (1990)(Synth).adf", null)]
    [TestCase("Game (Disk 1 of 2)(Disk 2 of 2).adf", null)]
    public void DiskNumber_FollowsTheTokenRule(string fileName, int? expected)
    {
        Assert.That(DiscSets.DiskNumber(fileName), Is.EqualTo(expected));
    }
}
