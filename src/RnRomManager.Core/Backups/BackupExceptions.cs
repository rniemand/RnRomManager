namespace RnRomManager.Core.Backups;

/// <summary>Another backup is already running (B16, B17 <c>409 BackupRunning</c>).</summary>
public sealed class BackupRunningException() : Exception("A backup is already running.");

/// <summary>A backup run failed; nothing was kept (B17 <c>500 BackupFailed</c>).</summary>
public sealed class BackupFailedException(string message, Exception inner) : Exception(message, inner);
