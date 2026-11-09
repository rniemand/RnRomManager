using RnRomManager.Core.Database;

namespace RnRomManager.Core.Collections;

/// <summary><c>collections</c> (B1–B7). Every read and write is scoped to the owner: another user's id acts as missing.</summary>
public interface ICollectionRepository
{
    /// <summary>Inserts the row (<see cref="Collection.GameCount"/> is ignored); a clashing name throws <see cref="CollectionNameTakenException"/>.</summary>
    Task InsertAsync(Collection collection, CancellationToken cancellationToken);

    /// <summary>The owner's collection with its game count, or null when missing or another user's.</summary>
    Task<Collection?> GetAsync(Guid id, int userId, CancellationToken cancellationToken);

    /// <summary>
    /// The owner's collections whose name contains every word (case/accent-insensitive, <c>%</c>/<c>_</c> literal),
    /// sorted by name then id.
    /// </summary>
    Task<Page<Collection>> ListAsync(int userId, IReadOnlyList<string> words, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Writes name, notes, cover columns and <c>updated_at</c> of the owner's row; false when missing or another user's.
    /// A clashing name throws <see cref="CollectionNameTakenException"/>.
    /// </summary>
    Task<bool> UpdateAsync(Collection collection, CancellationToken cancellationToken);

    /// <summary>Deletes the owner's row and (by cascade) its memberships; false when missing or another user's.</summary>
    Task<bool> DeleteAsync(Guid id, int userId, CancellationToken cancellationToken);
}
