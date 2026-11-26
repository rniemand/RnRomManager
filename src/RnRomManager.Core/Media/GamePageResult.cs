namespace RnRomManager.Core.Media;

/// <summary>
/// Timeline T7: the game's IGDB page (<c>https://www.igdb.com/…</c>); both null when IGDB has no such game or page, or
/// <see cref="FailedTitle"/> when IGDB couldn't be used.
/// </summary>
public sealed record GamePageResult(string? Url, string? FailedTitle);
