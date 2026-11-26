namespace RnRomManager.Core.Backups;

/// <summary>A backup ZIP in <c>Paths:Backups</c> (B17).</summary>
public sealed record BackupFile(string FileName, long SizeBytes, DateTimeOffset CreatedAt);
