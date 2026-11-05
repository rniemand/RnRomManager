namespace RnRomManager.Core.Library;

/// <summary><c>POST /api/games/{id}/merge</c> (spec §23).</summary>
public sealed record MergeRequest(Guid? TargetGameId);
