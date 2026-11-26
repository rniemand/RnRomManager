namespace RnRomManager.Core.Ingest;

/// <summary>One <c>[TRACK n]</c> of a <c>.ccd</c>: its MODE (0 audio, 1, 2) and its INDEX 0 and INDEX 1 sectors.</summary>
public sealed record CloneCdTrack(
    int Number,
    int Mode,
    int? Index0,
    int Index1);
