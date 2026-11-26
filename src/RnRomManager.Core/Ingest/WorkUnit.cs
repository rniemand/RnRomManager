namespace RnRomManager.Core.Ingest;

/// <summary>B7: a planned unit: a set, a lone file, or members skipped with <paramref name="Reason"/>.</summary>
internal sealed record WorkUnit(PlannedKind Kind, IReadOnlyList<IngestFile> Files, string? Reason = null);
