using RnRomManager.Core.Database;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Reports;

/// <summary>Read-only library reports (Reporting page, RP2–RP8).</summary>
public interface IReportRepository
{
    /// <summary>RP4: games with no cover image, or whose cover is the placeholder, sorted by system then title.</summary>
    Task<Page<MissingImageItem>> ListMissingImagesAsync(
        string? systemId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// The missing-image report row after <paramref name="afterGameId"/> in the unfiltered report order (system, title, id),
    /// wrapping to the first row other than the anchor. An absent or deleted anchor starts at the first row; an anchor that is no
    /// longer missing images still keeps its sort position.
    /// </summary>
    Task<MissingImageSuccessor> NextMissingImageAsync(Guid? afterGameId, CancellationToken cancellationToken);

    /// <summary>
    /// Missing Metadata M1–M2: games with no year, no developer or no publisher, filtered by <paramref name="missing"/>, system and a
    /// display-title search. Only the primary key follows <paramref name="descending"/>; tie-breakers are ascending; a null year sorts last.
    /// </summary>
    Task<Page<MissingMetadataItem>> ListMissingMetadataAsync(
        string? systemId,
        MissingMetadataField missing,
        string? q,
        MissingMetadataSort sort,
        bool descending,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Missing IGDB IDs R1: games not linked to IGDB (<see cref="MissingIgdbStatus.Missing"/>, <c>igdb_id IS NULL</c>) or marked Not on
    /// IGDB (<c>igdb_id = 0</c>), filtered and sorted as <see cref="ListMissingMetadataAsync"/>.
    /// </summary>
    Task<Page<MissingIgdbItem>> ListMissingIgdbIdsAsync(
        string? systemId,
        MissingIgdbStatus status,
        string? q,
        MissingMetadataSort sort,
        bool descending,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>M3: as <see cref="NextMissingImageAsync"/>, in the unfiltered default order (display title, id).</summary>
    Task<MissingImageSuccessor> NextMissingMetadataAsync(Guid? afterGameId, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="NextMissingImageAsync"/>, for games not linked to IGDB (<c>igdb_id IS NULL</c>; Not on IGDB is skipped) in the
    /// Missing IGDB IDs report's unfiltered default order (display title, id).
    /// </summary>
    Task<MissingImageSuccessor> NextMissingIgdbIdAsync(Guid? afterGameId, CancellationToken cancellationToken);

    /// <summary>RP2: per system with at least one game, <c>SUM(games.zip_size_bytes)</c>, the game and update-release counts, largest first.</summary>
    Task<IReadOnlyList<SystemSize>> ListSystemSizesAsync(CancellationToken cancellationToken);

    /// <summary>RP2: every game of one system with its ZIP size, release and update-release counts, largest first (ties by title).</summary>
    Task<IReadOnlyList<GameSize>> ListGameSizesAsync(string systemId, CancellationToken cancellationToken);

    /// <summary>
    /// Multiple Releases B15/B17: games with at least <paramref name="min"/> releases (rows flagged neither <c>Update</c> nor <c>DLC</c>, DLC B14), with their update count
    /// and Library Size bytes. Only the primary key follows <paramref name="descending"/>; tie-breakers are ascending.
    /// </summary>
    /// <param name="systemId">Release editing B12: only that system's games when set.</param>
    Task<Page<MultipleReleaseItem>> ListMultipleReleasesAsync(
        string? systemId,
        int min,
        MultipleReleaseSort sort,
        bool descending,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Multiple Releases B16: the highest release count of any game (of <paramref name="systemId"/> when set, B12), 0 for none.</summary>
    Task<int> MaxReleaseCountAsync(string? systemId, CancellationToken cancellationToken);

    /// <summary>
    /// Release editing B13: games with at least one release flagged <c>Hack</c>, of <paramref name="systemId"/> when set. Only the primary
    /// key follows <paramref name="descending"/>; ties by display title then id ascending.
    /// </summary>
    Task<Page<HackedGameItem>> ListHackedGamesAsync(
        string? systemId,
        HackedGameSort sort,
        bool descending,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Release editing B14: per system (or only <paramref name="systemId"/>), its <paramref name="perSystem"/> newest games by
    /// <c>created_at</c> then id, both descending; rows ordered by system id, then that order.
    /// </summary>
    Task<IReadOnlyList<NewGameItem>> ListNewlyAddedGamesAsync(string? systemId, int perSystem, CancellationToken cancellationToken);

    /// <summary>
    /// Image Resolutions R3: every non-placeholder image of a game, of <paramref name="systemId"/> and <paramref name="type"/> when set,
    /// listed when <c>width &lt;= maxWidth</c> or <c>height &lt;= maxHeight</c> (an unset limit is ignored; none set lists all), ordered
    /// by area, then system id, display title and image id.
    /// </summary>
    Task<Page<ImageResolutionItem>> ListImageResolutionsAsync(
        string? systemId,
        ImageType? type,
        int? maxWidth,
        int? maxHeight,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// RP6–RP8: undismissed duplicate groups, sorted by system then base title (D1: then IGDB id); paging and total exclude
    /// dismissed ones of the same <paramref name="match"/>.
    /// </summary>
    Task<Page<DuplicateGroup>> ListDuplicatesAsync(
        DuplicateMatch match,
        string? systemId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>UI polish B21: the newest <paramref name="limit"/> releases by <c>date_added</c> (ties by id), with the release total size.</summary>
    Task<IReadOnlyList<RecentRom>> ListRecentlyAddedRomsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>RP7: stores the dismissal; repeating it is a no-op. D2: an IGDB dismissal's <paramref name="baseTitle"/> is the IGDB id in decimal.</summary>
    Task DismissDuplicateAsync(
        DuplicateMatch match,
        string systemId,
        string baseTitle,
        string gamesKey,
        int? userId,
        DateTime now,
        CancellationToken cancellationToken);
}
