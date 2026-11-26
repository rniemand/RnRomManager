namespace RnRomManager.Core.Storage;

/// <summary>What a path under a storage root turned out to be.</summary>
public enum ExternalPathStatus
{
    /// <summary>It exists, is the expected kind, and nothing below the root on the way to it is a link.</summary>
    Found,

    /// <summary>
    /// Confirmed absent: the entry, or a directory on the way to it, isn't there, and everything that does exist on
    /// the way is a real directory.
    /// </summary>
    Missing,

    /// <summary>
    /// Not usable: outside the root, a link or reparse point, the wrong kind of entry (a directory, FIFO, socket or
    /// device where a regular file is expected), or several entries whose names differ only in case.
    /// </summary>
    Unsafe,

    /// <summary>An IO or permission failure (a sharing violation included): whether it exists is unknown.</summary>
    Unreadable,

    /// <summary>
    /// Only from a checked open that was given the snapshot to expect: a regular file is there, but it is not that
    /// same, unchanged file (another file identity, length or last write time).
    /// </summary>
    Changed,
}
