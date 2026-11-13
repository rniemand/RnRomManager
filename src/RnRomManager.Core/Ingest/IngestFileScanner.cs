using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Set for an external archive's entry: the archive as it was extracted. A failed placement deletes the entry's
    /// moved copy only while the archive still matches it (spec I5).
    /// </summary>
    public ExternalFileSnapshot? ArchiveSnapshot { get; init; }

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
public class IngestFileScanner(IOptions<PathsOptions> paths, ISystemRegistry systems, ILogger<IngestFileScanner> logger)
{
    private static readonly HashSet<string> Junk = new(["Thumbs.db", "desktop.ini"], StringComparer.OrdinalIgnoreCase);

    public static bool IsIgnored(string fileName) => fileName.StartsWith('.') || Junk.Contains(fileName);

    /// <returns>Per registered system (in registry order), its files ordered by name.</returns>
    /// <remarks>Virtual so a test can hand the job source paths no filesystem here could hold.</remarks>
    public virtual IReadOnlyList<(string SystemId, IReadOnlyList<IngestFile> Files)> Scan() =>
        systems.All.Select(h => (h.Id, ScanSystem(h.Id))).ToList();

    /// <summary>D1: per registered system (in registry order), the top-level files of its <c>_failed/</c> folder, ordered by name, without <c>.error.txt</c> sidecars.</summary>
    public IReadOnlyList<(string SystemId, IReadOnlyList<FailedFile> Files)> ScanFailed() =>
        systems.All.Select(h => (h.Id, ScanFailedSystem(h.Id))).ToList();

    public const string ErrorSuffix = ".error.txt";

    private bool IsExternal(string systemId) => systems.ById(systemId) is { EmbedRomsInZip: false };

    /// <summary>The longest diagnostic prefix read for an external system: enough for the <c>reason: </c> line.</summary>
    public const int MaxReasonBytes = 1024;

    /// <summary>
    /// G4 for an external system: the folder is walked without following links, so a linked <c>_failed/</c> lists
    /// nothing and a linked or special file is left out. A diagnostic is only read when it is a regular file, and
    /// then through the checked open.
    /// </summary>
    private IReadOnlyList<FailedFile> ScanFailedChecked(string systemId)
    {
        var root = paths.Value.Ingest;
        var scan = ExternalRomPath.ScanFolder(root, $"{systemId}/{StorageFolders.FailedFolder}");
        foreach (var problem in scan.Problems)
            logger.LogWarning("Ingest listing skipped {Path}: {Details}", problem.RelativePath, problem.Details);
        var prefix = $"{systemId}/{StorageFolders.FailedFolder}/";
        var top = scan.Files
            .Where(f => f.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.RelativePath.IndexOf('/', prefix.Length) < 0)
            .ToDictionary(f => f.RelativePath[prefix.Length..], StringComparer.OrdinalIgnoreCase);
        return top.Where(f => !IsIgnored(f.Key) && !f.Key.EndsWith(ErrorSuffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new FailedFile(f.Key, f.Value.Snapshot.Length, f.Value.Snapshot.LastWriteTimeUtc,
                top.TryGetValue(f.Key + ErrorSuffix, out var diagnostic) ? ReadReasonChecked(root, diagnostic) : null))
            .ToList();
    }

    /// <summary>
    /// The reason from the first <see cref="MaxReasonBytes"/> bytes: only a complete first line (LF or CRLF)
    /// starting with <c>reason: </c>, which is how the writer starts the file. Anything else is no reason.
    /// </summary>
    private static string? ReadReasonChecked(string root, CheckedFile diagnostic)
    {
        var opened = ExternalRomPath.OpenScanned(root, diagnostic.RelativePath, FileShare.Read | FileShare.Delete, diagnostic.Snapshot);
        if (opened.Status != ExternalPathStatus.Found)
            return null;
        try
        {
            using var stream = opened.Stream!;
            var buffer = new byte[MaxReasonBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var end = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (end < 0)
                return null;
            var line = System.Text.Encoding.UTF8.GetString(buffer, 0, end).TrimEnd('\r');
            return line.StartsWith("reason: ", StringComparison.Ordinal) ? line["reason: ".Length..].Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IReadOnlyList<FailedFile> ScanFailedSystem(string systemId)
    {
        if (IsExternal(systemId))
            return ScanFailedChecked(systemId);
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

    /// <summary>One system's top-level files, ordered by name, as <see cref="Scan"/> lists them.</summary>
    public IReadOnlyList<IngestFile> ScanSystem(string systemId)
    {
        var folder = new DirectoryInfo(Path.Combine(paths.Value.Ingest, systemId));
        if (IsExternal(systemId))
        {
            // G4: a linked or unreadable system folder is never listed; each file is still checked when it is used.
            var check = ExternalRomPath.ClassifyDirectory(paths.Value.Ingest, folder.FullName);
            if (check.Status != ExternalPathStatus.Found)
            {
                if (check.Status != ExternalPathStatus.Missing)
                    logger.LogWarning("Ingest listing skipped {Path}: {Details}", folder.FullName, check.Details);
                return [];
            }
        }
        if (!folder.Exists)
            return [];
        return folder.EnumerateFiles()
            .Where(f => !IsIgnored(f.Name))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new IngestFile(systemId, f.FullName, f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }
}
