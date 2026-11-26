namespace RnRomManager.Core.Storage;

/// <summary>Which file a path named when it was looked at: device + inode on Linux, volume serial + file index on Windows.</summary>
public readonly record struct FileIdentity(ulong Volume, ulong FileId);
