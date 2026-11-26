namespace RnRomManager.Core.Ingest;

/// <param name="Members">
/// File names as on disk. For a set: the cue or <c>.toc</c> first, then the files it names in order; the <c>.ccd</c>, its
/// <c>.img</c> and its <c>.sub</c> when there is one; or the <c>.mds</c> and its <c>.mdf</c>.
/// </param>
/// <param name="Reason">An <see cref="IngestReason"/> for <see cref="PlannedKind.Skipped"/>; null otherwise.</param>
public sealed record PlannedUnit(PlannedKind Kind, IReadOnlyList<string> Members, string? Reason = null);
