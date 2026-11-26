namespace RnRomManager.Core.Reports;

/// <param name="ReleaseCount">B14: releases flagged neither <c>Update</c> nor <c>DLC</c>, as <see cref="HackedGameItem"/>.</param>
public sealed record NewGameItem(
    Guid Id,
    string DisplayTitle,
    string SystemId,
    DateTime AddedAt,
    int ReleaseCount);
