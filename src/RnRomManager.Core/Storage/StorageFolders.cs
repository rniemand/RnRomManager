using Microsoft.Extensions.Logging;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Storage;

/// <summary>Startup step 4 (spec §4.1, §5): create the root and ingest folders and prove each root is writable; B31 cleanup.</summary>
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
    /// <c>ingest/&lt;id&gt;/{_duplicates,_failed,_removed}</c> for every handler. Library folders (<c>roms/&lt;id&gt;/…</c>) are
    /// created by the writers when a ROM first lands there (B30). Idempotent: existing folders and their contents are left
    /// alone. An external system's folders are classified first (G4): a linked or otherwise unsafe system folder or side
    /// folder is skipped with a warning and nothing is created through it.
    /// </summary>
    /// <exception cref="StorageNotWritableException">A folder can't be created, or an external one can't be checked.</exception>
    public static void EnsureSystemFolders(PathsOptions paths, IEnumerable<ISystemHandler> handlers, ILogger logger)
    {
        foreach (var handler in handlers)
        {
            var root = paths.Ingest;
            var system = SafePath.Resolve(root, handler.Id);
            var folders = IngestSubfolders.Select(c => SafePath.Resolve(root, Path.Combine(handler.Id, c)));
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

    /// <summary>
    /// B31, once per startup after crash recovery: removes each empty bucket folder (<see cref="Buckets"/> only) of every
    /// registered system, then <c>roms/&lt;id&gt;</c> if that leaves it empty. Every folder is classified first, embedded
    /// and external systems alike: only a real folder (<see cref="ExternalPathStatus.Found"/>) is a candidate, a missing
    /// one is skipped silently, anything else (a link, unsafe, unreadable) is logged and never deleted through. Failures
    /// are logged at Warning and never thrown.
    /// </summary>
    public static void RemoveEmptyLibraryFolders(PathsOptions paths, IEnumerable<ISystemHandler> handlers, ILogger logger)
    {
        foreach (var handler in handlers)
        {
            try
            {
                var system = SafePath.Resolve(paths.Roms, handler.Id);
                if (!IsRealFolder(paths.Roms, system, logger))
                    continue;
                foreach (var bucket in Buckets)
                {
                    var folder = SafePath.Resolve(paths.Roms, Path.Combine(handler.Id, bucket));
                    if (IsRealFolder(paths.Roms, folder, logger))
                        RemoveIfEmpty(folder, logger);
                }
                RemoveIfEmpty(system, logger);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't clean up the empty library folders of {System}", handler.Id);
            }
        }
    }

    private static bool IsRealFolder(string root, string folder, ILogger logger)
    {
        var check = ExternalRomPath.ClassifyDirectory(root, folder);
        if (check.Status == ExternalPathStatus.Found)
            return true;
        if (check.Status != ExternalPathStatus.Missing)
            logger.LogWarning("Library folder {Folder} is not a plain folder ({Details}); it is left alone", folder, check.Details);
        return false;
    }

    private static void RemoveIfEmpty(string folder, ILogger logger)
    {
        try
        {
            // Hidden files and subfolders count: only a folder with no entries at all goes. A non-recursive delete
            // also refuses a folder that gained an entry since.
            if (Directory.EnumerateFileSystemEntries(folder).Any())
                return;
            Directory.Delete(folder, recursive: false);
            logger.LogInformation("Removed empty library folder {Folder}", folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't remove empty library folder {Folder}", folder);
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
