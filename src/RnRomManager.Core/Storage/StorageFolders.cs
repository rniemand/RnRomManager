using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Storage;

/// <summary>Startup step 4 (spec §4.1): create the root folders and prove each one is writable.</summary>
public static class StorageFolders
{
    public const string ProbeFileName = ".rnrm-probe";

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
}
