namespace RnRomManager.Core.Storage;

/// <summary>Path forms for native Windows file calls.</summary>
public static class NativePath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// The extended-length form of a fully qualified Windows path (<c>C:\x</c> → <c>\\?\C:\x</c>,
    /// <c>\\server\share\x</c> → <c>\\?\UNC\server\share\x</c>), so a valid name still works when the whole path is
    /// longer than MAX_PATH, whatever the machine's long-path setting. Windows doesn't normalize such a path, so
    /// pass only a path that <see cref="Path.GetFullPath(string)"/> produced. A path already in a <c>\\?\</c> or
    /// <c>\\.\</c> form is returned as it is.
    /// </summary>
    public static string ToExtendedLength(string fullPath)
    {
        if (fullPath.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? ExtendedUncPrefix + fullPath[2..]
            : ExtendedPrefix + fullPath;
    }
}
