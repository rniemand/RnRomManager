namespace RnRomManager.Core.Storage;

/// <param name="NativeError">The Win32 error or errno; 0 on success.</param>
/// <param name="Details">Why, for every status but <see cref="NoReplaceRenameStatus.Renamed"/>.</param>
public readonly record struct NoReplaceRenameResult(NoReplaceRenameStatus Status, int NativeError = 0, string? Details = null)
{
    public bool Renamed => Status == NoReplaceRenameStatus.Renamed;
}
