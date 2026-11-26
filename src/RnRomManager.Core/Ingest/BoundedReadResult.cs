namespace RnRomManager.Core.Ingest;

/// <param name="Length">The open handle's length.</param>
public sealed record BoundedReadResult(
    BoundedReadStatus Status,
    long Length,
    byte[]? Bytes = null,
    string? Details = null);
