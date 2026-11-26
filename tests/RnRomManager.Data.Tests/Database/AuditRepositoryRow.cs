namespace RnRomManager.Data.Tests.Database;

internal sealed class AuditRepositoryRow
{
    public DateTime OccurredAt { get; init; }
    public string Action { get; init; } = "";
    public string? SystemId { get; init; }
    public Guid? GameId { get; init; }
    public int? UserId { get; init; }
    public string? Username { get; init; }
    public Guid? JobId { get; init; }
    public string? Path { get; init; }
    public string? TargetPath { get; init; }
    public string? Details { get; init; }
}
