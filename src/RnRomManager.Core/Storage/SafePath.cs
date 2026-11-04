namespace RnRomManager.Core.Storage;

/// <summary>
/// spec §9.3: every computed path goes through here. The result must be strictly inside <c>root</c>
/// (<c>GetFullPath(root) + separator</c> prefix), so <c>..</c>, rooted input and sibling folders like
/// <c>/app/roms2</c> are refused.
/// </summary>
public static class SafePath
{
    // Windows paths are case-insensitive, so "C:\App\Roms" contains "c:\app\roms\x".
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <exception cref="UnsafePathException"><paramref name="relative"/> is rooted, invalid, or resolves outside <paramref name="root"/>.</exception>
    public static string Resolve(string root, string relative)
    {
        // Path.Combine would silently drop root for these; also catches "\x" and "C:x" on Windows.
        if (Path.IsPathRooted(relative))
            throw new UnsafePathException(root, relative);

        string fullRoot, full;
        try
        {
            fullRoot = Path.GetFullPath(root);
            full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        }
        catch (ArgumentException ex)
        {
            throw new UnsafePathException(root, relative, ex);
        }

        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, Comparison) && full.Length > prefix.Length
            ? full
            : throw new UnsafePathException(root, relative);
    }
}
