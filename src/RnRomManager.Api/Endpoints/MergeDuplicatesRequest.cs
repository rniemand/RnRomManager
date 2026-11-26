namespace RnRomManager.Api.Endpoints;

/// <summary><c>POST /api/reports/duplicates/merge</c> (RP8): every source is merged into the target, in order.</summary>
public sealed record MergeDuplicatesRequest(Guid? TargetGameId, IReadOnlyList<Guid>? SourceGameIds);
