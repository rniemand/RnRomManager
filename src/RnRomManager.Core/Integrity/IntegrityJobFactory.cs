using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Integrity;

/// <summary>A fresh <see cref="IntegrityJob"/> for one run (used by the scheduler and <c>POST /api/jobs/integrity</c>).</summary>
public delegate IJob IntegrityJobFactory(JobMode mode);
