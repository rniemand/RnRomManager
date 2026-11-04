using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>A file waiting in <c>ingest/&lt;system&gt;/</c>.</summary>
public sealed record IngestFile(string SystemId, string FullPath, string Name, long SizeBytes, DateTime ModifiedAtUtc);

/// <summary>
/// Lists <c>ingest/&lt;system&gt;/</c> for every registered system (spec §5, §7.1 step 6): top level only (subfolders,
/// including <c>_duplicates</c>/<c>_failed</c>/<c>_removed</c>, are ignored), no dot-files, no OS junk. Shared by the
/// ingest snapshot and <c>GET /api/ingest/pending</c>.
/// </summary>
public sealed class IngestFileScanner(IOptions<PathsOptions> paths, ISystemRegistry systems)
{
    private static readonly HashSet<string> Junk = new(["Thumbs.db", "desktop.ini"], StringComparer.OrdinalIgnoreCase);

    public static bool IsIgnored(string fileName) => fileName.StartsWith('.') || Junk.Contains(fileName);

    /// <returns>Per registered system (in registry order), its files ordered by name.</returns>
    public IReadOnlyList<(string SystemId, IReadOnlyList<IngestFile> Files)> Scan() =>
        systems.All.Select(h => (h.Id, ScanSystem(h.Id))).ToList();

    private IReadOnlyList<IngestFile> ScanSystem(string systemId)
    {
        var folder = new DirectoryInfo(Path.Combine(paths.Value.Ingest, systemId));
        if (!folder.Exists)
            return [];
        return folder.EnumerateFiles()
            .Where(f => !IsIgnored(f.Name))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new IngestFile(systemId, f.FullName, f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }
}
