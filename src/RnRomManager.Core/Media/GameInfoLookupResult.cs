namespace RnRomManager.Core.Media;

/// <summary>The matches, or the failure title (the image suggestions' titles) when IGDB couldn't be used.</summary>
public sealed record GameInfoLookupResult(IReadOnlyList<GameInfoMatch>? Matches, string? FailedTitle);
