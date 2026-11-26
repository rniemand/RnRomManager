namespace RnRomManager.Core.Ingest;

/// <summary>How a <see cref="BoundedRomRead"/> ended.</summary>
public enum BoundedReadStatus
{
    /// <summary>Exactly the handle's length was read into <see cref="BoundedReadResult.Bytes"/>.</summary>
    Read,

    /// <summary>The open handle's length is outside the limits; nothing was allocated.</summary>
    OutOfRange,

    /// <summary>The file ended before the handle's length, or grew past it, while it was read.</summary>
    Changed,
}
