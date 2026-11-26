namespace RnRomManager.Core.Ingest;

/// <summary>Q4: the outcome of a batch review delete.</summary>
public sealed record ReviewBatchDeleteResult(int Deleted, int Skipped, IReadOnlyList<ReviewBatchDeleteFailure> Failed);
