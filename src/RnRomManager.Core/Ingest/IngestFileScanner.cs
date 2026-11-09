using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>A file waiting in <c>ingest/&lt;system&gt;/</c>, or a ROM entry extracted from an archive there.</summary>
public sealed record IngestFile(string SystemId, string FullPath, string Name, long SizeBytes, DateTime ModifiedAtUtc)
{
    /// <summary>Set for an archive entry: the archive in ingest (<c>FullPath</c> is then the extracted copy).</summary>
    public string? ArchivePath { get; init; }

    /// <summary>Set for an archive entry: its full path inside the archive.</summary>
    public string? ArchiveEntry { get; init; }

    /// <summary>The ingest path recorded as the source (§6.2 <c>sourcePath</c>): the archive for an entry.</summary>
    public string SourcePath => ArchivePath ?? FullPath;
}

/// <summary>D1: a file in <c>ingest/&lt;system&gt;/_failed/</c>; <see cref="Reason"/> comes from its <c>.error.txt</c>.</summary>
public sealed record FailedFile(string Name, long SizeBytes, DateTime ModifiedAtUtc, string? Reason);

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

    /// <summary>D1: per registered system (in registry order), the top-level files of its <c>_failed/</c> folder, ordered by name, without <c>.error.txt</c> sidecars.</summary>
    public IReadOnlyList<(string SystemId, IReadOnlyList<FailedFile> Files)> ScanFailed() =>
        systems.All.Select(h => (h.Id, ScanFailedSystem(h.Id))).ToList();

    public const string ErrorSuffix = ".error.txt";

    private IReadOnlyList<FailedFile> ScanFailedSystem(string systemId)
    {
        var folder = new DirectoryInfo(Path.Combine(paths.Value.Ingest, systemId, StorageFolders.FailedFolder));
        if (!folder.Exists)
            return [];
        return folder.EnumerateFiles()
            .Where(f => !IsIgnored(f.Name) && !f.Name.EndsWith(ErrorSuffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new FailedFile(f.Name, f.Length, f.LastWriteTimeUtc, ReadReason(f.FullName + ErrorSuffix)))
            .ToList();
    }

    /// <summary>The <c>reason: </c> line <see cref="IngestPipeline.MoveToFailed"/> writes; null when missing or unreadable.</summary>
    private static string? ReadReason(string sidecar)
    {
        try
        {
            if (!File.Exists(sidecar))
                return null;
            return File.ReadLines(sidecar).FirstOrDefault(l => l.StartsWith("reason: ", StringComparison.Ordinal))?["reason: ".Length..].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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
