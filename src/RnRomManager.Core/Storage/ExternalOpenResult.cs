namespace RnRomManager.Core.Storage;

/// <summary>The result of a checked open. <paramref name="Stream"/> and <paramref name="Snapshot"/> are set only for <see cref="ExternalPathStatus.Found"/>.</summary>
/// <param name="Path">As <see cref="ExternalPathCheck.Path"/>.</param>
/// <param name="Stream">The validated read stream itself; the caller disposes it and never reopens the path.</param>
/// <param name="Snapshot">Read from the open handle, so it describes exactly the file <paramref name="Stream"/> reads.</param>
public sealed record ExternalOpenResult(
    ExternalPathStatus Status,
    string Path,
    FileStream? Stream = null,
    ExternalFileSnapshot? Snapshot = null,
    string? Details = null);
