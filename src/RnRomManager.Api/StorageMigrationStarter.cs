using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api;

/// <summary>
/// S4, B11: once the app has started (so it never delays startup or readiness), starts the storage job when games still
/// wait to be moved, in either direction. A busy job lock or a failed check is retried every 5 minutes until no games remain or a run has
/// started; a started run is never retried here, whatever its outcome (Admins re-run it from Integrity).
/// </summary>
public sealed class StorageMigrationStarter(
    IHostApplicationLifetime lifetime,
    JobRunner runner,
    StorageMigrationJobFactory createJob,
    IGameRepository games,
    ISystemRegistry systems,
    TimeProvider time,
    ILogger<StorageMigrationStarter> logger) : BackgroundService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource();
        using (lifetime.ApplicationStarted.Register(started.SetResult))
        using (stoppingToken.Register(() => started.TrySetCanceled()))
        {
            try
            {
                await started.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var legacy = await StorageMigrationJob.ListMovesAsync(games, systems, stoppingToken).ConfigureAwait(false);
                if (legacy.Count == 0)
                    return;
                if (await runner.StartAsync(createJob(), JobTrigger.Startup, null, stoppingToken).ConfigureAwait(false) is { } job)
                {
                    logger.LogInformation("Storage migration job {JobId} started for {Count} game(s)", job.Id, legacy.Count);
                    return;
                }
                logger.LogInformation("Storage migration waits: another job is running; trying again in {Interval}", RetryInterval);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Storage migration couldn't start; trying again in {Interval}", RetryInterval);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await Task.Delay(RetryInterval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
