namespace RnRomManager.Core.Storage;

/// <summary>
/// A regular file as it was when checked: which file it is, its length and its last write time. Comparing two
/// snapshots tells whether a path still names the same, unchanged file.
/// </summary>
public sealed record ExternalFileSnapshot(FileIdentity Identity, long Length, DateTime LastWriteTimeUtc);
