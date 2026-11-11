using Microsoft.Extensions.Logging;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Storage;

/// <summary>Startup step 4 (spec §4.1, §5): create the root and system folders and prove each root is writable.</summary>
public static class StorageFolders
{
    public const string ProbeFileName = ".rnrm-probe";
    public const string DuplicatesFolder = "_duplicates";
    public const string FailedFolder = "_failed";
    public const string RemovedFolder = "_removed";

    /// <summary>Library letter folders: <c>#</c> then <c>A</c>–<c>Z</c> (spec §5).</summary>
    public static IReadOnlyList<string> Buckets { get; } = ["#", .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString())];

    public static IReadOnlyList<string> IngestSubfolders { get; } = [DuplicatesFolder, FailedFolder, RemovedFolder];

    /// <exception cref="StorageNotWritableException">A folder can't be created or written.</exception>
    public static void EnsureRoots(PathsOptions paths)
    {
        foreach (var (key, path) in paths.All())
        {
            try
            {
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, ProbeFileName);
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new StorageNotWritableException(
                    $"{key} '{path}' is not writable by {ProcessIdentity.Describe()}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// <c>roms/&lt;id&gt;/{#,A..Z}</c> and <c>ingest/&lt;id&gt;/{_duplicates,_failed,_removed}</c> for every handler.
    /// Idempotent: existing folders and their contents are left alone. An external system's folders are classified
    /// first (G4): a linked or otherwise unsafe system folder, bucket or side folder is skipped with a warning and
    /// nothing is created through it.
    /// </summary>
    /// <exception cref="StorageNotWritableException">A folder can't be created, or an external one can't be checked.</exception>
    public static void EnsureSystemFolders(PathsOptions paths, IEnumerable<ISystemHandler> handlers, ILogger logger)
    {
        foreach (var handler in handlers)
        {
            var trees = new[]
            {
                (Root: paths.Roms, Children: Buckets),
                (Root: paths.Ingest, Children: IngestSubfolders),
            };
            foreach (var (root, children) in trees)
            {
                var system = SafePath.Resolve(root, handler.Id);
                var folders = children.Select(c => SafePath.Resolve(root, Path.Combine(handler.Id, c)));
                if (!handler.EmbedRomsInZip)
                {
                    if (!SafeToCreate(root, system, logger))
                        continue;
                    folders = folders.Where(f => SafeToCreate(root, f, logger)).ToList();
                }
                foreach (var folder in folders)
                    Create(folder);
            }
        }
    }

    /// <returns>False (and a warning) for an unsafe external folder; true when it is there or can be created.</returns>
    private static bool SafeToCreate(string root, string folder, ILogger logger)
    {
        var check = ExternalRomPath.ClassifyDirectory(root, folder);
        switch (check.Status)
        {
            case ExternalPathStatus.Found or ExternalPathStatus.Missing:
                return true;
            case ExternalPathStatus.Unreadable:
                throw new StorageNotWritableException($"Folder '{folder}' can't be checked by {ProcessIdentity.Describe()}: {check.Details}", new IOException(check.Details));
            default:
                logger.LogWarning("External folder {Folder} is unsafe ({Details}); nothing is created in it", folder, check.Details);
                return false;
        }
    }

    private static void Create(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageNotWritableException(
                $"Folder '{folder}' can't be created by {ProcessIdentity.Describe()}: {ex.Message}", ex);
        }
    }
}
