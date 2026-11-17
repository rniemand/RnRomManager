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

/// <summary>
/// The outcome of <see cref="BackupService.RestoreAsync"/>: the restored file and the safety backup taken first (set
/// from the safety backup on), and a message for <see cref="BackupRestoreStatus.TooNew"/>,
/// <see cref="BackupRestoreStatus.InvalidBackup"/> and <see cref="BackupRestoreStatus.RestoreFailed"/>.
/// </summary>
public sealed record BackupRestoreResult(BackupRestoreStatus Status, string? Restored = null, string? SafetyBackup = null, string? Message = null);
