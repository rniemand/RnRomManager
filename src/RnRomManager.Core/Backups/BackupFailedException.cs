namespace RnRomManager.Core.Backups;

/// <summary>A backup run failed; nothing was kept (B17 <c>500 BackupFailed</c>).</summary>
public sealed class BackupFailedException(string message, Exception inner) : Exception(message, inner);
