namespace RnRomManager.Core.Storage;

/// <summary>S6: free disk space, behind a seam so tests can fake a full volume.</summary>
public interface IFreeSpace
{
    /// <returns>The bytes free to this process on the volume holding <paramref name="path"/>; null when it can't be read (e.g. a network share).</returns>
    long? AvailableBytes(string path);
}
