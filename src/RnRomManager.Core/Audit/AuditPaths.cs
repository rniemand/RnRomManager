using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Audit;

/// <summary>
/// AU1: audit paths are relative to the library or ingest root and prefixed <c>roms/</c> or <c>ingest/</c>, with <c>/</c>
/// separators. A path outside both roots is recorded as its file name only, never absolute.
/// </summary>
public sealed class AuditPaths(IOptions<PathsOptions> paths)
{
    /// <summary>A <c>games.zip_path</c> (already relative to the library root).</summary>
    public static string Library(string zipPath) => "roms/" + zipPath.Replace('\\', '/');

    /// <summary>An absolute path under the library or ingest root.</summary>
    public string Of(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        foreach (var (prefix, root) in new[] { ("roms/", paths.Value.Roms), ("ingest/", paths.Value.Ingest) })
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), full);
            if (relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return prefix + relative.Replace('\\', '/');
        }
        return Path.GetFileName(full);
    }

    /// <summary>An entry of an archive in ingest (its working copy sits under <c>cache/work</c>): <c>ingest/&lt;archive&gt; › &lt;entry&gt;</c>.</summary>
    public string ArchiveEntry(string archivePath, string entry) => $"{Of(archivePath)} › {entry}";
}
