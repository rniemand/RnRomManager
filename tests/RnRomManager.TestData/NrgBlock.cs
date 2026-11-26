namespace RnRomManager.TestData;

/// <summary>One track block of a synthetic <c>.nrg</c> DAO chunk; offsets are byte offsets into the image.</summary>
public sealed record NrgBlock(
    ulong Index0,
    ulong Index1,
    ulong End,
    int Mode = 0x07,
    int SectorSize = 2352);
