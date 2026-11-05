using RnRomManager.Core.Database;

namespace RnRomManager.Core.Library;

public interface IGameRepository
{
    Task<GameRow?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<GameSyncState>> ListSyncStateAsync(CancellationToken cancellationToken);

    /// <summary>The library list: filters, sort (year nulls last), tie-break <c>id</c>, one page (spec §23, §14.2).</summary>
    Task<Page<GameListItem>> ListAsync(GameQuery query, CancellationToken cancellationToken);

    /// <summary>Counts and regions for every system that has games (spec §23 <c>/api/systems</c>).</summary>
    Task<IReadOnlyList<SystemStats>> ListSystemStatsAsync(CancellationToken cancellationToken);

    /// <summary>Games with the default box art, no <c>BoxArt</c> image or no <c>Snap</c>, ordered by title then id (§10.2).</summary>
    Task<IReadOnlyList<GameRow>> ListMediaRetryCandidatesAsync(CancellationToken cancellationToken);
}

public interface IRomRepository
{
    Task<IReadOnlyList<RomRow>> ListByGameAsync(Guid gameId, CancellationToken cancellationToken);

    /// <summary>null when the ROM doesn't exist or belongs to another game (ids change on every rewrite, §14.2).</summary>
    Task<RomRow?> GetAsync(Guid gameId, long id, CancellationToken cancellationToken);

    Task<RomRow?> GetBySha1Async(string sha1, CancellationToken cancellationToken);

    /// <summary>Games owning a ROM with this system + base title, case-insensitively (§7.2 step 6 grouping).</summary>
    Task<IReadOnlyList<Guid>> ListGameIdsByBaseTitleAsync(string systemId, string baseTitle, CancellationToken cancellationToken);

    /// <summary>Every ROM's (game, SHA1): the sync's <c>DuplicateSha1</c> check.</summary>
    Task<IReadOnlyList<(Guid GameId, string Sha1)>> ListSha1sAsync(CancellationToken cancellationToken);
}

public interface IImageRepository
{
    Task<IReadOnlyList<ImageRow>> ListByGameAsync(Guid gameId, CancellationToken cancellationToken);

    /// <summary>null when the image doesn't exist or belongs to another game.</summary>
    Task<ImageRow?> GetAsync(Guid gameId, long id, CancellationToken cancellationToken);
}

/// <summary>
/// The only writer of <c>games</c>/<c>roms</c>/<c>images</c> (spec §14.2 sync rule: ZIP first, then one transaction).
/// A replace deletes the game's rows and any row holding its <c>zip_path</c>, then inserts fresh ones from
/// <c>info.json</c>; the ZIP at a path is the truth.
/// </summary>
public interface ILibraryMirror
{
    /// <summary>All of it in one transaction. A duplicate <c>roms.sha1</c> throws and rolls back everything.</summary>
    Task ApplyAsync(IReadOnlyList<MirrorEntry> replace, IReadOnlyCollection<Guid> delete, CancellationToken cancellationToken);

    Task ReplaceAsync(MirrorEntry entry, CancellationToken cancellationToken) => ApplyAsync([entry], [], cancellationToken);

    Task DeleteAsync(Guid gameId, CancellationToken cancellationToken) => ApplyAsync([], [gameId], cancellationToken);
}
