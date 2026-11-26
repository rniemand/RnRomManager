namespace RnRomManager.Core.Reports;

/// <param name="HackCount">B13: releases whose flags contain <c>Hack</c>.</param>
/// <param name="ReleaseCount">B13: releases flagged neither <c>Update</c> nor <c>DLC</c>, as <see cref="MultipleReleaseItem"/>.</param>
/// <param name="Bytes">B13: the Library Size report's game bytes.</param>
public sealed record HackedGameItem(
    Guid Id,
    string DisplayTitle,
    string SystemId,
    int HackCount,
    int ReleaseCount,
    long Bytes);
