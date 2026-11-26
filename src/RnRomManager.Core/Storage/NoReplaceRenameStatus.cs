namespace RnRomManager.Core.Storage;

/// <summary>How a <see cref="NoReplaceRename.Move"/> ended.</summary>
public enum NoReplaceRenameStatus
{
    Renamed,

    /// <summary>Something already has the destination name; it was left untouched.</summary>
    DestinationExists,

    /// <summary>Source and destination are on different volumes: only a copy can move the file. Nothing was changed.</summary>
    CrossDevice,

    /// <summary>The OS or this filesystem has no rename that refuses to replace (e.g. an NFS mount). Nothing was changed.</summary>
    Unsupported,

    /// <summary>The source, or the destination's folder, doesn't exist.</summary>
    NotFound,

    /// <summary>Any other failure (permissions, a sharing violation, IO). Nothing was renamed.</summary>
    Failed,
}
