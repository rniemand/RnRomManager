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

    /// <summary>The library list: filters, sort (year nulls last), tie-break <c>id</c>, one page (spec §23, §14.2).</summary>
    Task<Page<GameListItem>> ListAsync(GameQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Filter counts (F1, F2): per system, region and letter, the games matching every filter of <paramref name="query"/>
    /// except that facet's own. Only values with at least one game; sort, paging and collections are ignored.
    /// </summary>
    Task<GameFacets> ListFacetsAsync(GameQuery query, CancellationToken cancellationToken);

    /// <summary>Counts and regions for every system that has games (spec §23 <c>/api/systems</c>).</summary>
    Task<IReadOnlyList<SystemStats>> ListSystemStatsAsync(CancellationToken cancellationToken);

    /// <summary>Games with the default box art, no <c>BoxArt</c> image or no <c>Snap</c>, ordered by title then id (§10.2).</summary>
    Task<IReadOnlyList<GameRow>> ListMediaRetryCandidatesAsync(CancellationToken cancellationToken);
}

/// <summary>Input paths the database treats as one <c>zip_path</c>, and the games already stored under it.</summary>
/// <param name="Paths">The input spellings, in input order; at least one.</param>
/// <param name="Existing">Rows whose <c>zip_path</c> equals them in the database (at most one today: the column is unique).</param>
public sealed record ZipPathEquivalenceGroup(IReadOnlyList<string> Paths, IReadOnlyList<ZipPathOwner> Existing);

/// <summary>A game and its stored <c>zip_path</c> spelling.</summary>
public sealed record ZipPathOwner(Guid GameId, string ZipPath);

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

    /// <summary>
    /// Every file stored outside its game's ZIP: each external main ROM (<c>external_file_name</c> set) and each
    /// <c>rom_files</c> row (B13), ordered by ZIP path, then ROM id, then the main file before its files in ordinal
    /// order. It lists what the mirror claims, whether or not the file is on disk: name allocation, integrity and
    /// orphan checks all start from these claims.
    /// </summary>
    Task<IReadOnlyList<ExternalRomRow>> ListExternalAsync(CancellationToken cancellationToken);

    /// <summary>B13: every set file of a game, by ROM id then ordinal.</summary>
    Task<IReadOnlyList<RomFileRow>> ListFilesByGameAsync(Guid gameId, CancellationToken cancellationToken);

    /// <summary>B13: null unless the file belongs to that release of that game.</summary>
    Task<RomFileRow?> GetFileAsync(Guid gameId, long romId, long fileId, CancellationToken cancellationToken);

    /// <summary>B14: every set file with this SHA1 (not unique: audio tracks repeat).</summary>
    Task<IReadOnlyList<RomFileRow>> ListFilesBySha1Async(string sha1, CancellationToken cancellationToken);
}

/// <summary>One mirrored claim on an external ROM file: <paramref name="ExternalFileName"/> in the folder of <paramref name="ZipPath"/>.</summary>
/// <param name="ZipPath">The owning game's ZIP, relative to <c>Paths:Roms</c>, <c>/</c> separators.</param>
/// <param name="SizeBytes">The recorded size, not the size on disk.</param>
/// <param name="FileId">B13: the <c>rom_files</c> id of a set file; null for a main file.</param>
public sealed record ExternalRomRow(Guid GameId, string ZipPath, string ExternalFileName, long SizeBytes, string Sha1, long? FileId = null);

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

/// <summary>Game counts per system id, region and letter bucket (F2); a multi-region game counts once per region.</summary>
public sealed record GameFacets(
    IReadOnlyDictionary<string, int> Systems, IReadOnlyDictionary<string, int> Regions, IReadOnlyDictionary<string, int> Letters);
