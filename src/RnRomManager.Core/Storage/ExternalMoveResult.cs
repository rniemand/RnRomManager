namespace RnRomManager.Core.Storage;

/// <param name="Path">The destination, for <see cref="ExternalMoveStatus.Moved"/>.</param>
/// <param name="Snapshot">The promoted file, for <see cref="ExternalMoveStatus.Moved"/>; pass it to every later check, undo or cleanup of that path.</param>
/// <param name="SurvivingPath">
/// For <see cref="ExternalMoveStatus.Failed"/>: where the complete, owned copy is now (the source when nothing
/// moved, a staging name when putting it back failed); null when it could not be found.
/// </param>
public sealed record ExternalMoveResult(
    ExternalMoveStatus Status,
    string? Path = null,
    ExternalFileSnapshot? Snapshot = null,
    string? Details = null,
    string? SurvivingPath = null);
