namespace RnRomManager.Core.Reports;

/// <param name="UpdateCount">B21: releases whose flags contain <c>Update</c>.</param>
public sealed record GameSize(
    Guid Id,
    string DisplayTitle,
    long Bytes,
    int RomCount,
    int UpdateCount);
