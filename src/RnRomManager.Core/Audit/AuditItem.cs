namespace RnRomManager.Core.Audit;

public sealed record AuditItem(
    long Id,
    DateTime OccurredAt,
    string Action,
    string? System,
    Guid? GameId,
    string? User,
    Guid? JobId,
    string? Path,
    string? TargetPath,
    string? Details,
    string? GameTitle);
