namespace RnRomManager.Core.Backups;

/// <summary>Another backup is already running (B16, B17 <c>409 BackupRunning</c>).</summary>
public sealed class BackupRunningException() : Exception("A backup is already running.");

/// <summary>A backup run failed; nothing was kept (B17 <c>500 BackupFailed</c>).</summary>
public sealed class BackupFailedException(string message, Exception inner) : Exception(message, inner);

/// <summary>A startup restore can't run or failed (B19); startup stops (Fatal, exit 1).</summary>
public sealed class BackupRestoreException : Exception
{
    public BackupRestoreException(string message) : base(message) { }

    public BackupRestoreException(string message, Exception inner) : base(message, inner) { }
}
