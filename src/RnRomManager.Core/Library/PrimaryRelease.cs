using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Library;

/// <summary>Spec §6.1: exactly one <c>isPrimary</c> release, recomputed on every write.</summary>
public static class PrimaryRelease
{
    public static RankedRelease Rank(InfoJsonRom r) => new(r.FileName, r.Regions, r.Languages, r.Revision, r.Flags);

    /// <summary><paramref name="releases"/> (non-empty) with <c>isPrimary</c> set on the best-ranked one only.</summary>
    public static IReadOnlyList<InfoJsonRom> Apply(IReadOnlyList<InfoJsonRom> releases)
    {
        var primary = PrimaryReleaseRanker.Primary(releases, Rank);
        return releases.Select(r => r with { IsPrimary = ReferenceEquals(r, primary) }).ToList();
    }
}
