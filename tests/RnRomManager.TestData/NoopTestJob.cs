using RnRomManager.Core.Jobs;

namespace RnRomManager.TestData;

/// <summary>
/// A job that does nothing until a test releases it (plan S21). Reports progress 0/3 on <c>item-1</c>, then waits
/// for <see cref="Release"/> (or cancellation, unless <see cref="IgnoreCancel"/>), then throws <see cref="Throw"/>
/// or returns <c>{ done: 1 }</c>.
/// </summary>
public sealed class NoopTestJob : IJob
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? Throw { get; init; }

    /// <summary>Keeps running after cancel (a job that misses the shutdown timeout).</summary>
    public bool IgnoreCancel { get; init; }

    /// <summary>The context the runner passed in (AU3 actor checks).</summary>
    public JobContext? Context { get; private set; }

    /// <summary>B4: a job <c>Cancel</c> and shutdown leave alone.</summary>
    public bool Cancellable { get; init; } = true;

    public JobType Type => JobType.Integrity;
    public JobMode? Mode => JobMode.Quick;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        Context = context;
        context.Progress.SetTotal(3);
        context.Progress.SetCurrentItem("item-1");
        Started.TrySetResult();
        await (IgnoreCancel ? Release.Task : Release.Task.WaitAsync(cancellationToken)).ConfigureAwait(false);
        if (Throw is not null)
            throw Throw;
        context.Progress.ItemDone();
        return new { done = 1 };
    }
}
