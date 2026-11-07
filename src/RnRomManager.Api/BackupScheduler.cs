using RnRomManager.Core.Backups;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Api;

/// <summary>
/// B16: the daily database backup at <c>backup.time</c> (container local time), driven by <see cref="TimeProvider"/>.
/// Saving the settings wakes it to reschedule; missed runs aren't caught up; a run that finds a manual backup running
/// is skipped (<c>Info</c>). Failures are logged at Error by <see cref="BackupService"/>.
/// </summary>
public sealed class BackupScheduler(BackupService backups, TimeZoneInfo zone, TimeProvider time, ILogger<BackupScheduler> logger)
    : BackgroundService
{
    /// <summary>How long to wait before re-reading the settings after the database failed.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private CancellationTokenSource _wake = new();

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        backups.SettingsChanged += Wake;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        backups.SettingsChanged -= Wake;
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        lock (_gate)
            _wake.Dispose();
        base.Dispose();
    }

    private void Wake()
    {
        lock (_gate)
            _wake.Cancel();
    }

    /// <summary>A fresh wake token: a change signalled before this point was already seen by the caller.</summary>
    private CancellationToken WakeToken()
    {
        lock (_gate)
        {
            if (_wake.IsCancellationRequested)
            {
                _wake.Dispose();
                _wake = new CancellationTokenSource();
            }

            return _wake.Token;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The last run's due time: the next one is strictly after it, so a quick run never repeats in the same minute.
        DateTimeOffset? lastDue = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Taken before reading the settings, so a save during the read still wakes the wait below.
            var wake = WakeToken();
            BackupSettings settings;
            DateTimeOffset? due;
            try
            {
                settings = await backups.GetSettingsAsync(stoppingToken).ConfigureAwait(false);
                var now = time.GetUtcNow();
                var after = lastDue is { } last && last > now ? last : now;
                due = settings.Enabled ? ScheduleCalculator.NextDaily(after, settings.TimeOfDay, zone) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Backup schedule couldn't be read; retrying in {Delay}", RetryDelay);
                if (!await WaitAsync(RetryDelay, wake, stoppingToken).ConfigureAwait(false) && stoppingToken.IsCancellationRequested)
                    return;
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            logger.LogInformation("Next scheduled backup: {Next}", due?.ToString("O") ?? "disabled");
            var wait = due is { } d ? d - time.GetUtcNow() : Timeout.InfiniteTimeSpan;
            if (!await WaitAsync(wait, wake, stoppingToken).ConfigureAwait(false))
            {
                if (stoppingToken.IsCancellationRequested) return;
                continue; // settings changed: reschedule
            }

            lastDue = due;
            await RunAsync(settings.Retention, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>True when the wait elapsed; false when woken by a settings change or stopping.</summary>
    private async Task<bool> WaitAsync(TimeSpan wait, CancellationToken wake, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(wake, stoppingToken);
        try
        {
            if (wait != Timeout.InfiniteTimeSpan && wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            await Task.Delay(wait, time, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunAsync(int retention, CancellationToken stoppingToken)
    {
        try
        {
            await backups.CreateAsync(retention, stoppingToken).ConfigureAwait(false);
        }
        catch (BackupRunningException)
        {
            logger.LogInformation("Scheduled backup skipped: a backup is already running");
        }
        catch (BackupFailedException)
        {
            // Already logged at Error with the cause; the next run is tomorrow.
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled backup failed");
        }
    }
}
