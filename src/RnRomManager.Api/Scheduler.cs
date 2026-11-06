using System.Globalization;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api;

/// <summary>
/// Startup step 10 (spec §4.1): the daily quick integrity check at local <c>Integrity:QuickCheckTime</c> (§4, §12) and
/// the log and audit purges at startup and daily at 00:05 UTC (§13, AU5). Driven by <see cref="TimeProvider"/>. Runs missed while the
/// app was down aren't caught up. A quick check that finds the job lock busy is skipped (<c>Info</c>, D20).
/// </summary>
public sealed class Scheduler(JobRunner runner, IntegrityJobFactory integrity, LogRetention logs, IAuditLog audit,
    IOptions<IntegrityOptions> options, TimeZoneInfo zone, TimeProvider time, ILogger<Scheduler> logger) : BackgroundService
{
    /// <summary>The next quick check (UTC), or null when <c>QuickCheckTime</c> is empty (disabled).</summary>
    public DateTimeOffset? NextQuickCheckAt() => QuickCheckTime is { } at ? ScheduleCalculator.NextDaily(time.GetUtcNow(), at, zone) : null;

    private TimeOnly? QuickCheckTime => options.Value.QuickCheckTime.Length == 0
        ? null
        : TimeOnly.ParseExact(options.Value.QuickCheckTime, "HH:mm", CultureInfo.InvariantCulture);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PurgeLogs();
        await PurgeAuditAsync().ConfigureAwait(false);
        var now = time.GetUtcNow();
        var nextQuick = NextQuickCheckAt();
        var nextPurge = ScheduleCalculator.NextLogPurge(now);
        logger.LogInformation("Scheduler started: quick check {NextQuick}, log purge {NextPurge}",
            nextQuick?.ToString("O") ?? "disabled", nextPurge.ToString("O"));

        while (!stoppingToken.IsCancellationRequested)
        {
            var due = nextQuick is { } q && q < nextPurge ? q : nextPurge;
            var wait = due - time.GetUtcNow();
            try
            {
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Next runs are strictly after the later of "due" and "now": a long sleep never triggers a catch-up run.
            now = time.GetUtcNow();
            var after = now > due ? now : due;
            if (nextQuick is { } quick && quick <= now)
            {
                await StartQuickCheckAsync(stoppingToken).ConfigureAwait(false);
                nextQuick = QuickCheckTime is { } at ? ScheduleCalculator.NextDaily(after, at, zone) : null;
            }
            if (nextPurge <= now)
            {
                PurgeLogs();
                await PurgeAuditAsync().ConfigureAwait(false);
                nextPurge = ScheduleCalculator.NextLogPurge(after);
            }
        }
    }

    private async Task StartQuickCheckAsync(CancellationToken stoppingToken)
    {
        try
        {
            var started = await runner.StartAsync(integrity(JobMode.Quick), JobTrigger.Schedule, null, stoppingToken).ConfigureAwait(false);
            if (started is null)
                logger.LogInformation("Scheduled quick integrity check skipped: another job is running");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scheduled quick integrity check couldn't start");
        }
    }

    /// <summary>AU5: its own catch-all, because an unhandled exception here (e.g. the DB is down) would stop the host.</summary>
    private async Task PurgeAuditAsync()
    {
        try
        {
            var deleted = await audit.PurgeAsync(time.GetUtcNow().UtcDateTime - AuditLog.Retention, CancellationToken.None)
                .ConfigureAwait(false);
            if (deleted > 0)
                logger.LogInformation("Audit purge removed {Count} rows", deleted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audit purge failed");
        }
    }

    private void PurgeLogs()
    {
        try
        {
            logs.Purge();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Log purge failed");
        }
    }
}
