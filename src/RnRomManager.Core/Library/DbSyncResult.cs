namespace RnRomManager.Core.Library;

public sealed record DbSyncResult(IReadOnlyList<DbSyncChangeItem> Changes, IReadOnlyList<DbSyncSkipItem> Skipped);
