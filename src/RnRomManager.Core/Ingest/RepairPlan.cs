using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Ingest;

/// <summary>I6: the file at a repaired ROM's pointer, and the reserved names a repair sets files aside under.</summary>
/// <param name="ExistingPath">The file at the pointer as spelled on disk; null when it is missing.</param>
/// <param name="BackupPath">Where that file waits until the ZIP is written (<c>.repair.tmp</c>).</param>
/// <param name="OrphanPath">An unlisted sibling for the incoming copy when its archive changed (I5).</param>
internal sealed record RepairPlan(
    string? ExistingPath,
    ExternalFileSnapshot? Snapshot,
    string BackupPath,
    string OrphanPath);
