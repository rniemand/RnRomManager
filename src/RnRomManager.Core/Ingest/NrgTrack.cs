namespace RnRomManager.Core.Ingest;

/// <summary>One track of an <c>.nrg</c>'s DAO chunk; offsets are byte offsets into the image.</summary>
public sealed record NrgTrack(
    int Number,
    int Mode,
    int SectorSize,
    ulong Index0,
    ulong Index1,
    ulong End);
