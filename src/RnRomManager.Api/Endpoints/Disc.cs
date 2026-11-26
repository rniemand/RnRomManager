namespace RnRomManager.Api.Endpoints;

/// <summary>One disc as streamed: a standalone image (no cue), or a playback cue and its aliased tracks.</summary>
internal sealed record Disc(PlayDiscEntry? Cue, IReadOnlyList<PlayDiscEntry> Entries);
