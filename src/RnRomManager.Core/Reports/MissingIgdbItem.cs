namespace RnRomManager.Core.Reports;

public sealed record MissingIgdbItem(
    Guid Id,
    string DisplayTitle,
    string SystemId,
    int? ReleaseYear);
