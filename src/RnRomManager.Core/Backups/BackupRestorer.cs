using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Backups;

/// <summary>
/// B19: restores <c>Backup:RestoreFrom</c> at startup, after the database is reachable and before migrations. Every
/// refusal throws <see cref="BackupRestoreException"/>, which stops startup (Fatal, exit 1).
/// </summary>
public sealed class BackupRestorer(
    IBackupStore store,
    IOptions<PathsOptions> paths,
    IOptions<BackupOptions> options,
    TimeProvider time,
    ILogger<BackupRestorer> logger)
{
    /// <summary>Restores the configured backup, or does nothing when none is set or it was already restored.</summary>
    /// <returns>True when a restore ran.</returns>
    public async Task<bool> RestoreIfConfiguredAsync(CancellationToken cancellationToken)
    {
        var name = options.Value.RestoreFrom?.Trim();
        if (string.IsNullOrEmpty(name)) return false;

        // Rule 1: a bare file name of an existing file in Paths:Backups.
        if (!BackupCheck.IsBareName(name))
            throw new BackupRestoreException(
                $"Backup:RestoreFrom must be a file name in Paths:Backups, without a folder (got '{name}').");
        var folder = paths.Value.Backups;
        var file = Path.Combine(folder, name);
        if (!File.Exists(file))
            throw new BackupRestoreException($"Backup:RestoreFrom '{name}' doesn't exist in Paths:Backups '{folder}'.");

        // Rule 2: never restore the same file twice.
        var marker = Path.Combine(folder, BackupNames.MarkerFor(name));
        if (File.Exists(marker))
        {
            logger.LogWarning("Backup {FileName} was already restored on {RestoredAt}; skipping. Remove Backup__RestoreFrom",
                name, (await File.ReadAllTextAsync(marker, cancellationToken)).Trim());
            return false;
        }

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        // Rule 3: checked before anything is dropped.
        var inspection = BackupCheck.Open(stream, name, store.KnownMigrations);
        if (inspection.Zip is not { } zip)
            throw new BackupRestoreException(inspection.Message!);

        using (zip)
        {
            var manifest = inspection.Manifest!;
            // Rule 4
            logger.LogWarning("Restoring backup {FileName} (id {BackupId}, created {CreatedAt:O}, app {AppVersion}): every table is replaced",
                name, manifest.BackupId, manifest.CreatedAt, manifest.AppVersion);
            await store.RestoreAsync(zip, manifest, cancellationToken);

            // Rules 5–6
            await File.WriteAllTextAsync(marker,
                time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
            logger.LogWarning("Restored backup {FileName} (id {BackupId}, created {CreatedAt:O}): {Rows}",
                name, manifest.BackupId, manifest.CreatedAt, string.Join(", ", manifest.Tables.Select(t => $"{t.Name} {t.Rows}")));
            logger.LogWarning(
                "The library ZIPs may be newer than this backup: run Integrity → Run quick so the database matches them. " +
                "Then remove Backup__RestoreFrom");
            return true;
        }
    }
}
