namespace RnRomManager.Core.Reports;

/// <param name="ReleaseCount">B15: releases whose flags contain neither <c>Update</c> nor <c>DLC</c> (DLC B14).</param>
/// <param name="UpdateCount">B15: releases whose flags contain <c>Update</c>.</param>
/// <param name="Bytes">B15: the Library Size report's game bytes.</param>
public sealed record MultipleReleaseItem(
    Guid Id,
    string DisplayTitle,
    string SystemId,
    int ReleaseCount,
    int UpdateCount,
    long Bytes);
