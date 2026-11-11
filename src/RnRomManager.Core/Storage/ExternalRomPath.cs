namespace RnRomManager.Core.Storage;

/// <summary>What a path under a storage root turned out to be.</summary>
public enum ExternalPathStatus
{
    /// <summary>It exists, is the expected kind, and nothing below the root on the way to it is a link.</summary>
    Found,

    /// <summary>
    /// Confirmed absent: the entry, or a directory on the way to it, isn't there, and everything that does exist on
    /// the way is a real directory.
    /// </summary>
    Missing,

    /// <summary>
    /// Not usable: outside the root, a link or reparse point, the wrong kind of entry (a directory, FIFO, socket or
    /// device where a regular file is expected), or several entries whose names differ only in case.
    /// </summary>
    Unsafe,

    /// <summary>An IO or permission failure (a sharing violation included): whether it exists is unknown.</summary>
    Unreadable,

    /// <summary>
    /// Only from a checked open that was given the snapshot to expect: a regular file is there, but it is not that
    /// same, unchanged file (another file identity, length or last write time).
    /// </summary>
    Changed,
}

/// <summary>The result of classifying one path.</summary>
/// <param name="Path">
/// The full path with the spelling found on disk for every part that exists (a name is matched ignoring case);
/// parts that don't exist keep the requested spelling.
/// </param>
/// <param name="Snapshot">Set for a <see cref="ExternalPathStatus.Found"/> file.</param>
/// <param name="Details">Why, for every status but <see cref="ExternalPathStatus.Found"/>.</param>
public sealed record ExternalPathCheck(ExternalPathStatus Status, string Path, ExternalFileSnapshot? Snapshot = null, string? Details = null);

/// <summary>The result of a checked open. <paramref name="Stream"/> and <paramref name="Snapshot"/> are set only for <see cref="ExternalPathStatus.Found"/>.</summary>
/// <param name="Path">As <see cref="ExternalPathCheck.Path"/>.</param>
/// <param name="Stream">The validated read stream itself; the caller disposes it and never reopens the path.</param>
/// <param name="Snapshot">Read from the open handle, so it describes exactly the file <paramref name="Stream"/> reads.</param>
public sealed record ExternalOpenResult(
    ExternalPathStatus Status, string Path, FileStream? Stream = null, ExternalFileSnapshot? Snapshot = null, string? Details = null);

/// <summary>Test seams of <see cref="ExternalRomPath.OpenRead"/>: points where a test swaps an entry mid-open.</summary>
public sealed class ExternalOpenHooks
{
    /// <summary>Runs after the path was classified and before anything is opened.</summary>
    public Action? AfterClassify { get; init; }

    /// <summary>Runs with each directory's full path right before that directory is opened.</summary>
    public Action<string>? BeforeDirectoryOpen { get; init; }
}

/// <summary>A regular file found by <see cref="ExternalRomPath.ScanFolder"/>.</summary>
/// <param name="RelativePath">Relative to the root, <c>/</c> separators, each name as spelled on disk.</param>
public sealed record CheckedFile(string RelativePath, string FullPath, ExternalFileSnapshot Snapshot);

/// <summary>Something <see cref="ExternalRomPath.ScanFolder"/> refused to open or walk into.</summary>
/// <param name="RelativePath">
/// Relative to the root, <c>/</c> separators. For names that differ only in case it is the first spelling in
/// ordinal order; everything at or below any of those spellings was skipped.
/// </param>
/// <param name="Status"><see cref="ExternalPathStatus.Unsafe"/> or <see cref="ExternalPathStatus.Unreadable"/>.</param>
/// <param name="IsSpecialFile">True for a FIFO, socket or device: a single entry that can't be a folder. A link, an unreadable entry and case variants may each be or hide one.</param>
public sealed record CheckedScanProblem(string RelativePath, ExternalPathStatus Status, string Details, bool IsSpecialFile = false);

public sealed record CheckedScan(IReadOnlyList<CheckedFile> Files, IReadOnlyList<CheckedScanProblem> Problems);

/// <summary>
/// The one place that decides whether a path under a storage root may be used for an external ROM, its metadata ZIP,
/// an ingest source or a destination folder (spec G4). Below the root every existing component must be a real
/// directory, never a symlink or reparse point; the root itself is taken as configured. Each name is matched against
/// the directory listing ignoring case: one match is used with its spelling on disk, several are ambiguous and
/// refused. Nothing is opened: entries are inspected by metadata only, so a FIFO or device can't block or be read.
/// This is a classification at one moment; a caller that then reads the file confirms the handle it opened against
/// the returned <see cref="ExternalFileSnapshot"/>.
/// </summary>
public static class ExternalRomPath
{
    private static readonly StringComparison RootComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Classifies <paramref name="fullPath"/> as a regular file under <paramref name="root"/>.</summary>
    public static ExternalPathCheck ClassifyFile(string root, string fullPath) => Classify(root, fullPath, expectFile: true);

