namespace RnRomManager.Core.Ingest;

/// <summary>One loose file of an ingest folder, as the planner sees it.</summary>
/// <param name="CueBytes">
/// For a <c>.cue</c> of at most <see cref="IngestSetPlanner.MaxCueBytes"/>: its bytes, or null when it couldn't be read.
/// Ignored for other files.
/// </param>
public sealed record PlannerFile(string Name, long Size, byte[]? CueBytes = null);

public enum PlannedKind
{
    /// <summary>A complete bin/cue set: processed as one unit.</summary>
    Set,

    /// <summary>A file no cue names: processed on its own as today.</summary>
    Lone,

    /// <summary>Present members of a set that can't be processed; each is <c>Skipped</c> with <see cref="PlannedUnit.Reason"/> and stays.</summary>
    Skipped,
}

/// <param name="Members">File names as on disk. For a set: the cue first, then its <c>FILE</c> names in order.</param>
/// <param name="Reason">An <see cref="IngestReason"/> for <see cref="PlannedKind.Skipped"/>; null otherwise.</param>
public sealed record PlannedUnit(PlannedKind Kind, IReadOnlyList<string> Members, string? Reason = null);

/// <summary>
/// B7: groups one <c>FileSets</c> folder's loose files into bin/cue sets, lone files and skipped members. Pure: the
/// caller reads each small cue and passes its bytes. Units come in the scanner's ordinal name order, each at the
/// position of its first member; every file appears in exactly one unit.
/// </summary>
public static class IngestSetPlanner
{
    /// <summary>A larger <c>.cue</c> isn't parsed; it is a lone file.</summary>
    public const long MaxCueBytes = 64 * 1024;

    /// <param name="files">Every loose file of the folder, whatever the selection (B7 plans from all of them).</param>
    /// <param name="selected">The selected names (exact spelling), or null for the whole folder.</param>
    public static IReadOnlyList<PlannedUnit> Plan(IReadOnlyList<PlannerFile> files, IReadOnlySet<string>? selected = null)
    {
        var ordered = files.Select(f => f.Name).Order(StringComparer.Ordinal).ToList();
        var position = ordered.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);
        var byFolded = ordered.ToLookup(n => n, StringComparer.OrdinalIgnoreCase);

        var parsed = new List<(string Cue, CueSheet Sheet)>();
        var broken = new List<string>();
        foreach (var file in files.Where(f => IsExtension(f.Name, ".cue") && f.Size <= MaxCueBytes).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var sheet = file.CueBytes is null ? null : CueSheet.Parse(file.CueBytes);
            if (sheet is { IsValid: true })
                parsed.Add((file.Name, sheet));
            else
                broken.Add(file.Name);
        }

        // How many parsed cues name each file (by folded name): a file named by two is a SetConflict for both.
        var namedBy = parsed.SelectMany(p => p.Sheet.Files.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var units = new List<PlannedUnit>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        void Add(PlannedKind kind, IEnumerable<string> members, string? reason = null)
        {
            var list = members.Where(claimed.Add).ToList();
            if (list.Count > 0)
                units.Add(new PlannedUnit(kind, list, reason));
        }

        foreach (var (cue, sheet) in parsed)
        {
            var present = new List<string> { cue };
            var missing = false;
            var conflict = false;
            foreach (var name in sheet.Files)
            {
                var matches = byFolded[name].ToList();
                missing |= matches.Count == 0;
                conflict |= matches.Count > 1 || namedBy[name] > 1;
                present.AddRange(matches);
            }
            var unsupported = CueSheet.NameProblem(cue) is not null
                || sheet.Files.Any(n => !IsExtension(n, ".bin") || string.Equals(n, cue, StringComparison.OrdinalIgnoreCase));

            var reason = unsupported ? IngestReason.UnsupportedSetMember
                : conflict ? IngestReason.SetConflict
                : missing ? IngestReason.IncompleteSet
                : null;
            Add(reason is null ? PlannedKind.Set : PlannedKind.Skipped, present.Distinct(StringComparer.Ordinal), reason);
        }

        // A broken cue has no member list: it and every .bin starting with its stem wait, unless a parsed cue names that .bin.
        foreach (var cue in broken)
        {
            var stem = Path.GetFileNameWithoutExtension(cue);
            Add(PlannedKind.Skipped, ordered.Where(n => n == cue || (IsExtension(n, ".bin") && !namedBy.ContainsKey(n)
                && n.StartsWith(stem, StringComparison.OrdinalIgnoreCase))), IngestReason.IncompleteSet);
        }

        foreach (var name in ordered)
            Add(PlannedKind.Lone, [name]);

        return units
            .Where(u => selected is null || u.Members.Any(selected.Contains))
            .OrderBy(u => u.Members.Min(m => position[m]))
            .ToList();
    }

    private static bool IsExtension(string name, string extension) => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
