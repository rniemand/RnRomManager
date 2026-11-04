namespace RnRomManager.Core.Storage;

/// <summary>
/// The ` (2)`, ` (3)`, … clash suffix before the extension (spec §5 moves into <c>_failed/</c>, <c>_duplicates/</c>,
/// <c>_removed/</c>; §6.1 ZIP and entry name clashes). Names compare case-insensitively everywhere (§5).
/// </summary>
public static class FileNameRules
{
    /// <summary><c>Tetris (World).gb</c>, 2 → <c>Tetris (World) (2).gb</c>.</summary>
    public static string WithSuffix(string fileName, int n) =>
        $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}";

    /// <summary><paramref name="fileName"/> if free, else the first free suffixed name from (2) up.</summary>
    public static string FirstFree(string fileName, Func<string, bool> isTaken)
    {
        if (!isTaken(fileName))
            return fileName;
        for (var n = 2; ; n++)
        {
            var candidate = WithSuffix(fileName, n);
            if (!isTaken(candidate))
                return candidate;
        }
    }

    /// <summary>First free name among the files and folders in <paramref name="directory"/>, ignoring case even on Linux.</summary>
    public static string FirstFreeIn(string directory, string fileName)
    {
        var taken = Directory.Exists(directory)
            ? Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        return FirstFree(fileName, taken.Contains!);
    }
}
