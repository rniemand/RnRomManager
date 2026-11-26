using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Hands the test the job's progress, then runs the job.</summary>
internal sealed class Capture(IJob inner, Action<JobProgress> seen) : IJob
{
    public JobType Type => inner.Type;
    public JobMode? Mode => inner.Mode;

    public Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        seen(context.Progress);
        return inner.RunAsync(context, cancellationToken);
    }
}
