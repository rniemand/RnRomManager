namespace RnRomManager.Core.Library;

/// <summary><c>POST /api/games/{id}/roms/{romId}/move</c> (spec §23): exactly one of the two.</summary>
public sealed record MoveReleaseRequest(Guid? TargetGameId, string? NewGameTitle);
