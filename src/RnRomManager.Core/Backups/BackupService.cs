using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Backups;

/// <summary>A backup ZIP in <c>Paths:Backups</c> (B17).</summary>
public sealed record BackupFile(string FileName, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>The outcome of the last run, scheduled or manual (B16); kept in memory only.</summary>
public sealed record BackupResult(DateTimeOffset At, bool Ok, string? FileName, string? Error);

/// <summary>
/// Creates database backups (B13), applies retention (B14) and lists them. One run at a time, on its own lock: the
/// store reads a consistent snapshot, so jobs and edits keep running and the job lock isn't taken.
/// </summary>
public sealed class BackupService(
    IBackupStore store,
    ISettingsRepository settings,
    IOptions<PathsOptions> paths,
    IOptions<BackupOptions> options,
    TimeZoneInfo zone,
    TimeProvider time,
    ILogger<BackupService> logger)
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

    private async Task<BackupFile> RunAsync(int retention, CancellationToken cancellationToken)
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
            ApplyRetention(retention);
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
    /// named by <c>Backup:RestoreFrom</c> is never deleted and doesn't count. Failures are logged, not thrown: the new
    /// backup is already safe.
    /// </summary>
    private void ApplyRetention(int retention)
    {
        var restoreFrom = options.Value.RestoreFrom?.Trim();
        var counted = List()
            .Where(b => !string.Equals(b.FileName, restoreFrom, StringComparison.Ordinal))
            .Where(b => !File.Exists(Path.Combine(Folder, BackupNames.MarkerFor(b.FileName))));
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
