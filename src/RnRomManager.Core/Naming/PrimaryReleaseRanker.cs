namespace RnRomManager.Core.Naming;

/// <summary>
/// Spec §6.1 primary release, recomputed on every write (exactly one per game). Best first: not Hack/Translated →
/// no status flag → region World &gt; USA &gt; Europe &gt; other with <c>En</c> &gt; Japan &gt; other → highest
/// revision → <c>fileName</c> ordinal.
/// </summary>
public sealed class PrimaryReleaseRanker : IComparer<RankedRelease>
{
    public const string Hack = "Hack";
    public const string Translated = "Translated";
    public const string Unverified = "Unverified";

    private static readonly string[] StatusFlagPrefixes = ["Beta", "Proto", "Possible Proto", "Demo", "Sample", "Pirate", Unverified];

    public static PrimaryReleaseRanker Instance { get; } = new();

    /// <summary>The primary release of a non-empty game.</summary>
    public static T Primary<T>(IEnumerable<T> releases, Func<T, RankedRelease> describe) =>
        releases.OrderBy(describe, Instance).First();

    /// <summary>Negative when <paramref name="x"/> ranks above <paramref name="y"/>.</summary>
    public int Compare(RankedRelease? x, RankedRelease? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        var result = IsHackOrTranslation(x).CompareTo(IsHackOrTranslation(y));
        if (result == 0)
            result = HasStatusFlag(x).CompareTo(HasStatusFlag(y));
        if (result == 0)
            result = RegionRank(x).CompareTo(RegionRank(y));
        if (result == 0)
            result = RevisionComparer.Instance.Compare(y.Revision, x.Revision); // highest first
        return result != 0 ? result : string.CompareOrdinal(x.FileName, y.FileName);
    }

    private static bool IsHackOrTranslation(RankedRelease r) => r.Flags.Any(f => f is Hack or Translated);

    private static bool HasStatusFlag(RankedRelease r) =>
        r.Flags.Any(f => StatusFlagPrefixes.Any(p => f.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>0 World … 5 other; a multi-region release takes its best region.</summary>
    private static int RegionRank(RankedRelease r)
    {
        var hasEnglish = r.Languages.Contains("En");
        return r.Regions.Select(region => region switch
        {
            "World" => 0,
            "USA" => 1,
            "Europe" => 2,
            "Japan" => 4,
            _ => hasEnglish ? 3 : 5,
        }).DefaultIfEmpty(hasEnglish ? 3 : 5).Min();
    }
}
