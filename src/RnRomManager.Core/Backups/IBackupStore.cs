using System.IO.Compression;

namespace RnRomManager.Core.Backups;

/// <summary>Reads the whole database into a backup ZIP (B13) and, later, restores one (B19).</summary>
public interface IBackupStore
{
    /// <summary>
    /// Writes <c>manifest.json</c>, <c>schema.sql</c> and <c>data/&lt;table&gt;.jsonl</c> as a ZIP into
    /// <paramref name="destination"/> (left open), reading every table inside one consistent snapshot.
    /// </summary>
    Task<BackupManifest> DumpAsync(Stream destination, Guid backupId, DateTimeOffset createdAt, CancellationToken cancellationToken);

    /// <summary>Migration script names this app version knows (as journalled in <c>schema_versions</c>).</summary>
    IReadOnlySet<string> KnownMigrations { get; }

    /// <summary>
    /// B19 step 4: drops every base table, runs <c>schema.sql</c> and inserts every <c>data/*.jsonl</c>, checking the
    /// row counts against <paramref name="manifest"/>. Not transactional (DDL auto-commits) but safe to retry.
    /// </summary>
    /// <exception cref="BackupRestoreException">A row count doesn't match, or the archive is incomplete.</exception>
    Task RestoreAsync(ZipArchive backup, BackupManifest manifest, CancellationToken cancellationToken);
}
