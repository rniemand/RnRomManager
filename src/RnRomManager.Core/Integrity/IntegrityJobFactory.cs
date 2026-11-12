using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Integrity;

/// <summary>
/// A fresh <see cref="IntegrityJob"/> for one run (used by the scheduler, <c>POST /api/jobs/integrity</c> and, with
/// <see cref="JobMode.Game"/> and the game's ZIP path, the game check after a first real box art, B14).
/// </summary>
public delegate IJob IntegrityJobFactory(JobMode mode, string? zipPath = null);
