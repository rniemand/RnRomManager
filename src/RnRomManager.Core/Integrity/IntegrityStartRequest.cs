namespace RnRomManager.Core.Integrity;

/// <summary><c>POST /api/jobs/integrity</c> (spec §23): <c>{mode:"Quick"|"Deep"}</c>.</summary>
public sealed record IntegrityStartRequest(string? Mode);
