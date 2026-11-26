namespace RnRomManager.Api.Tests.Integration;

internal sealed record AuditLibraryEditRow(
    string Action,
    int? UserId,
    string? Username,
    Guid? JobId,
    Guid? GameId,
    string? Path,
    string? TargetPath,
    string? Details);
