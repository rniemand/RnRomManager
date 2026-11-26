namespace RnRomManager.TestData;

/// <summary>
/// One session-1 block of a synthetic <c>.mds</c>. A null <paramref name="Pregap"/> writes <c>extra_offset</c> 0; otherwise the
/// extra block holds the pregap and a length of 1000 sectors.
/// </summary>
public sealed record MdsBlock(
    int Point,
    int Mode = 0xAB,
    int Sub = 0,
    int SectorSize = 2352,
    ulong Offset = 0,
    uint? Pregap = 150);
