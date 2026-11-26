namespace RnRomManager.Core.Reports;

/// <param name="SizeBytes">The release total: <c>roms.size_bytes</c> plus its set files, as the game page's <c>totalSizeBytes</c>.</param>
public sealed record RecentRom(
    long RomId,
    Guid GameId,
    string GameTitle,
    string FileName,
    string System,
    long SizeBytes,
    DateTime AddedAt);
