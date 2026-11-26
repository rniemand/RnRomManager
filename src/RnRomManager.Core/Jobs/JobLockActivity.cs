namespace RnRomManager.Core.Jobs;

/// <summary>L1: what holds the job lock: a job (its <see cref="JobType"/>) or another activity such as "Saving changes".</summary>
public sealed record JobLockActivity(string Activity, JobType? JobType);
