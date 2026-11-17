using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Api;

/// <summary>
/// B29: the daily DAT refresh at <c>dats.autoRefresh.time</c> (container local time), driven by <see cref="TimeProvider"/>,
/// started like the scheduled quick check (trigger Schedule, no user). Saving the settings wakes it to reschedule. When
/// the job lock is busy it retries every minute until the job starts or the next day's due time comes (that day's run is
/// then skipped). Missed runs while the app was down aren't caught up; failures are the job's own.
/// </summary>
public sealed class DatRefreshScheduler(
    DatRefreshSettingsService settings,
    DatRefreshJobFactory jobs,
    JobRunner runner,
    TimeZoneInfo zone,
    TimeProvider time,
    ILogger<DatRefreshScheduler> logger) : BackgroundService
{
    /// <summary>How long to wait before retrying a busy lock, or re-reading the settings after the database failed.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private CancellationTokenSource _wake = new();

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        settings.SettingsChanged += Wake;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        settings.SettingsChanged -= Wake;
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
            var wake = WakeToken();
            DatRefreshSettings current;
            DateTimeOffset? due;
            try
            {
                current = await settings.GetAsync(stoppingToken).ConfigureAwait(false);
                var now = time.GetUtcNow();
                var after = lastDue is { } last && last > now ? last : now;
                due = current.Enabled ? ScheduleCalculator.NextDaily(after, current.TimeOfDay, zone) : null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DAT refresh schedule couldn't be read; retrying in {Delay}", RetryDelay);
                if (!await WaitAsync(RetryDelay, wake, stoppingToken).ConfigureAwait(false) && stoppingToken.IsCancellationRequested)
                    return;
                continue;
            }

            logger.LogInformation("Next scheduled DAT refresh: {Next}", due?.ToString("O") ?? "disabled");
            var wait = due is { } d ? d - time.GetUtcNow() : Timeout.InfiniteTimeSpan;
            if (!await WaitAsync(wait, wake, stoppingToken).ConfigureAwait(false))
            {
                if (stoppingToken.IsCancellationRequested) return;
                continue; // settings changed: reschedule
            }

            var ran = due!.Value;
            while (!await TryStartAsync(stoppingToken).ConfigureAwait(false))
            {
                var next = ScheduleCalculator.NextDaily(ran, current.TimeOfDay, zone);
                var retry = next - time.GetUtcNow();
                if (!await WaitAsync(retry < RetryDelay ? retry : RetryDelay, wake, stoppingToken).ConfigureAwait(false))
                    break; // stopping or settings changed: the outer loop decides
                if (time.GetUtcNow() >= next)
                {
                    logger.LogInformation("Scheduled DAT refresh for {Due} skipped: another job held the lock until the next run", ran.ToString("O"));
                    ran = next;
                }
            }
            lastDue = ran;
        }
    }

    /// <summary>True when the job started (or couldn't start for a reason a retry won't fix); false when the lock is busy.</summary>
    private async Task<bool> TryStartAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await runner.StartAsync(jobs(), JobTrigger.Schedule, null, stoppingToken).ConfigureAwait(false) is not null)
                return true;
            logger.LogInformation("Scheduled DAT refresh waiting: another job is running; retrying in {Delay}", RetryDelay);
            return false;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled DAT refresh couldn't start");
            return true;
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
}
