namespace RnRomManager.Core.Library;

/// <summary>One row of the library list (spec §23 <c>/api/games</c>).</summary>
/// <param name="Regions">Distinct regions of every release, sorted ordinally.</param>
/// <param name="BoxArtImageId">The game's <c>BoxArt</c> image row, null when it has none (<c>MissingBoxArt</c>).</param>
public sealed record GameListItem(
    Guid Id, string SystemId, string DisplayTitle, int? ReleaseYear, IReadOnlyList<string> Regions, int RomCount, long? BoxArtImageId,
    int? Rating, bool Favourite);
