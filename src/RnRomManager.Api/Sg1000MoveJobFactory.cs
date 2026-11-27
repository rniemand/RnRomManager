using RnRomManager.Core.Jobs;

namespace RnRomManager.Api;

/// <summary>SG-1000 S13: a new SG-1000 move job, a fresh one per start.</summary>
public delegate IJob Sg1000MoveJobFactory();
