using RnRomManager.Core.Dats;
using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Library;

/// <summary>Spec §6.1: exactly one <c>isPrimary</c> release, recomputed on every write; T6 for titledb games.</summary>
public static class PrimaryRelease
{
    public static RankedRelease Rank(InfoJsonRom r) => new(r.FileName, r.Regions, r.Languages, r.Revision, r.Flags);

    /// <summary>
    /// The releases best first: by <see cref="PrimaryReleaseRanker"/>, and for a titledb game (T6) every release that
    /// is neither an <c>Update</c> nor a <c>DLC</c> ahead of all that are; for a MAME game (Arcade G7) parents (header
    /// <c>cloneOf</c> null, or no header) ahead of clones.
    /// </summary>
    public static IOrderedEnumerable<InfoJsonRom> Order(IEnumerable<InfoJsonRom> releases, DatSource source) => source switch
    {
        DatSource.TitleDb => releases.OrderBy(IsAddOn).ThenBy(Rank, PrimaryReleaseRanker.Instance),
        DatSource.Mame or DatSource.Fbneo => releases.OrderBy(IsClone).ThenBy(Rank, PrimaryReleaseRanker.Instance),
        _ => releases.OrderBy(Rank, PrimaryReleaseRanker.Instance),
    };

    /// <summary>
    /// <paramref name="releases"/> (non-empty) with <c>isPrimary</c> set on one only: the release whose SHA1 is
    /// <paramref name="chosenSha1"/> (<see cref="InfoJsonGame.ChosenPrimarySha1"/>) when listed, else the first of <see cref="Order"/>.
    /// </summary>
    public static IReadOnlyList<InfoJsonRom> Apply(IReadOnlyList<InfoJsonRom> releases, DatSource source, string? chosenSha1 = null)
    {
        var primary = releases.FirstOrDefault(r => chosenSha1 is not null && r.Metadata.Sha1 == chosenSha1)
            ?? Order(releases, source).First();
        return releases.Select(r => r with { IsPrimary = ReferenceEquals(r, primary) }).ToList();
    }

    private static bool IsClone(InfoJsonRom r) =>
        r.Header?["cloneOf"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var parent) && parent.Length > 0;

    private static bool IsAddOn(InfoJsonRom r) => r.Flags.Contains("Update", StringComparer.Ordinal) || r.Flags.Contains("DLC", StringComparer.Ordinal);
}
