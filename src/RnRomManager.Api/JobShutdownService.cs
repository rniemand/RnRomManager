using RnRomManager.Core.Jobs;

namespace RnRomManager.Api;

/// <summary>SIGTERM: cancels the running job and waits for it within <c>HostOptions.ShutdownTimeout</c> (spec §3.1).</summary>
public sealed class JobShutdownService(JobRunner runner) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => runner.StopAsync(cancellationToken);
}
