namespace RnRomManager.Core.Reports;

/// <param name="Reason">A <see cref="MissingImageReasons"/> value.</param>
public sealed record MissingImageItem(
    Guid Id,
    string DisplayTitle,
    string SystemId,
    string Reason);
