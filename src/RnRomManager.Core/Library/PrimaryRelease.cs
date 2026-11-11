using RnRomManager.Core.Dats;
using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Library;

/// <summary>Spec §6.1: exactly one <c>isPrimary</c> release, recomputed on every write; T6 for titledb games.</summary>
public static class PrimaryRelease
{
    public static RankedRelease Rank(InfoJsonRom r) => new(r.FileName, r.Regions, r.Languages, r.Revision, r.Flags);

    /// <summary>
    /// The releases best first: by <see cref="PrimaryReleaseRanker"/>, and for a titledb game (T6) every release that
    /// is neither an <c>Update</c> nor a <c>DLC</c> ahead of all that are.
    /// </summary>
    public static IOrderedEnumerable<InfoJsonRom> Order(IEnumerable<InfoJsonRom> releases, DatSource source) => source == DatSource.TitleDb
        ? releases.OrderBy(IsAddOn).ThenBy(Rank, PrimaryReleaseRanker.Instance)
        : releases.OrderBy(Rank, PrimaryReleaseRanker.Instance);

    /// <summary><paramref name="releases"/> (non-empty) with <c>isPrimary</c> set on the first of <see cref="Order"/> only.</summary>
    public static IReadOnlyList<InfoJsonRom> Apply(IReadOnlyList<InfoJsonRom> releases, DatSource source)
    {
        var primary = Order(releases, source).First();
        return releases.Select(r => r with { IsPrimary = ReferenceEquals(r, primary) }).ToList();
    }

    private static bool IsAddOn(InfoJsonRom r) => r.Flags.Contains("Update", StringComparer.Ordinal) || r.Flags.Contains("DLC", StringComparer.Ordinal);
}
