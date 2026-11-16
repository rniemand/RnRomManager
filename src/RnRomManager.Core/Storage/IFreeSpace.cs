namespace RnRomManager.Core.Storage;

/// <summary>S6: free disk space, behind a seam so tests can fake a full volume.</summary>
public interface IFreeSpace
{
    /// <returns>The bytes free to this process on the volume holding <paramref name="path"/>; null when it can't be read (e.g. a network share).</returns>
    long? AvailableBytes(string path);
}

/// <summary>Reads <see cref="DriveInfo.AvailableFreeSpace"/> of the path's root.</summary>
public sealed class DriveFreeSpace : IFreeSpace
{
    public long? AvailableBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
