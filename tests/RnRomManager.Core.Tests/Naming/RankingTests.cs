using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Tests.Naming;

/// <summary>Spec §6.1.1 revision order and §6.1 primary release ranking.</summary>
public class RankingTests
{
    [TestCase(null, "Rev 1")]
    [TestCase("Rev 1", "Rev 2")]
    [TestCase("Rev A", "Rev B")]
    [TestCase("v1.9", "v1.10")]
    [TestCase("v1.0", "v1.0.1")]
    [TestCase("v1.6", "v1.6b")]
    [TestCase("v1.21", "v1.995")]
    [TestCase("v1.2", "v2.0")]
    [TestCase("v0.0.1.0", "v0.1")]
    public void Revision_LowerThenHigher(string? lower, string higher)
    {
        Assert.That(RevisionComparer.Instance.Compare(lower, higher), Is.Negative);
        Assert.That(RevisionComparer.Instance.Compare(higher, lower), Is.Positive);
    }

    [TestCase(null, null)]
    [TestCase("Rev 1", "Rev 1")]
    [TestCase("v1.0", "v1.0")]
    public void Revision_Equal(string? x, string? y) =>
        Assert.That(RevisionComparer.Instance.Compare(x, y), Is.Zero);

    private static RankedRelease Release(string fileName, string[]? regions = null, string[]? languages = null,
        string? revision = null, params string[] flags) =>
        new(fileName, regions ?? ["USA"], languages ?? [], revision, flags);

    private static string Primary(params RankedRelease[] releases) =>
        PrimaryReleaseRanker.Primary(releases, r => r).FileName;

    [Test]
    public void HacksAndTranslations_NeverWin()
    {
        Assert.That(Primary(Release("hack", flags: "Hack"), Release("proto", flags: "Proto")), Is.EqualTo("proto"));
        Assert.That(Primary(Release("tr", ["World"], flags: "Translated"), Release("jp", ["Japan"])), Is.EqualTo("jp"));
    }

    [TestCase("Beta")]
    [TestCase("Beta 2")]
    [TestCase("Proto")]
    [TestCase("Possible Proto")]
    [TestCase("Demo")]
    [TestCase("Sample")]
    [TestCase("Pirate")]
    [TestCase("Unverified")]
    public void StatusFlags_LoseToACleanDump(string flag)
    {
        Assert.That(Primary(Release("flagged", ["World"], flags: flag), Release("clean", ["Japan"])), Is.EqualTo("clean"));
    }

    [Test]
    public void OtherFlags_DoNotCount()
    {
        Assert.That(Primary(Release("sgb", ["World"], flags: "SGB Enhanced"), Release("usa", ["USA"])), Is.EqualTo("sgb"));
    }

    [Test]
    public void RegionOrder_WorldUsaEuropeEnglishJapanOther()
    {
        RankedRelease[] all =
        [
            Release("other", ["Germany"]),
            Release("japan", ["Japan"]),
            Release("english", ["Germany"], ["En", "De"]),
            Release("europe", ["Europe"]),
            Release("usa", ["USA"]),
            Release("world", ["World"]),
        ];

        Assert.That(all.Order(PrimaryReleaseRanker.Instance).Select(r => r.FileName),
            Is.EqualTo(new[] { "world", "usa", "europe", "english", "japan", "other" }));
    }

    [Test]
    public void MultiRegionRelease_TakesItsBestRegion()
    {
        Assert.That(Primary(Release("eu", ["Europe"]), Release("jp-us", ["Japan", "USA"])), Is.EqualTo("jp-us"));
    }

    [Test]
    public void HighestRevision_ThenFileName()
    {
        Assert.That(Primary(Release("b", revision: "Rev 1"), Release("a"), Release("c", revision: "Rev 2")), Is.EqualTo("c"));
        Assert.That(Primary(Release("b"), Release("a")), Is.EqualTo("a"));
    }

    [Test]
    public void UnverifiedNeverRanksAboveAVerifiedDump()
    {
        // D30: a suggestion-picked release is Unverified even with a better region and revision.
        var unverified = Release("World (Rev 9) (Unverified)", ["World"], revision: "Rev 9", flags: "Unverified");
        var verified = Release("Japan", ["Japan"]);

        Assert.That(Primary(unverified, verified), Is.EqualTo("Japan"));
    }
}
