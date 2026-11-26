namespace RnRomManager.Core.Backups;

/// <summary>A startup restore can't run or failed (B19); startup stops (Fatal, exit 1).</summary>
public sealed class BackupRestoreException : Exception
{
    public BackupRestoreException(string message) : base(message) { }

    public BackupRestoreException(string message, Exception inner) : base(message, inner) { }
}
