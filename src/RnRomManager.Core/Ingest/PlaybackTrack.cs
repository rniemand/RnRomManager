namespace RnRomManager.Core.Ingest;

/// <summary>One stored track and the short name it has inside a playback archive.</summary>
public sealed record PlaybackTrack(string StoredName, string Alias);
