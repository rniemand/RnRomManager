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
    /// Idempotent: existing folders and their contents are left alone.
    /// </summary>
    /// <exception cref="StorageNotWritableException">A folder can't be created.</exception>
    public static void EnsureSystemFolders(PathsOptions paths, IEnumerable<ISystemHandler> handlers)
    {
        foreach (var handler in handlers)
        {
            var folders = Buckets.Select(b => SafePath.Resolve(paths.Roms, Path.Combine(handler.Id, b)))
                .Concat(IngestSubfolders.Select(f => SafePath.Resolve(paths.Ingest, Path.Combine(handler.Id, f))));
            foreach (var folder in folders)
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
    }
}
