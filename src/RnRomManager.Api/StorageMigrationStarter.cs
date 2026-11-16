using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api;

/// <summary>A new storage migration job (S4); a fresh one per start.</summary>
public delegate IJob StorageMigrationJobFactory();

/// <summary>
/// S4: once the app has started (so it never delays startup or readiness), starts the storage migration job when legacy
/// games remain. A busy job lock or a failed check is retried every 5 minutes until no legacy games remain or a run has
/// started; a started run is never retried here, whatever its outcome (Admins re-run it from Integrity).
/// </summary>
public sealed class StorageMigrationStarter(IHostApplicationLifetime lifetime, JobRunner runner, StorageMigrationJobFactory createJob,
    IGameRepository games, ISystemRegistry systems, TimeProvider time, ILogger<StorageMigrationStarter> logger) : BackgroundService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>The ids of the systems that can still have legacy games.</summary>
    public static IReadOnlyList<string> LegacySystems(ISystemRegistry systems) =>
        [.. systems.All.Where(h => h.LegacyEmbeddedRoms).Select(h => h.Id)];

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
                var legacy = await games.ListLegacyEmbeddedAsync(LegacySystems(systems), stoppingToken).ConfigureAwait(false);
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
