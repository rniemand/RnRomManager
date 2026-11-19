using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Users;

namespace RnRomManager.Core.Backups;

/// <summary>A backup ZIP in <c>Paths:Backups</c> (B17).</summary>
public sealed record BackupFile(string FileName, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>The outcome of the last run, scheduled or manual (B16); kept in memory only.</summary>
public sealed record BackupResult(DateTimeOffset At, bool Ok, string? FileName, string? Error);

/// <summary>
/// Creates database backups (B13), applies retention (B14) and lists them. One run at a time, on its own lock: the
/// store reads a consistent snapshot, so jobs and edits keep running and the job lock isn't taken. A live restore (B27)
/// takes both the job lock and its own.
/// </summary>
public sealed class BackupService(
    IBackupStore store,
    ISettingsRepository settings,
    IOptions<PathsOptions> paths,
    IOptions<BackupOptions> options,
    TimeZoneInfo zone,
    TimeProvider time,
    ILogger<BackupService> logger,
    JobLock jobLock,
    IMaintenanceWindow maintenance,
    IDatabaseLifecycle database,
    IJobRepository jobs,
    IUserRepository users,
    IDiscoveryRepository discovery,
    DatRefreshSettingsService datRefresh,
    IRomStorageSettings romStorage,
    IAuditLog audit)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile BackupResult? _lastResult;

    public bool IsRunning => _lock.CurrentCount == 0;

    public BackupResult? LastResult => _lastResult;

    /// <summary>The container's time zone (<c>TZ</c>) the daily time is in (B15, B18).</summary>
    public string TimeZoneId => zone.Id;

    /// <summary>Raised after <see cref="SaveSettingsAsync"/>, so the scheduler reschedules at once (B16).</summary>
    public event Action? SettingsChanged;

    private string Folder => paths.Value.Backups;

    public async Task<BackupSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        BackupSettings.FromStored(await settings.GetAsync(BackupSettings.Keys.ToList(), cancellationToken));

    /// <exception cref="ArgumentException"><paramref name="value"/> isn't valid (callers validate first).</exception>
    public async Task SaveSettingsAsync(BackupSettings value, CancellationToken cancellationToken)
    {
        if (value.Validate().Count > 0)
            throw new ArgumentException("Invalid backup settings.", nameof(value));
        await settings.SetAsync(value.ToStored(), cancellationToken);
        logger.LogInformation("Backup settings saved: enabled {Enabled}, daily at {Time} ({Zone}), keep {Retention}",
            value.Enabled, value.Time, zone.Id, value.Retention);
        SettingsChanged?.Invoke();
    }

    /// <summary>The next scheduled run (UTC) for <paramref name="value"/>, or null when disabled (B16).</summary>
    public DateTimeOffset? NextRunAt(BackupSettings value) =>
        value.Enabled ? ScheduleCalculator.NextDaily(time.GetUtcNow(), value.TimeOfDay, zone) : null;

    /// <summary>Runs a backup now, then keeps the newest <paramref name="retention"/> backups.</summary>
    /// <exception cref="BackupRunningException">Another backup is running.</exception>
    /// <exception cref="BackupFailedException">The dump or the file write failed; no file was kept.</exception>
    public async Task<BackupFile> CreateAsync(int retention, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, 1);
        if (!await _lock.WaitAsync(0, cancellationToken))
            throw new BackupRunningException();
        try
        {
            return await RunAsync(retention, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// B27: restores <paramref name="fileName"/> while the app runs. Checks run in this order and refuse without changing
    /// anything: the name, the job and backup locks, the file and its pre-drop checks, the maintenance window, a safety
    /// backup. From the drop on nothing is cancelled: store restore, migrations, stale jobs aborted, every session ended,
    /// caches dropped, schedulers woken, then <see cref="AuditActions.BackupRestored"/>.
    /// </summary>
    public async Task<BackupRestoreResult> RestoreAsync(string fileName, AuditActor actor, CancellationToken cancellationToken)
    {
        // 1: exactly the names the backups list shows (never a path, .tmp or .restored file).
        if (!BackupCheck.IsBareName(fileName) || BackupNames.Parse(fileName) is null)
            return new(BackupRestoreStatus.InvalidName);

        // 3: no job, library edit, scheduled backup or retention runs while it holds both locks.
        using var job = jobLock.TryAcquire();
        if (job is null)
            return new(BackupRestoreStatus.JobRunning);
        if (!await _lock.WaitAsync(0, cancellationToken))
            return new(BackupRestoreStatus.BackupRunning);
        try
        {
            // 2: the file stays open until the restore ends.
            var path = Path.Combine(Folder, fileName);
            if (!File.Exists(path))
                return new(BackupRestoreStatus.NotFound);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var inspection = BackupCheck.Open(stream, fileName, store.KnownMigrations);
            if (inspection.Zip is not { } zip)
                return new(inspection.Failure == BackupCheckFailure.TooNew ? BackupRestoreStatus.TooNew : BackupRestoreStatus.InvalidBackup,
                    fileName, Message: inspection.Message);
            using (zip)
            {
                var retention = (await GetSettingsAsync(cancellationToken)).Retention;

                // 4: every other request is 503 and audit writes are held until the window closes.
                await using var window = await maintenance.TryEnterAsync(cancellationToken);
                if (window is null)
                    return new(BackupRestoreStatus.Busy, fileName);

                // 5: under the backup lock already, so RunAsync, not CreateAsync; the source isn't touched by retention.
                string safety;
                try
                {
                    safety = (await RunAsync(retention, cancellationToken, keep: fileName)).FileName;
                }
                catch (BackupFailedException)
                {
                    return new(BackupRestoreStatus.BackupFailed, fileName);
                }

                return await ReplaceAsync(fileName, safety, zip, inspection.Manifest!, actor);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>B27.6–7, B27.10: everything from the drop on, with <see cref="CancellationToken.None"/>.</summary>
    private async Task<BackupRestoreResult> ReplaceAsync(string fileName, string safety, System.IO.Compression.ZipArchive zip,
        BackupManifest manifest, AuditActor actor)
    {
        var none = CancellationToken.None;
        try
        {
            logger.LogWarning("Restoring backup {FileName} (id {BackupId}, created {CreatedAt:O}, app {AppVersion}): every table is replaced; safety backup {Safety}",
                fileName, manifest.BackupId, manifest.CreatedAt, manifest.AppVersion, safety);
            await store.RestoreAsync(zip, manifest, none);
            database.Migrate();
            foreach (var id in await jobs.AbortRunningAsync(time.GetUtcNow().UtcDateTime, none))
                logger.LogWarning("Job {JobId} was Running in the restored backup; marked Aborted", id);
            await users.RotateAllSecurityStampsAsync(none);
            discovery.Invalidate();
            SettingsChanged?.Invoke();
            datRefresh.NotifySettingsChanged();
            // ROM Zip Config B2: a restored value that disagrees with the ZIPs just makes those games pending.
            await romStorage.LoadAsync(none);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restoring backup {FileName} failed after the tables were dropped; the database may be partial. Restore the safety backup {Safety}",
                fileName, safety);
            // Later scheduled backups dump the partial database; retention must not prune the good copies away.
            foreach (var name in new[] { safety, fileName })
            {
                try
                {
                    File.WriteAllText(Path.Combine(Folder, BackupNames.KeepFor(name)), "kept after a failed restore; delete this file to let retention prune it");
                }
                catch (Exception keepEx) when (keepEx is IOException or UnauthorizedAccessException)
                {
                    logger.LogError("Couldn't pin {FileName} against retention ({Error}); copy it somewhere safe", name, keepEx.Message);
                }
            }
            return new(BackupRestoreStatus.RestoreFailed, fileName, safety,
                $"The restore failed and the database may be incomplete. Restore the safety backup {safety}.");
        }

        audit.Record(new AuditEvent(AuditActions.BackupRestored, actor, Details: $"{fileName}; safety backup {safety}"));
        logger.LogWarning("Restored backup {FileName} (id {BackupId}, created {CreatedAt:O}): {Rows}",
            fileName, manifest.BackupId, manifest.CreatedAt, string.Join(", ", manifest.Tables.Select(t => $"{t.Name} {t.Rows}")));
        logger.LogWarning("The library ZIPs may be newer than this backup: run Integrity → Run quick so the database matches them.");
        return new(BackupRestoreStatus.Restored, fileName, safety);
    }

    /// <summary>Every backup in <c>Paths:Backups</c>, newest first.</summary>
    public IReadOnlyList<BackupFile> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return new DirectoryInfo(Folder).EnumerateFiles()
            .Select(f => (File: f, Parsed: BackupNames.Parse(f.Name)))
            .Where(x => x.Parsed is not null)
            .OrderByDescending(x => x.Parsed!.Value.CreatedAt)
            .ThenByDescending(x => x.Parsed!.Value.N)
            .Select(x => new BackupFile(x.File.Name, x.File.Length, x.Parsed!.Value.CreatedAt))
            .ToList();
    }

    /// <param name="keep">B27.5: a backup that is neither deleted nor counted by this run's retention (the one being restored).</param>
    private async Task<BackupFile> RunAsync(int retention, CancellationToken cancellationToken, string? keep = null)
    {
        var startedAt = time.GetUtcNow();
        var backupId = Guid.NewGuid();
        string? name = null;
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Folder);
            DeleteStaleTemps();
            name = FreeName(startedAt);
            var final = Path.Combine(Folder, name);
            temp = final + BackupNames.TempSuffix;

            BackupManifest manifest;
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                manifest = await store.DumpAsync(stream, backupId, startedAt, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temp, final);
            temp = null;
            var file = new BackupFile(name, new FileInfo(final).Length, startedAt);
            _lastResult = new BackupResult(time.GetUtcNow(), true, name, null);
            logger.LogInformation("Backup {FileName} written: {Bytes} bytes, {Tables} tables, {Rows} rows, id {BackupId}",
                name, file.SizeBytes, manifest.Tables.Count, manifest.Tables.Sum(t => t.Rows), backupId);
            ApplyRetention(retention, keep);
            return file;
        }
        catch (Exception ex)
        {
            if (temp is not null) TryDelete(temp);
            var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            _lastResult = new BackupResult(time.GetUtcNow(), false, null, cancelled ? "The backup was cancelled." : ex.Message);
            if (cancelled)
            {
                logger.LogWarning("Backup {FileName} was cancelled", name);
                throw;
            }

            logger.LogError(ex, "Backup {FileName} failed", name);
            throw new BackupFailedException($"The backup failed: {ex.Message}", ex);
        }
    }

    /// <summary>B13: two runs in the same second get <c> (2)</c>, <c> (3)</c>….</summary>
    private string FreeName(DateTimeOffset startedAt)
    {
        for (var n = 1; ; n++)
        {
            var name = BackupNames.For(startedAt, n);
            var path = Path.Combine(Folder, name);
            if (!File.Exists(path) && !File.Exists(path + BackupNames.TempSuffix))
                return name;
        }
    }

    /// <summary>B14: leftovers of a crashed run.</summary>
    private void DeleteStaleTemps()
    {
        foreach (var file in Directory.EnumerateFiles(Folder).Where(f => BackupNames.IsTemp(Path.GetFileName(f))))
        {
            logger.LogWarning("Deleting the incomplete backup {FileName} left by an earlier run", Path.GetFileName(file));
            File.Delete(file);
        }
    }

    /// <summary>
    /// B14: deletes the oldest backups beyond <paramref name="retention"/>. A backup with a <c>.restored</c> marker or
    /// named by <c>Backup:RestoreFrom</c> or <paramref name="keep"/> is never deleted and doesn't count. Failures are
    /// logged, not thrown: the new backup is already safe.
    /// </summary>
    internal void ApplyRetention(int retention, string? keep = null)
    {
        var restoreFrom = options.Value.RestoreFrom?.Trim();
        var counted = List()
            .Where(b => !string.Equals(b.FileName, restoreFrom, StringComparison.Ordinal))
            .Where(b => !string.Equals(b.FileName, keep, StringComparison.Ordinal))
            .Where(b => !File.Exists(Path.Combine(Folder, BackupNames.MarkerFor(b.FileName))))
            .Where(b => !File.Exists(Path.Combine(Folder, BackupNames.KeepFor(b.FileName))));
        foreach (var old in counted.Skip(retention))
        {
            try
            {
                File.Delete(Path.Combine(Folder, old.FileName));
                logger.LogInformation("Deleted old backup {FileName} (keeping {Retention})", old.FileName, retention);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Couldn't delete old backup {FileName}: {Error}", old.FileName, ex.Message);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't delete {Path}: {Error}", path, ex.Message);
        }
    }
}
