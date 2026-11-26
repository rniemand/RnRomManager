namespace RnRomManager.Core.Backups;

/// <summary>B27: how a live restore ended; the endpoint maps each to its response.</summary>
public enum BackupRestoreStatus
{
    Restored,
    InvalidName,
    NotFound,
    JobRunning,
    BackupRunning,
    TooNew,
    InvalidBackup,
    Busy,
    BackupFailed,
    RestoreFailed,
}
