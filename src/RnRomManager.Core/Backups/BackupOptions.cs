namespace RnRomManager.Core.Backups;

/// <summary><c>Backup</c> section (B19). Only the startup restore lives in config; the schedule is in the DB (B15).</summary>
public sealed class BackupOptions
{
    public const string Section = "Backup";

    /// <summary>Bare file name of a backup in <c>Paths:Backups</c> to restore at startup; null/empty = none.</summary>
    public string? RestoreFrom { get; set; }
}
