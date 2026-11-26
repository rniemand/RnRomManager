namespace RnRomManager.Core.Ingest;

/// <summary>
/// B7, CloneCD B5, file type gaps D8: groups one <c>FileSets</c> folder's loose files into cue, TOC, CloneCD and MDS sets, lone
/// files and skipped members. Pure: the caller reads each small cue, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c> and passes its bytes. Units come in the scanner's ordinal name order, each at the
/// position of its first member; every file appears in exactly one unit.
/// </summary>
public static class IngestSetPlanner
{
    /// <summary>A larger <c>.cue</c> isn't parsed; it is a lone file.</summary>
    public const long MaxCueBytes = 64 * 1024;

    /// <param name="files">Every loose file of the folder, whatever the selection (B7 plans from all of them).</param>
    /// <param name="selected">The selected names (exact spelling), or null for the whole folder.</param>
    /// <param name="loneImage">CloneCD B5: an <c>.img</c> without a <c>.ccd</c> is a lone file (PlayStation) instead of waiting.</param>
    public static IReadOnlyList<PlannedUnit> Plan(IReadOnlyList<PlannerFile> files, IReadOnlySet<string>? selected = null, bool loneImage = false)
    {
        var ordered = files.Select(f => f.Name).Order(StringComparer.Ordinal).ToList();
        var position = ordered.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);
        var byFolded = ordered.ToLookup(n => n, StringComparer.OrdinalIgnoreCase);

        // D8: a .toc is planned exactly as a cue.
        var parsed = new List<(string Cue, IReadOnlyList<string> Files)>();
        var broken = new List<string>();
        foreach (var file in files.Where(f => (IsExtension(f.Name, ".cue") || IsExtension(f.Name, ".toc")) && f.Size <= MaxCueBytes)
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var sheetFiles = file.CueBytes is null ? null
                : IsExtension(file.Name, ".toc") ? (TocSheet.Parse(file.CueBytes) is { IsValid: true } toc ? toc.Files : null)
                : CueSheet.Parse(file.CueBytes) is { IsValid: true } cue ? cue.Files : null;
            if (sheetFiles is not null)
                parsed.Add((file.Name, sheetFiles));
            else
                broken.Add(file.Name);
        }

        // How many parsed cues name each file (by folded name): a file named by two is a SetConflict for both.
        var namedBy = parsed.SelectMany(p => p.Files.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var units = new List<PlannedUnit>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        void Add(PlannedKind kind, IEnumerable<string> members, string? reason = null)
        {
            var list = members.Where(claimed.Add).ToList();
            if (list.Count > 0)
                units.Add(new PlannedUnit(kind, list, reason));
        }

        foreach (var (cue, sheetFiles) in parsed)
        {
            var present = new List<string> { cue };
            var missing = false;
            var conflict = false;
            foreach (var name in sheetFiles)
            {
                var matches = byFolded[name].ToList();
                missing |= matches.Count == 0;
                conflict |= matches.Count > 1 || namedBy[name] > 1;
                present.AddRange(matches);
            }
            var unsupported = CueSheet.NameProblem(cue) is not null
                || sheetFiles.Any(n => !IsExtension(n, ".bin") || CueSheet.NameProblem(n) is not null
                    || string.Equals(n, cue, StringComparison.OrdinalIgnoreCase));

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

        // CloneCD B5: <stem>.ccd, <stem>.img and an optional <stem>.sub, matched ignoring case.
        var sizes = files.ToDictionary(f => f.Name, StringComparer.Ordinal);
        foreach (var ccd in ordered.Where(n => IsExtension(n, ".ccd") && !claimed.Contains(n)))
        {
            var stem = Path.GetFileNameWithoutExtension(ccd);
            var ccds = byFolded[ccd].ToList();
            var images = byFolded[stem + ".img"].ToList();
            var subs = byFolded[stem + ".sub"].ToList();
            List<string> members = [.. ccds, .. images, .. subs];
            var file = sizes[ccd];
            if (members.Any(m => CueSheet.NameProblem(m) is not null))
                Add(PlannedKind.Skipped, members, IngestReason.UnsupportedSetMember);
            else if (file.Size > MaxCueBytes || file.CueBytes is null || !CloneCdSheet.Parse(file.CueBytes).IsValid)
                Add(PlannedKind.Skipped, [ccd, .. images, .. subs], IngestReason.IncompleteSet);
            else if (images.Count == 0)
                Add(PlannedKind.Skipped, [ccd, .. subs], IngestReason.IncompleteSet);
            else if (ccds.Count > 1 || images.Count > 1 || subs.Count > 1 || members.Any(claimed.Contains))
                Add(PlannedKind.Skipped, members, IngestReason.SetConflict);
            else
                Add(PlannedKind.Set, [ccd, .. images, .. subs]);
        }

        // D8: <stem>.mds and <stem>.mdf, matched ignoring case, with the .mdf the main file.
        foreach (var mds in ordered.Where(n => IsExtension(n, ".mds") && !claimed.Contains(n)))
        {
            var mdss = byFolded[mds].ToList();
            var mdfs = byFolded[Path.GetFileNameWithoutExtension(mds) + ".mdf"].ToList();
            List<string> members = [.. mdss, .. mdfs];
            var file = sizes[mds];
            if (members.Any(m => CueSheet.NameProblem(m) is not null))
                Add(PlannedKind.Skipped, members, IngestReason.UnsupportedSetMember);
            else if (file.Size > MaxCueBytes || file.CueBytes is null || !MdsSheet.Parse(file.CueBytes).IsValid)
                Add(PlannedKind.Skipped, [mds, .. mdfs], IngestReason.IncompleteSet);
            else if (mdfs.Count == 0)
                Add(PlannedKind.Skipped, [mds], IngestReason.IncompleteSet);
            else if (mdss.Count > 1 || mdfs.Count > 1 || members.Any(claimed.Contains))
                Add(PlannedKind.Skipped, members, IngestReason.SetConflict);
            else
                Add(PlannedKind.Set, [mds, .. mdfs]);
        }

        foreach (var name in ordered.Where(n => IsExtension(n, ".sub") || IsExtension(n, ".mdf") || (!loneImage && IsExtension(n, ".img"))))
            Add(PlannedKind.Skipped, [name], IngestReason.IncompleteSet);

        foreach (var name in ordered)
            Add(PlannedKind.Lone, [name]);

        return units
            .Where(u => selected is null || u.Members.Any(selected.Contains))
            .OrderBy(u => u.Members.Min(m => position[m]))
            .ToList();
    }

    private static bool IsExtension(string name, string extension) => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
