namespace RnRomManager.Core.Storage;

/// <param name="Action">The audit action of a move; null for a success that only re-mirrored (B8).</param>
internal sealed record StorageMigrationJobOutcome(
    string? Code = null,
    string? Details = null,
    int Roms = 0,
    long Bytes = 0,
    bool Skipped = false,
    string? Action = null);
