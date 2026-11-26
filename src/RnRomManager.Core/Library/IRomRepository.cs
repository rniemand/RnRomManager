using RnRomManager.Core.Bundles;

namespace RnRomManager.Core.Library;

public interface IRomRepository
{
    Task<IReadOnlyList<RomRow>> ListByGameAsync(Guid gameId, CancellationToken cancellationToken);

    /// <summary>null when the ROM doesn't exist or belongs to another game (ids change on every rewrite, §14.2).</summary>
    Task<RomRow?> GetAsync(Guid gameId, long id, CancellationToken cancellationToken);

    Task<RomRow?> GetBySha1Async(string sha1, CancellationToken cancellationToken);

    /// <summary>Games owning a ROM with this system + base title, case-insensitively (§7.2 step 6 grouping).</summary>
    Task<IReadOnlyList<Guid>> ListGameIdsByBaseTitleAsync(string systemId, string baseTitle, CancellationToken cancellationToken);

    /// <summary>
    /// Arcade P2: the system's ROMs whose header <c>setName</c> is one of <paramref name="setNames"/> (compared lower-cased),
    /// so only DAT-matched releases; per set, the primary release first, then the lowest id.
    /// </summary>
    Task<IReadOnlyList<RomRow>> ListBySetNamesAsync(string systemId, IReadOnlyCollection<string> setNames, CancellationToken cancellationToken);

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
    Task<RomFileRow?> GetFileAsync(
        Guid gameId,
        long romId,
        long fileId,
        CancellationToken cancellationToken);

    /// <summary>B14: every set file with this SHA1 (not unique: audio tracks repeat).</summary>
    Task<IReadOnlyList<RomFileRow>> ListFilesBySha1Async(string sha1, CancellationToken cancellationToken);

    /// <summary>
    /// Bundle Creator B3/B5: each identity's current download target, in input order, or null when it can't be resolved
    /// uniquely (the release is gone or belongs to another game; the set file is missing, renamed or ambiguous). Set-file
    /// names compare ordinally. Callers validate identities first and bound the batch to <see cref="BundleFile.MaxBatch"/>.
    /// </summary>
    Task<IReadOnlyList<BundleFileTarget?>> ResolveBundleFilesAsync(IReadOnlyList<BundleFileIdentity> identities, CancellationToken cancellationToken);
}
