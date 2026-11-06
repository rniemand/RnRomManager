namespace RnRomManager.Core.Audit;

/// <summary>
/// AU1: one file action. Paths are relative and prefixed <c>roms/</c> or <c>ingest/</c> (see <see cref="AuditPaths"/>), never
/// absolute. <see cref="OccurredAt"/> is stamped by <see cref="IAuditLog.Record"/>.
/// </summary>
/// <param name="Action">An <see cref="AuditActions"/> name.</param>
public sealed record AuditEvent(string Action, AuditActor Actor, string? SystemId = null, Guid? GameId = null, string? Path = null,
    string? TargetPath = null, string? Details = null)
{
    public DateTime OccurredAt { get; init; }
}
