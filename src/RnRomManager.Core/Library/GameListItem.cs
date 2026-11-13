namespace RnRomManager.Core.Library;

/// <summary>One row of the library list (spec §23 <c>/api/games</c>).</summary>
/// <param name="Regions">Distinct regions of every release, sorted ordinally.</param>
/// <param name="BoxArtImageId">The game's cover image row (any type, I8), null when it has none (<c>MissingBoxArt</c>).</param>
/// <param name="PrimarySizeBytes">ROM size of the game's primary release (LP1) including its set files (B21), null when it has none.</param>
/// <param name="MatchedOn">With a search: the metadata value that matched when no word was in a title (S3).</param>
/// <param name="UpdateCount">Roms whose flags contain <c>Update</c> (C1).</param>
/// <param name="PrimaryRomId">The primary release (lowest id if several), null when none; with its file name and external flag for quick play (C2).</param>
public sealed record GameListItem(
    Guid Id, string SystemId, string DisplayTitle, int? ReleaseYear, IReadOnlyList<string> Regions, int RomCount, long? BoxArtImageId,
    int? Rating, bool Favourite, long? PrimarySizeBytes, SearchMatch? MatchedOn = null,
    int UpdateCount = 0, long? PrimaryRomId = null, string? PrimaryRomFileName = null, bool PrimaryIsExternal = false);
