namespace RnRomManager.Core.Ingest;

/// <summary>
/// One track block of an <c>.mds</c> (offsets per libmirage's <c>image-mds.h</c>). <paramref name="Pregap"/> and
/// <paramref name="Length"/> come from the extra block, 0 when there is none.
/// </summary>
public sealed record MdsTrack(
    int Number,
    int Mode,
    int Subchannel,
    int SectorSize,
    long StartSector,
    ulong StartOffset,
    long Pregap,
    long Length);