    /// <summary>
    /// Classifies <paramref name="fullPath"/> as a directory under <paramref name="root"/> (or the root itself).
    /// <see cref="ExternalPathStatus.Missing"/> means it can be created: every ancestor that exists is a real
    /// directory, and <see cref="ExternalPathCheck.Path"/> is where to create it.
    /// </summary>
    public static ExternalPathCheck ClassifyDirectory(string root, string fullPath) => Classify(root, fullPath, expectFile: false);

    /// <summary>
    /// Creates a new directory for this caller's own work files (an archive's extraction folder, a DAT refresh's
    /// staging folder). Every ancestor below <paramref name="root"/> must be a real directory (missing ones are
    /// created), and nothing may already be at <paramref name="fullPath"/>: an occupant is never adopted or changed.
    /// </summary>
    /// <returns><see cref="ExternalPathStatus.Found"/> with the created path; otherwise why not (an occupant is <see cref="ExternalPathStatus.Unsafe"/>).</returns>
    public static ExternalPathCheck CreateDirectory(string root, string fullPath)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(fullPath))!;
            var above = ClassifyDirectory(root, parent);
            if (above.Status == ExternalPathStatus.Missing)
            {
                Directory.CreateDirectory(above.Path);
                above = ClassifyDirectory(root, parent);
            }
            if (above.Status != ExternalPathStatus.Found)
                return above;

            var target = ClassifyDirectory(root, fullPath);
            if (target.Status == ExternalPathStatus.Found)
                return new ExternalPathCheck(ExternalPathStatus.Unsafe, target.Path, Details: $"{target.Path} already exists");
            if (target.Status != ExternalPathStatus.Missing)
                return target;
            Directory.CreateDirectory(target.Path);
            return ClassifyDirectory(root, fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ExternalPathCheck(ExternalPathStatus.Unreadable, fullPath, Details: $"{fullPath} can't be created: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a regular file under <paramref name="root"/> for reading, safe against an entry being swapped between
    /// the check and the open: after <see cref="ClassifyFile"/>, every directory below the root is opened without
    /// following links and the file is validated on the handle that was opened (Linux: no-follow directory-relative
    /// opens, then the pinned file through <c>/proc/self/fd</c>, so procfs must be mounted; Windows: directory
    /// handles held without delete sharing, the file opened without following a reparse point).
    /// </summary>
    /// <param name="share">The Windows sharing mode of the returned handle; Linux has none.</param>
    /// <param name="expected">When set, the opened file must be this same, unchanged file, else <see cref="ExternalPathStatus.Changed"/>.</param>
    /// <param name="hooks">Test seams.</param>
    public static ExternalOpenResult OpenRead(string root, string fullPath, FileShare share = FileShare.Read,
        ExternalFileSnapshot? expected = null, ExternalOpenHooks? hooks = null)
    {
        var check = ClassifyFile(root, fullPath);
        if (check.Status != ExternalPathStatus.Found)
            return new ExternalOpenResult(check.Status, check.Path, Details: check.Details);
        hooks?.AfterClassify?.Invoke();

        // check.Path is the root plus each name as spelled on disk.
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var components = check.Path[(fullRoot.Length + 1)..].Split(Path.DirectorySeparatorChar);
        return Finish(check.Path, NativeCheckedOpen.OpenRead(fullRoot, components, share, hooks?.BeforeDirectoryOpen), expected);
    }

    private static ExternalOpenResult Finish(string path, NativeCheckedOpen.Result opened, ExternalFileSnapshot? expected)
    {
        if (opened.Outcome != NativeCheckedOpen.Outcome.Opened)
        {
            var status = opened.Outcome switch
            {
                NativeCheckedOpen.Outcome.Missing => ExternalPathStatus.Missing,
                NativeCheckedOpen.Outcome.Unsafe => ExternalPathStatus.Unsafe,
                _ => ExternalPathStatus.Unreadable,
            };
            return new ExternalOpenResult(status, path, Details: opened.Details);
        }

        var handle = opened.Handle!;
        if (expected is not null && opened.Snapshot != expected)
        {
            handle.Dispose();
            return new ExternalOpenResult(ExternalPathStatus.Changed, path, Details: $"{path} changed since it was checked");
        }

        try
        {
            // Unbuffered: every consumer (hashing, copying, downloads) reads in large blocks of its own.
            return new ExternalOpenResult(ExternalPathStatus.Found, path, new FileStream(handle, FileAccess.Read, bufferSize: 0), opened.Snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            handle.Dispose();
            return new ExternalOpenResult(ExternalPathStatus.Unreadable, path, Details: $"{path}: {ex.Message}");
        }
    }

    /// <summary>
    /// <see cref="OpenRead"/> for a path whose spelling came from <see cref="ScanFolder"/>: the scan already ruled
    /// out case variants, so the folders aren't listed again (which would make opening every file of a large folder
    /// quadratic). Every other check is the same, and made on the handles opened here.
    /// </summary>
    /// <param name="relativePath">A <see cref="CheckedFile.RelativePath"/>.</param>
    public static ExternalOpenResult OpenScanned(string root, string relativePath, FileShare share = FileShare.Read,
        ExternalFileSnapshot? expected = null, ExternalOpenHooks? hooks = null)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var components = relativePath.Split('/');
        var path = Path.Combine([fullRoot, .. components]);
        if (components.Any(c => c.Length == 0 || c is "." or ".." || c.Contains('\\') || c.Contains(Path.DirectorySeparatorChar)))
            return new ExternalOpenResult(ExternalPathStatus.Unsafe, path, Details: $"'{relativePath}' is not a scanned relative path");
        hooks?.AfterClassify?.Invoke();
        return Finish(path, NativeCheckedOpen.OpenRead(fullRoot, components, share, hooks?.BeforeDirectoryOpen), expected);
    }

    /// <summary>
    /// Lists every regular file at any depth under <paramref name="relativeFolder"/> without following a link:
    /// a linked folder is reported and not entered, a linked or special file is reported and not opened. Names in
    /// one folder that differ only in case are reported once and all skipped, since no rule could pick one. On
    /// Linux a name containing a backslash is reported: it is one name there, never a path. A missing folder is an
    /// empty scan.
    /// </summary>
    /// <param name="relativeFolder">Relative to <paramref name="root"/>, <c>/</c> separators, e.g. a system id.</param>
    public static CheckedScan ScanFolder(string root, string relativeFolder)
    {
        var files = new List<CheckedFile>();
        var problems = new List<CheckedScanProblem>();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var start = ClassifyDirectory(fullRoot, Path.Combine([fullRoot, .. relativeFolder.Split('/')]));
        switch (start.Status)
        {
            case ExternalPathStatus.Found:
                Scan(start.Path, string.Join('/', start.Path[(fullRoot.Length + 1)..].Split(Path.DirectorySeparatorChar)), files, problems);
                break;
            case ExternalPathStatus.Missing:
                break;
            default:
                problems.Add(new CheckedScanProblem(relativeFolder, start.Status, start.Details!));
                break;
        }
        return new CheckedScan(files, problems);
    }

    private static void Scan(string directory, string relative, List<CheckedFile> files, List<CheckedScanProblem> problems)
    {
        List<string> names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(directory).Select(e => Path.GetFileName(e)!).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return; // removed since it was listed
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problems.Add(new CheckedScanProblem(relative, ExternalPathStatus.Unreadable, $"{directory} can't be listed: {ex.Message}"));
            return;
        }

        // In a fixed order (by each name's first spelling), so a scan reports the same thing whatever the listing order.
        foreach (var spellings in names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.Order(StringComparer.Ordinal).ToList()).OrderBy(s => s[0], StringComparer.Ordinal))
        {
            var name = spellings[0];
            var childRelative = relative + "/" + name;
            if (spellings.Count > 1)
            {
                problems.Add(new CheckedScanProblem(childRelative, ExternalPathStatus.Unsafe,
                    $"names differing only in case in {directory}: {string.Join(", ", spellings)}"));
                continue;
            }
            if (name.Contains('\\'))
            {
                problems.Add(new CheckedScanProblem(childRelative, ExternalPathStatus.Unsafe, $"'{name}' in {directory} contains a backslash"));
                continue;
            }

            var full = Path.Combine(directory, name);
            var stat = NativeFileStat.Read(full);
            if (stat.Outcome == NativeStatOutcome.NotFound)
                continue; // removed since it was listed
            if (stat.Outcome == NativeStatOutcome.Failed)
                problems.Add(new CheckedScanProblem(childRelative, ExternalPathStatus.Unreadable, $"{full}: {stat.Error}"));
            else if (stat.IsLink)
                problems.Add(new CheckedScanProblem(childRelative, ExternalPathStatus.Unsafe, $"{full} is a link or reparse point"));
            else if (stat.IsDirectory)
                Scan(full, childRelative, files, problems);
            else if (stat.IsRegularFile)
                files.Add(new CheckedFile(childRelative, full, stat.Snapshot!));
            else
                problems.Add(new CheckedScanProblem(childRelative, ExternalPathStatus.Unsafe, $"{full} is not a regular file", IsSpecialFile: true));
        }
    }

    /// <summary>
    /// What an open stream is reading right now, whatever its path names by now; null when it isn't a regular file
    /// or can't be inspected. Compare it with the snapshot taken at open to see whether the file changed meanwhile.
    /// </summary>
    public static ExternalFileSnapshot? SnapshotOf(FileStream stream) =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? NativeFileStat.Read(stream.SafeFileHandle).Snapshot : null;

    private static ExternalPathCheck Classify(string root, string fullPath, bool expectFile)
    {
        string fullRoot, full;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            full = Path.GetFullPath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ExternalPathCheck(ExternalPathStatus.Unsafe, fullPath, Details: $"not a valid path: {ex.Message}");
        }

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        string[] components;
        if (string.Equals(Path.TrimEndingDirectorySeparator(full), fullRoot, RootComparison))
            components = [];
        else if (full.StartsWith(prefix, RootComparison))
            components = full[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        else
            return new ExternalPathCheck(ExternalPathStatus.Unsafe, full, Details: $"outside {fullRoot}");

        if (components.Length == 0)
        {
            return expectFile
                ? new ExternalPathCheck(ExternalPathStatus.Unsafe, full, Details: "the storage root is not a file")
                : new ExternalPathCheck(ExternalPathStatus.Found, fullRoot);
        }

        var current = fullRoot;
        for (var i = 0; i < components.Length; i++)
        {
            var name = components[i];
            var isLeaf = i == components.Length - 1;
            string Rest(string from) => Path.Combine([from, .. components[(i + 1)..]]);

            // On Linux '\' is an ordinary name character; a stored path never means it as part of a name.
            if (name.Contains('\\'))
                return new ExternalPathCheck(ExternalPathStatus.Unsafe, Rest(Path.Combine(current, name)), Details: $"'{name}' contains a backslash");

            var (matches, listError, parentMissing) = Matches(current, name);
            if (listError is not null)
                return new ExternalPathCheck(ExternalPathStatus.Unreadable, Rest(Path.Combine(current, name)), Details: listError);
            if (parentMissing || matches.Count == 0)
                return new ExternalPathCheck(ExternalPathStatus.Missing, Rest(Path.Combine(current, name)), Details: $"{Path.Combine(current, name)} does not exist");
            if (matches.Count > 1)
            {
                return new ExternalPathCheck(ExternalPathStatus.Unsafe, Rest(Path.Combine(current, name)),
                    Details: $"names differing only in case in {current}: {string.Join(", ", matches.Order(StringComparer.Ordinal))}");
            }

            var actual = Path.Combine(current, matches[0]);
            var stat = NativeFileStat.Read(actual);
            switch (stat.Outcome)
            {
                case NativeStatOutcome.NotFound: // removed since the listing
                    return new ExternalPathCheck(ExternalPathStatus.Missing, Rest(actual), Details: $"{actual} does not exist");
                case NativeStatOutcome.Failed:
                    return new ExternalPathCheck(ExternalPathStatus.Unreadable, Rest(actual), Details: $"{actual}: {stat.Error}");
            }

            if (stat.IsLink)
                return new ExternalPathCheck(ExternalPathStatus.Unsafe, Rest(actual), Details: $"{actual} is a link or reparse point");
            if (!isLeaf || !expectFile)
            {
                if (!stat.IsDirectory)
                    return new ExternalPathCheck(ExternalPathStatus.Unsafe, Rest(actual), Details: $"{actual} is not a directory");
            }
            else if (!stat.IsRegularFile)
            {
                return new ExternalPathCheck(ExternalPathStatus.Unsafe, actual, Details: $"{actual} is not a regular file");
            }
            else
            {
                return new ExternalPathCheck(ExternalPathStatus.Found, actual, stat.Snapshot);
            }

            current = actual;
        }

        return new ExternalPathCheck(ExternalPathStatus.Found, current);
    }

    /// <summary>The names in <paramref name="directory"/> equal to <paramref name="name"/> ignoring case.</summary>
    private static (List<string> Matches, string? Error, bool DirectoryMissing) Matches(string directory, string name)
    {
        var matches = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var entryName = Path.GetFileName(entry);
                if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                    matches.Add(entryName);
            }
        }
        catch (DirectoryNotFoundException)
        {
            return (matches, null, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (matches, $"{directory} can't be listed: {ex.Message}", false);
        }
        return (matches, null, false);
    }
}
