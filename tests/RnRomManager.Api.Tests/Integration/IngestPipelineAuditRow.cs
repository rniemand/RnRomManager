namespace RnRomManager.Api.Tests.Integration;

internal sealed record IngestPipelineAuditRow(
    string Action,
    string? SystemId,
    Guid? GameId,
    int? UserId,
    string? Username,
    Guid? JobId,
    string? Path,
    string? TargetPath,
    string? Details);
