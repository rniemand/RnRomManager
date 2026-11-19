using RnRomManager.Core.Database;

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

    /// <summary>M3: as <see cref="NextMissingImageAsync"/>, in the unfiltered default order (display title, id).</summary>
    Task<MissingImageSuccessor> NextMissingMetadataAsync(Guid? afterGameId, CancellationToken cancellationToken);

    /// <summary>RP2: per system with at least one game, <c>SUM(games.zip_size_bytes)</c>, the game and update-release counts, largest first.</summary>
    Task<IReadOnlyList<SystemSize>> ListSystemSizesAsync(CancellationToken cancellationToken);

    /// <summary>RP2: every game of one system with its ZIP size, release and update-release counts, largest first (ties by title).</summary>
    Task<IReadOnlyList<GameSize>> ListGameSizesAsync(string systemId, CancellationToken cancellationToken);

    /// <summary>
    /// Multiple Releases B15/B17: games with at least <paramref name="min"/> releases (rows not flagged <c>Update</c>), with their update count
    /// and Library Size bytes. Only the primary key follows <paramref name="descending"/>; tie-breakers are ascending.
    /// </summary>
    Task<Page<MultipleReleaseItem>> ListMultipleReleasesAsync(int min, MultipleReleaseSort sort, bool descending, int page, int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Multiple Releases B16: the highest release count of any game, 0 for an empty library.</summary>
    Task<int> MaxReleaseCountAsync(CancellationToken cancellationToken);

    /// <summary>RP6–RP8: undismissed duplicate groups, sorted by system then base title; paging and total exclude dismissed ones.</summary>
    Task<Page<DuplicateGroup>> ListDuplicatesAsync(string? systemId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>UI polish B21: the newest <paramref name="limit"/> releases by <c>date_added</c> (ties by id), with the release total size.</summary>
    Task<IReadOnlyList<RecentRom>> ListRecentlyAddedRomsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>RP7: stores the dismissal; repeating it is a no-op.</summary>
    Task DismissDuplicateAsync(string systemId, string baseTitle, string gamesKey, int? userId, DateTime now,
        CancellationToken cancellationToken);
}

/// <param name="SizeBytes">The release total: <c>roms.size_bytes</c> plus its set files, as the game page's <c>totalSizeBytes</c>.</param>
public sealed record RecentRom(long RomId, Guid GameId, string GameTitle, string FileName, string System, long SizeBytes, DateTime AddedAt);

/// <param name="UpdateCount">B21: releases whose flags contain <c>Update</c>.</param>
public sealed record SystemSize(string SystemId, long Bytes, int GameCount, int UpdateCount);

/// <param name="UpdateCount">B21: releases whose flags contain <c>Update</c>.</param>
public sealed record GameSize(Guid Id, string DisplayTitle, long Bytes, int RomCount, int UpdateCount);

/// <param name="ReleaseCount">B15: releases whose flags don't contain <c>Update</c> (DLC counts).</param>
/// <param name="UpdateCount">B15: releases whose flags contain <c>Update</c>.</param>
/// <param name="Bytes">B15: the Library Size report's game bytes.</param>
public sealed record MultipleReleaseItem(Guid Id, string DisplayTitle, string SystemId, int ReleaseCount, int UpdateCount, long Bytes);

/// <summary>Multiple Releases B16/B17: the report's sort keys.</summary>
public enum MultipleReleaseSort { Name, System, Releases, Updates, Size }

/// <param name="Reason">A <see cref="MissingImageReasons"/> value.</param>
public sealed record MissingImageItem(Guid Id, string DisplayTitle, string SystemId, string Reason);

/// <param name="Developers">As stored; empty when unknown.</param>
public sealed record MissingMetadataItem(Guid Id, string DisplayTitle, string SystemId, int? ReleaseYear, IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers);

/// <summary>Missing Metadata M2: which missing field the report lists.</summary>
public enum MissingMetadataField { Any, Year, Developer, Publisher }

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

/// <summary>RP6: games in one system that own releases with the same base title. <c>Games</c> are in suggested-target order.</summary>
public sealed record DuplicateGroup(string SystemId, string BaseTitle, string GamesKey, IReadOnlyList<DuplicateGame> Games);

public sealed record DuplicateGame(Guid Id, string DisplayTitle, int RomCount, long ZipSizeBytes, DateTime CreatedAt, long? BoxArtImageId);
