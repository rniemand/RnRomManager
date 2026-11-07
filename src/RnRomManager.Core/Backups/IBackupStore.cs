namespace RnRomManager.Core.Backups;

/// <summary>Reads the whole database into a backup ZIP (B13) and, later, restores one (B19).</summary>
public interface IBackupStore
{
    /// <summary>
    /// Writes <c>manifest.json</c>, <c>schema.sql</c> and <c>data/&lt;table&gt;.jsonl</c> as a ZIP into
    /// <paramref name="destination"/> (left open), reading every table inside one consistent snapshot.
    /// </summary>
    Task<BackupManifest> DumpAsync(Stream destination, Guid backupId, DateTimeOffset createdAt, CancellationToken cancellationToken);
}
