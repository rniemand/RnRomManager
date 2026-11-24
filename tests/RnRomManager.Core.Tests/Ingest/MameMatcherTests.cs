using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Core.Tests.Ingest;

public class MameMatcherTests
{
    private static MameRom Rom(string name, string? crc, string? merge = null, string? bios = null, bool noDump = false) =>
        new(name, 1, crc, merge, bios, noDump);

    private static MameSet Set(params MameRom[] roms) => new("s", "S", null, null, null, null, false, roms);

    private static MameEntry[] Entries(params string[] crcs) => crcs.Select((c, i) => new MameEntry($"file{i}", c)).ToArray();

    // sf2ua: two own ROMs, one merged from sf2.
    private static readonly MameSet Clone = Set(Rom("own1", "00000001"), Rom("own2", "00000002"), Rom("shared", "00000003", merge: "shared"));

    [Test]
    public void SplitClone_IsCompleteButNotSelfContained()
    {
        var match = MameMatcher.Match(Clone, Entries("00000001", "00000002"));

        Assert.That(match, Is.EqualTo(new MameMatch(true, [], false)).Using<MameMatch>(Same));
    }

    [Test]
    public void NonMergedClone_IsSelfContained()
    {
        var match = MameMatcher.Match(Clone, Entries("00000001", "00000002", "00000003", "000000ff"));

        Assert.That((match.Complete, match.SelfContained), Is.EqualTo((true, true)), "extra entries are allowed");
    }

    [Test]
    public void RenamedEntries_StillMatch_ByCrc()
    {
        var match = MameMatcher.Match(Clone, [new MameEntry("wrong.name", "00000002"), new MameEntry("other", "00000001")]);

        Assert.That(match.Complete, Is.True);
    }

    [Test]
    public void MissingRoms_AreListedInDatOrder()
    {
        var set = Set(Rom("c", "0000000c"), Rom("a", "0000000a"), Rom("b", "0000000b"));

        var match = MameMatcher.Match(set, Entries("0000000a"));

        Assert.That((match.Complete, match.Missing), Is.EqualTo((false, new[] { "c", "b" })).Using<(bool, IReadOnlyList<string>)>(SameTuple));
    }

    [Test]
    public void NoDumpRoms_AreIgnored()
    {
        var set = Set(Rom("a", "0000000a"), Rom("bad", null, noDump: true));

        Assert.That(MameMatcher.Match(set, Entries("0000000a")).Complete, Is.True);
    }

    [Test]
    public void BiosChoiceRoms_AreNotRequired_ButOneMakesItSelfContained()
    {
        var set = Set(Rom("game", "00000001"), Rom("euro", "000000e0", bios: "euro"), Rom("us", "000000a0", bios: "us"));

        var without = MameMatcher.Match(set, Entries("00000001"));
        var with = MameMatcher.Match(set, Entries("00000001", "000000a0"));

        Assert.That((without.Complete, without.SelfContained), Is.EqualTo((true, false)));
        Assert.That((with.Complete, with.SelfContained), Is.EqualTo((true, true)));
    }

    [Test]
    public void AllMergedSet_NeedsOneOfItsRoms()
    {
        var set = Set(Rom("m1", "00000001", merge: "m1"), Rom("m2", "00000002", merge: "m2"));

        var none = MameMatcher.Match(set, Entries("000000ff"));
        var one = MameMatcher.Match(set, Entries("00000002"));

        Assert.That((none.Complete, none.Missing), Is.EqualTo((false, new[] { "m1", "m2" })).Using<(bool, IReadOnlyList<string>)>(SameTuple));
        Assert.That((one.Complete, one.Missing.Count, one.SelfContained), Is.EqualTo((true, 0, false)));
    }

    [Test]
    public void SetWithNoDumpedRoms_NeverMatches()
    {
        var match = MameMatcher.Match(Set(Rom("bad", null, noDump: true)), Entries("00000001"));

        Assert.That((match.Complete, match.Missing.Count), Is.EqualTo((false, 0)));
    }

    private static bool Same(MameMatch a, MameMatch b) => a.Complete == b.Complete && a.SelfContained == b.SelfContained && a.Missing.SequenceEqual(b.Missing);

    private static bool SameTuple((bool, IReadOnlyList<string>) a, (bool, IReadOnlyList<string>) b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2);
}
