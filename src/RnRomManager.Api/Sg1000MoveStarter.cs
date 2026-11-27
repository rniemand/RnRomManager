using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;

namespace RnRomManager.Api;

/// <summary>
/// SG-1000 S13, as <see cref="StorageMigrationStarter"/>: once the app has started, starts <see cref="Sg1000MoveJob"/> when a
/// Master System game's releases were all dropped as <c>.sg</c>. A busy job lock or a failed check is retried every 5 minutes
/// until a run has started; a started run is never retried in this process. No setting: every start re-checks.
/// </summary>
public sealed class Sg1000MoveStarter(
    IHostApplicationLifetime lifetime,
    JobRunner runner,
    Sg1000MoveJobFactory createJob,
    IGameRepository games,
    TimeProvider time,
    ILogger<Sg1000MoveStarter> logger) : BackgroundService
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
                var candidates = await games.ListByOriginalExtensionAsync("sms", ".sg", stoppingToken).ConfigureAwait(false);
                if (candidates.Count == 0)
                    return;
                if (await runner.StartAsync(createJob(), JobTrigger.Startup, null, stoppingToken).ConfigureAwait(false) is { } job)
                {
                    logger.LogInformation("SG-1000 move job {JobId} started for {Count} game(s)", job.Id, candidates.Count);
                    return;
                }
                logger.LogInformation("SG-1000 move waits: another job is running; trying again in {Interval}", RetryInterval);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SG-1000 move couldn't start; trying again in {Interval}", RetryInterval);
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
