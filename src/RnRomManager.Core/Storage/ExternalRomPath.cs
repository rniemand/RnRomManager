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

    /// <summary>An IO or permission failure: whether it exists is unknown.</summary>
    Unreadable,
}

/// <summary>The result of classifying one path.</summary>
/// <param name="Path">
/// The full path with the spelling found on disk for every part that exists (a name is matched ignoring case);
/// parts that don't exist keep the requested spelling.
/// </param>
/// <param name="Snapshot">Set for a <see cref="ExternalPathStatus.Found"/> file.</param>
/// <param name="Details">Why, for every status but <see cref="ExternalPathStatus.Found"/>.</param>
public sealed record ExternalPathCheck(ExternalPathStatus Status, string Path, ExternalFileSnapshot? Snapshot = null, string? Details = null);

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
