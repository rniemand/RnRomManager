namespace RnRomManager.Core.Ingest;

internal sealed class TrackState(int number, string mode, string? subchannel)
{
    public readonly int Number = number;
    public readonly List<TocItem> Items = [];
    public int? Pregap;
    public int? Start;
    public bool HasSilence;

    /// <summary>The summed length of the track's items so far, or null once one has no length (for a bare START).</summary>
    public int? Known = 0;

    public TocTrack Build() => new(Number, mode, subchannel, Pregap, Start, HasSilence, Items);
}
