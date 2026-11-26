namespace RnRomManager.Core.Storage;

/// <summary>Something <see cref="ExternalRomPath.ScanFolder"/> refused to open or walk into.</summary>
/// <param name="RelativePath">
/// Relative to the root, <c>/</c> separators. For names that differ only in case it is the first spelling in
/// ordinal order; everything at or below any of those spellings was skipped.
/// </param>
/// <param name="Status"><see cref="ExternalPathStatus.Unsafe"/> or <see cref="ExternalPathStatus.Unreadable"/>.</param>
/// <param name="IsSpecialFile">True for a FIFO, socket or device: a single entry that can't be a folder. A link, an unreadable entry and case variants may each be or hide one.</param>
public sealed record CheckedScanProblem(
    string RelativePath,
    ExternalPathStatus Status,
    string Details,
    bool IsSpecialFile = false);
