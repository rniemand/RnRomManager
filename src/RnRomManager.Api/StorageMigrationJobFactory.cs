using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api;

/// <summary>A new storage migration job (S4); a fresh one per start, applying <paramref name="change"/> first (B7).</summary>
public delegate IJob StorageMigrationJobFactory(RomStorageChange? change = null);
