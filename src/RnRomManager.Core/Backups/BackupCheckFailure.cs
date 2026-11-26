namespace RnRomManager.Core.Backups;

/// <summary>Why a backup can't be restored (B27.2): from a newer app, or not a usable backup ZIP.</summary>
public enum BackupCheckFailure
{
    TooNew,
    Invalid,
}
