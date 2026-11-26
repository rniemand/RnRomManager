namespace RnRomManager.Core.Ingest;

/// <summary>Play B2: the result of <see cref="CueSheet.ForPlayback"/>.</summary>
/// <param name="CueName">The playback cue's file name: the stored cue's base name with <c>.cue</c>.</param>
/// <param name="Content">The canonical cue (UTF-8, no BOM, LF).</param>
/// <param name="Tracks">Each stored track and its alias, in FILE order; every stored track exactly once.</param>
/// <param name="Error">Why the cue can't be played, or null.</param>
public sealed record PlaybackCue(
    string CueName,
    byte[] Content,
    IReadOnlyList<PlaybackTrack> Tracks,
    string? Error)
{
    /// <summary>The pinned parser reads lines into a 256-byte buffer: 254 bytes, the LF and the terminator.</summary>
    public const int MaxLineBytes = 254;

    public bool IsValid => Error is null;

    internal static PlaybackCue Fail(string error) => new("", [], [], error);
}
