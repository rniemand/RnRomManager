namespace RnRomManager.Core.Library;

/// <param name="NotChecked">
/// Nothing at this path was opened or entered (an unsafe or unreadable path, or a ZIP path the database equates with
/// another, spec G4/E4); integrity reports it and keeps the path's issues, as no check of it ran.
/// </param>
public sealed record DbSyncSkipItem(
    DbSyncSkip Reason,
    string ZipPath,
    string? Details,
    bool NotChecked = false);
