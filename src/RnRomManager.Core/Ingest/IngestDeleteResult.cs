namespace RnRomManager.Core.Ingest;

/// <param name="Code"><c>JobRunning</c> when the lock was busy (nothing deleted), else null.</param>
public sealed record IngestDeleteResult(int Deleted, IReadOnlyList<IngestDeleteRef> Failed, string? Code = null);
