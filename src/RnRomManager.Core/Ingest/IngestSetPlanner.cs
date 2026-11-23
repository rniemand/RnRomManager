using Microsoft.Extensions.Logging;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Ingest;

/// <summary>One loose file of an ingest folder, as the planner sees it.</summary>
/// <param name="CueBytes">
/// For a <c>.cue</c>, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c> of at most <see cref="IngestSetPlanner.MaxCueBytes"/>: its bytes, or null when it couldn't be read.
/// Ignored for other files.
/// </param>
public sealed record PlannerFile(string Name, long Size, byte[]? CueBytes = null);

public enum PlannedKind
{
    /// <summary>A complete cue, TOC, CloneCD or MDS set: processed as one unit.</summary>
    Set,

    /// <summary>A file no cue names: processed on its own as today.</summary>
    Lone,

    /// <summary>Present members of a set that can't be processed; each is <c>Skipped</c> with <see cref="PlannedUnit.Reason"/> and stays.</summary>
    Skipped,
}

/// <param name="Members">
/// File names as on disk. For a set: the cue or <c>.toc</c> first, then the files it names in order; the <c>.ccd</c>, its
/// <c>.img</c> and its <c>.sub</c> when there is one; or the <c>.mds</c> and its <c>.mdf</c>.
/// </param>
/// <param name="Reason">An <see cref="IngestReason"/> for <see cref="PlannedKind.Skipped"/>; null otherwise.</param>
public sealed record PlannedUnit(PlannedKind Kind, IReadOnlyList<string> Members, string? Reason = null);

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

/// <summary>B7/B12, CloneCD B5, D8: the checked read of a small cue, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c> for <see cref="IngestSetPlanner"/>, shared by ingest and Review.</summary>
internal static class IngestCueReader
{
    private static readonly string[] SheetExtensions = [".cue", ".toc", ".ccd", ".mds"];

    /// <returns>The cue's, <c>.toc</c>'s, <c>.ccd</c>'s or <c>.mds</c>'s bytes, through the same checked open as other ingest reads; null for any other file, one over
    /// <see cref="IngestSetPlanner.MaxCueBytes"/>, or one that can't be read.</returns>
    public static byte[]? Read(string ingestRoot, IngestFile file, ILogger logger)
    {
        if (!SheetExtensions.Any(e => file.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            || file.SizeBytes > IngestSetPlanner.MaxCueBytes)
            return null;
        var opened = ExternalRomPath.OpenRead(ingestRoot, file.FullPath, FileShare.Read);
        if (opened.Status != ExternalPathStatus.Found)
            return null;
        try
        {
            using var stream = opened.Stream!;
            using var buffer = new MemoryStream();
            new LimitStream(stream, IngestSetPlanner.MaxCueBytes).CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LimitExceededException)
        {
            logger.LogWarning("{Path}: the set sheet couldn't be read: {Error}", file.FullPath, ex.Message);
            return null;
        }
    }
}
