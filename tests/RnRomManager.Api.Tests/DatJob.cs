using RnRomManager.Core.Jobs;

namespace RnRomManager.Api.Tests;

/// <summary>A DAT refresh stand-in that finishes at once.</summary>
internal sealed class DatJob : IJob
{
    public JobType Type => JobType.DatRefresh;
    public JobMode? Mode => null;
    public Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
}
