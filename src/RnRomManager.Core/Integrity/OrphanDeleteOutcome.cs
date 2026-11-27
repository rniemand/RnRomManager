namespace RnRomManager.Core.Integrity;

/// <summary>What <see cref="OrphanFileDeleter.DeleteAsync"/> did; anything but <c>Deleted</c> leaves the issue Open.</summary>
public enum OrphanDeleteOutcome
{
    Deleted,
    NotFound,
    NotOpenOrphan,
    JobRunning,
    NoLongerOrphan,
    NotDeletable,
    DeleteFailed,
}
