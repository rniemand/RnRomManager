using RnRomManager.Core.Database;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Reports;

/// <summary>Read-only library reports (Reporting page, RP2–RP8).</summary>
public interface IReportRepository
{
    /// <summary>RP4: games with no cover image, or whose cover is the placeholder, sorted by system then title.</summary>
    Task<Page<MissingImageItem>> ListMissingImagesAsync(string? systemId, int page, int pageSize, CancellationToken cancellationToken);

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
    Task<Page<MissingMetadataItem>> ListMissingMetadataAsync(string? systemId, MissingMetadataField missing, string? q, MissingMetadataSort sort,
        bool descending, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Missing IGDB IDs R1: games not linked to IGDB (<see cref="MissingIgdbStatus.Missing"/>, <c>igdb_id IS NULL</c>) or marked Not on
    /// IGDB (<c>igdb_id = 0</c>), filtered and sorted as <see cref="ListMissingMetadataAsync"/>.
    /// </summary>
    Task<Page<MissingIgdbItem>> ListMissingIgdbIdsAsync(string? systemId, MissingIgdbStatus status, string? q, MissingMetadataSort sort,
        bool descending, int page, int pageSize, CancellationToken cancellationToken);

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
    Task<Page<MultipleReleaseItem>> ListMultipleReleasesAsync(string? systemId, int min, MultipleReleaseSort sort, bool descending, int page, int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Multiple Releases B16: the highest release count of any game (of <paramref name="systemId"/> when set, B12), 0 for none.</summary>
    Task<int> MaxReleaseCountAsync(string? systemId, CancellationToken cancellationToken);

    /// <summary>
    /// Release editing B13: games with at least one release flagged <c>Hack</c>, of <paramref name="systemId"/> when set. Only the primary
    /// key follows <paramref name="descending"/>; ties by display title then id ascending.
    /// </summary>
    Task<Page<HackedGameItem>> ListHackedGamesAsync(string? systemId, HackedGameSort sort, bool descending, int page, int pageSize,
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
    Task<Page<ImageResolutionItem>> ListImageResolutionsAsync(string? systemId, ImageType? type, int? maxWidth, int? maxHeight, int page,
        int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// RP6–RP8: undismissed duplicate groups, sorted by system then base title (D1: then IGDB id); paging and total exclude
    /// dismissed ones of the same <paramref name="match"/>.
    /// </summary>
    Task<Page<DuplicateGroup>> ListDuplicatesAsync(DuplicateMatch match, string? systemId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>UI polish B21: the newest <paramref name="limit"/> releases by <c>date_added</c> (ties by id), with the release total size.</summary>
    Task<IReadOnlyList<RecentRom>> ListRecentlyAddedRomsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>RP7: stores the dismissal; repeating it is a no-op. D2: an IGDB dismissal's <paramref name="baseTitle"/> is the IGDB id in decimal.</summary>
    Task DismissDuplicateAsync(DuplicateMatch match, string systemId, string baseTitle, string gamesKey, int? userId, DateTime now,
        CancellationToken cancellationToken);
}

/// <param name="SizeBytes">The release total: <c>roms.size_bytes</c> plus its set files, as the game page's <c>totalSizeBytes</c>.</param>
public sealed record RecentRom(long RomId, Guid GameId, string GameTitle, string FileName, string System, long SizeBytes, DateTime AddedAt);

/// <param name="UpdateCount">B21: releases whose flags contain <c>Update</c>.</param>
public sealed record SystemSize(string SystemId, long Bytes, int GameCount, int UpdateCount);

/// <param name="UpdateCount">B21: releases whose flags contain <c>Update</c>.</param>
public sealed record GameSize(Guid Id, string DisplayTitle, long Bytes, int RomCount, int UpdateCount);

/// <param name="ReleaseCount">B15: releases whose flags contain neither <c>Update</c> nor <c>DLC</c> (DLC B14).</param>
/// <param name="UpdateCount">B15: releases whose flags contain <c>Update</c>.</param>
/// <param name="Bytes">B15: the Library Size report's game bytes.</param>
public sealed record MultipleReleaseItem(Guid Id, string DisplayTitle, string SystemId, int ReleaseCount, int UpdateCount, long Bytes);

/// <param name="HackCount">B13: releases whose flags contain <c>Hack</c>.</param>
/// <param name="ReleaseCount">B13: releases flagged neither <c>Update</c> nor <c>DLC</c>, as <see cref="MultipleReleaseItem"/>.</param>
/// <param name="Bytes">B13: the Library Size report's game bytes.</param>
public sealed record HackedGameItem(Guid Id, string DisplayTitle, string SystemId, int HackCount, int ReleaseCount, long Bytes);

/// <param name="ReleaseCount">B14: releases flagged neither <c>Update</c> nor <c>DLC</c>, as <see cref="HackedGameItem"/>.</param>
public sealed record NewGameItem(Guid Id, string DisplayTitle, string SystemId, DateTime AddedAt, int ReleaseCount);

/// <summary>Release editing B13: the Games With Hacks sort keys.</summary>
public enum HackedGameSort { Name, System, Hacks, Releases, Size }

/// <summary>Multiple Releases B16/B17: the report's sort keys.</summary>
public enum MultipleReleaseSort { Name, System, Releases, Updates, Size }

/// <summary>Image Resolutions R4: one stored image with its pixel size.</summary>
public sealed record ImageResolutionItem(long ImageId, Guid GameId, string DisplayTitle, string SystemId, ImageType Type, int Width, int Height,
    long SizeBytes);

/// <param name="Reason">A <see cref="MissingImageReasons"/> value.</param>
public sealed record MissingImageItem(Guid Id, string DisplayTitle, string SystemId, string Reason);

/// <param name="Developers">As stored; empty when unknown.</param>
public sealed record MissingMetadataItem(Guid Id, string DisplayTitle, string SystemId, int? ReleaseYear, IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers);

/// <summary>Missing Metadata M2: which missing field the report lists.</summary>
public enum MissingMetadataField { Any, Year, Developer, Publisher }

public sealed record MissingIgdbItem(Guid Id, string DisplayTitle, string SystemId, int? ReleaseYear);

/// <summary>Missing IGDB IDs R1: which games the report lists.</summary>
public enum MissingIgdbStatus { Missing, NotOnIgdb }

/// <summary>Missing Metadata M2: the report's sort keys.</summary>
public enum MissingMetadataSort { Name, System, Year }

/// <param name="Id">The game to open, or null when no other game is missing images.</param>
/// <param name="CurrentIsOnly">True when <c>Id</c> is null because the anchor itself is the only game still missing images.</param>
public sealed record MissingImageSuccessor(Guid? Id, bool CurrentIsOnly);

public static class MissingImageReasons
{
    /// <summary>No image has <c>is_cover = 1</c>. Wins when both apply (only possible after DB drift).</summary>
    public const string NoCover = "NoCover";

    /// <summary><c>has_default_boxart = 1</c>: the cover is the placeholder art.</summary>
    public const string Placeholder = "Placeholder";
}

/// <summary>D1: how duplicate games are grouped: by release base title (RP6), or by a shared IGDB id.</summary>
public enum DuplicateMatch
{
    Title,
    Igdb,
}

/// <summary>
/// RP6: games in one system that own releases with the same base title, or (D1) share <see cref="IgdbId"/>, when
/// <see cref="BaseTitle"/> is the smallest display title. <c>Games</c> are in suggested-target order.
/// </summary>
public sealed record DuplicateGroup(string SystemId, string BaseTitle, string GamesKey, IReadOnlyList<DuplicateGame> Games, int? IgdbId = null);

public sealed record DuplicateGame(Guid Id, string DisplayTitle, int RomCount, long ZipSizeBytes, DateTime CreatedAt, long? BoxArtImageId);
