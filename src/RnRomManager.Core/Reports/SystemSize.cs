namespace RnRomManager.Core.Reports;

/// <param name="UpdateCount">B21: releases whose flags contain <c>Update</c>.</param>
public sealed record SystemSize(
    string SystemId,
    long Bytes,
    int GameCount,
    int UpdateCount);
