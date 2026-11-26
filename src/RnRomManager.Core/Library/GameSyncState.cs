namespace RnRomManager.Core.Library;

/// <summary>What the sync compares to decide whether a ZIP must be opened (§12.1 quick mode).</summary>
/// <param name="SystemId">The mirrored system, which decides the storage mode whatever folder the ZIP is in.</param>
public sealed record GameSyncState(
    Guid Id,
    string ZipPath,
    long ZipSizeBytes,
    DateTime ZipMtimeUtc,
    string SystemId = "");
