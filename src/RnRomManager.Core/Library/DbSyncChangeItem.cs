namespace RnRomManager.Core.Library;

public sealed record DbSyncChangeItem(DbSyncChange Kind, Guid GameId, string ZipPath);
