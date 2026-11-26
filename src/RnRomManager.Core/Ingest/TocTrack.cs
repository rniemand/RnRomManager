namespace RnRomManager.Core.Ingest;

/// <summary>One <c>TRACK</c> of a <c>.toc</c>: its mode and subchannel tokens (upper-cased), PREGAP and START in sectors, and its data items.</summary>
public sealed record TocTrack(
    int Number,
    string Mode,
    string? Subchannel,
    int? Pregap,
    int? Start,
    bool HasSilence,
    IReadOnlyList<TocItem> Items);
