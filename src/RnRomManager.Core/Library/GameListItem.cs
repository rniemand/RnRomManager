namespace RnRomManager.Core.Library;

/// <summary>One row of the library list (spec §23 <c>/api/games</c>).</summary>
/// <param name="Regions">Distinct regions of every release, sorted ordinally.</param>
/// <param name="BoxArtImageId">The game's cover image row (any type, I8), null when it has none (<c>MissingBoxArt</c>).</param>
/// <param name="MatchedOn">With a search: the metadata value that matched when no word was in a title (S3).</param>
public sealed record GameListItem(
    Guid Id, string SystemId, string DisplayTitle, int? ReleaseYear, IReadOnlyList<string> Regions, int RomCount, long? BoxArtImageId,
    int? Rating, bool Favourite, SearchMatch? MatchedOn = null);
