namespace RnRomManager.Core.Storage;

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
