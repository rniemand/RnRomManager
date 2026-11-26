namespace RnRomManager.Core.Storage;

public enum ExternalMoveStatus
{
    /// <summary>The file is at the destination; <see cref="ExternalMoveResult.Snapshot"/> describes it there.</summary>
    Moved,

    /// <summary>
    /// The file at the source is not the expected one (replaced, changed, or gone). It was left, or put back, at
    /// the source; nothing reached the destination.
    /// </summary>
    SourceChanged,

    /// <summary>An <c>IoError</c>: see <see cref="ExternalMoveResult.Details"/> and <see cref="ExternalMoveResult.SurvivingPath"/>.</summary>
    Failed,
}
