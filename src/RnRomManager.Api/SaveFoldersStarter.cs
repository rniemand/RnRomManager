using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;

namespace RnRomManager.Api;

/// <summary>Release editing B19: a new save folder migration job, a fresh one per start.</summary>
public delegate IJob SaveFoldersJobFactory();

/// <summary>
/// Release editing B19, as <see cref="StorageMigrationStarter"/>: once the app has started, starts <see cref="SaveFoldersJob"/>
/// unless <see cref="SaveFoldersJob.MigratedSetting"/> is <c>"true"</c>. A busy job lock or a failed check is retried every
/// 5 minutes until a run has started; a started run is never retried here (the job sets the setting itself).
/// </summary>
public sealed class SaveFoldersStarter(IHostApplicationLifetime lifetime, JobRunner runner, SaveFoldersJobFactory createJob,
    ISettingsRepository settings, TimeProvider time, ILogger<SaveFoldersStarter> logger) : BackgroundService
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
                var stored = await settings.GetAsync([SaveFoldersJob.MigratedSetting], stoppingToken).ConfigureAwait(false);
                if (stored.TryGetValue(SaveFoldersJob.MigratedSetting, out var value) && value == "true")
                    return;
                if (await runner.StartAsync(createJob(), JobTrigger.Startup, null, stoppingToken).ConfigureAwait(false) is { } job)
                {
                    logger.LogInformation("Save folder migration job {JobId} started", job.Id);
                    return;
                }
                logger.LogInformation("Save folder migration waits: another job is running; trying again in {Interval}", RetryInterval);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Save folder migration couldn't start; trying again in {Interval}", RetryInterval);
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
