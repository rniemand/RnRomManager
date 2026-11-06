using RnRomManager.Core.Database;

namespace RnRomManager.Core.Reports;

/// <summary>Read-only library reports (Reporting page, RP2–RP8).</summary>
public interface IReportRepository
{
    /// <summary>RP4: games with no cover image, or whose cover is the placeholder, sorted by system then title.</summary>
    Task<Page<MissingImageItem>> ListMissingImagesAsync(string? systemId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>RP2: per system with at least one game, <c>SUM(games.zip_size_bytes)</c> and the game count, largest first.</summary>
    Task<IReadOnlyList<SystemSize>> ListSystemSizesAsync(CancellationToken cancellationToken);

    /// <summary>RP2: every game of one system with its ZIP size and release count, largest first (ties by title).</summary>
    Task<IReadOnlyList<GameSize>> ListGameSizesAsync(string systemId, CancellationToken cancellationToken);

    /// <summary>RP6–RP8: undismissed duplicate groups, sorted by system then base title; paging and total exclude dismissed ones.</summary>
    Task<Page<DuplicateGroup>> ListDuplicatesAsync(string? systemId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>RP7: stores the dismissal; repeating it is a no-op.</summary>
    Task DismissDuplicateAsync(string systemId, string baseTitle, string gamesKey, int? userId, DateTime now,
        CancellationToken cancellationToken);
}

public sealed record SystemSize(string SystemId, long Bytes, int GameCount);

public sealed record GameSize(Guid Id, string DisplayTitle, long Bytes, int RomCount);

/// <param name="Reason">A <see cref="MissingImageReasons"/> value.</param>
public sealed record MissingImageItem(Guid Id, string DisplayTitle, string SystemId, string Reason);

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
