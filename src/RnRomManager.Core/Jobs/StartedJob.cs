namespace RnRomManager.Core.Jobs;

/// <param name="Completion">Ends when the job's row is final (tests and shutdown wait on it).</param>
public sealed record StartedJob(Guid Id, Task Completion);
