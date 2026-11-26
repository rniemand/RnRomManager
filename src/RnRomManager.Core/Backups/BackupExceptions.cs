namespace RnRomManager.Core.Backups;

/// <summary>Another backup is already running (B16, B17 <c>409 BackupRunning</c>).</summary>
public sealed class BackupRunningException() : Exception("A backup is already running.");
