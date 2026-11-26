using RnRomManager.Core.Jobs;

namespace RnRomManager.Api;

/// <summary>Release editing B19: a new save folder migration job, a fresh one per start.</summary>
public delegate IJob SaveFoldersJobFactory();
