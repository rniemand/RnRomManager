using RnRomManager.Core.Database;

namespace RnRomManager.Core.Library;

public interface IGameRepository
{
    Task<GameRow?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<GameSyncState>> ListSyncStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Groups <paramref name="zipPaths"/> by equality under the collation of <c>games.zip_path</c>, as the database
    /// itself compares them (never a .NET approximation: two spellings can be equal there without matching
    /// ordinal-ignore-case, e.g. a composed and a decomposed accent), and lists the games whose stored path equals
    /// each group. Every input is in exactly one group, in input order; a path that fits no row is its own group.
    /// </summary>
    /// <param name="zipPaths">Relative to <c>Paths:Roms</c>, <c>/</c> separators; the spellings are returned unchanged.</param>
    Task<IReadOnlyList<ZipPathEquivalenceGroup>> ListZipPathEquivalenceAsync(IReadOnlyList<string> zipPaths, CancellationToken cancellationToken);

    /// <summary>Every <c>games</c> row (integrity: the state before its DB repair, §12.1).</summary>
    Task<IReadOnlyList<GameRow>> ListAllAsync(CancellationToken cancellationToken);

    /// <summary>S7: <paramref name="username"/>'s own save count per game (from <c>game_user_save_counts</c>); games without a row are absent.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetUserSaveCountsAsync(string username, IReadOnlyCollection<Guid> gameIds, CancellationToken cancellationToken);

    /// <summary>A6: the game's IGDB link changed: its <c>game_actions</c> rating, vote count and sync time become null.</summary>
    Task ClearIgdbSyncAsync(Guid gameId, CancellationToken cancellationToken);

    /// <summary>R2: every IGDB-linked game (<c>igdb_id &gt; 0</c>), by title then id.</summary>
    Task<IReadOnlyList<GameRow>> ListIgdbLinkedAsync(CancellationToken cancellationToken);

    /// <summary>R2: upserts the game's IGDB rating, vote count and sync time; never touches <c>cover_image_pulled_at</c>.</summary>
    Task SaveIgdbSyncAsync(
        Guid gameId,
        double? rating,
        int? count,
        DateTime syncedAt,
        CancellationToken cancellationToken);

    /// <summary>R4: the game's <c>game_actions</c> row, or null when it has none.</summary>
    Task<GameActionsRow?> GetActionsAsync(Guid gameId, CancellationToken cancellationToken);

    /// <summary>A5: <c>cover_image_pulled_at</c> (UTC) of every <c>game_actions</c> row that has one.</summary>
    Task<IReadOnlyDictionary<Guid, DateTime>> ListCoverPulledAsync(CancellationToken cancellationToken);

    /// <summary>T2: the Admin Actions tab's counts.</summary>
    Task<GameActionsSummary> GetActionsSummaryAsync(CancellationToken cancellationToken);

    /// <summary>A5: deletes the <c>game_actions</c> rows whose game is gone.</summary>
    /// <returns>How many were deleted.</returns>
    Task<int> DeleteOrphanActionsAsync(CancellationToken cancellationToken);

    /// <summary>The library list: filters, sort (year nulls last), tie-break <c>id</c>, one page (spec §23, §14.2).</summary>
    Task<Page<GameListItem>> ListAsync(GameQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Filter counts (F1, F2): per system, region and letter, the games matching every filter of <paramref name="query"/>
    /// except that facet's own. Only values with at least one game; sort, paging and collections are ignored.
    /// </summary>
    Task<GameFacets> ListFacetsAsync(GameQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Timeline T8: per release year, the games linked to IGDB (<c>igdb_id &gt; 0</c>) with a year, of one system or all;
    /// only years with games, ascending.
    /// </summary>
    Task<IReadOnlyList<(int Year, int Count)>> ListReleaseYearCountsAsync(string? systemId, CancellationToken cancellationToken);

    /// <summary>Counts and regions for every system that has games (spec §23 <c>/api/systems</c>).</summary>
    Task<IReadOnlyList<SystemStats>> ListSystemStatsAsync(CancellationToken cancellationToken);

    /// <summary>Games with the default box art, no <c>BoxArt</c> image or no <c>Snap</c>, ordered by title then id (§10.2).</summary>
    Task<IReadOnlyList<GameRow>> ListMediaRetryCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>P2: IGDB-linked games (<c>igdb_id &gt; 0</c>) without <c>cover_image_pulled_at</c>, by title then id.</summary>
    Task<IReadOnlyList<GameRow>> ListPullCoverCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// S4, ROM Zip Config B7: games whose ROM layout differs from their system's storage, by ZIP path: games of
    /// <paramref name="embedSystemIds"/> with any ROM beside the ZIP (<c>external_file_name</c> set), and of
    /// <paramref name="externalSystemIds"/> with any ROM inside it. Candidates only: the storage job re-opens each ZIP to decide.
    /// </summary>
    Task<IReadOnlyList<GameRow>> ListStorageMovesAsync(IReadOnlyCollection<string> embedSystemIds, IReadOnlyCollection<string> externalSystemIds,
        CancellationToken cancellationToken);
}
