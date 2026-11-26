namespace RnRomManager.Core.Backups;

/// <summary>The outcome of the last run, scheduled or manual (B16); kept in memory only.</summary>
public sealed record BackupResult(
    DateTimeOffset At,
    bool Ok,
    string? FileName,
    string? Error);
