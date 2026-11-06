namespace RnRomManager.Core.Audit;

public sealed record AuditQuery(string? Action = null, string? System = null, string? User = null,
    bool Scheduled = false, string? Q = null, DateTime? From = null, DateTime? To = null);

public sealed record AuditItem(long Id, DateTime OccurredAt, string Action, string? System, Guid? GameId,
    string? User, Guid? JobId, string? Path, string? TargetPath, string? Details);
