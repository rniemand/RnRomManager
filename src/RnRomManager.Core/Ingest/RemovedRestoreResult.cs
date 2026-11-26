namespace RnRomManager.Core.Ingest;

/// <param name="Code"><c>JobRunning</c> when the lock was busy (nothing moved), else null.</param>
public sealed record RemovedRestoreResult(IReadOnlyList<RestoredFile> Restored, IReadOnlyList<IngestDeleteRef> Failed, string? Code = null);
