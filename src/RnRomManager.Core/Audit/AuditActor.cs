namespace RnRomManager.Core.Audit;

/// <summary>
/// AU3: who caused a file action. Built once per request (from the session's claims) or once per job (<c>JobRunner</c>).
/// A scheduled job has no user (<see cref="UserId"/> and <see cref="Username"/> null) and shows as <c>System</c>.
/// </summary>
public sealed record AuditActor(int? UserId, string? Username, Guid? JobId);
