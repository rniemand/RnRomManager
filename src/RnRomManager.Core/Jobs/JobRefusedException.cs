namespace RnRomManager.Core.Jobs;

/// <summary>The job's <see cref="IJob.Preflight"/> refused the start; no row was written and the lock is free again.</summary>
public sealed class JobRefusedException(string code) : Exception($"Job refused: {code}.")
{
    public string Code { get; } = code;
}
