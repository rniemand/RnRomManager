namespace RnRomManager.Core.Bundles;

/// <summary>
/// <c>bundle_queues</c> (B2, B4). Every call is scoped to the owner: another user's id acts as missing.
/// Callers validate names and entries first (<c>BundleQueueValidation</c>); the repository stores them as given.
/// </summary>
public interface IBundleQueueRepository
{
    /// <summary>The owner's queues sorted by name then id.</summary>
    Task<IReadOnlyList<BundleQueueSummary>> ListAsync(int userId, CancellationToken cancellationToken);

    /// <summary>The owner's queue, or null when missing or another user's.</summary>
    Task<BundleQueue?> GetAsync(Guid id, int userId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a queue with revision 1. Creates for one owner are serialized by locking the owner's <c>users</c> row;
    /// a name conflict is reported before the account cap. A missing owner is <see cref="BundleQueueWriteStatus.NotFound"/>.
    /// </summary>
    Task<BundleQueueWrite> CreateAsync(
        int userId,
        Guid id,
        string name,
        IReadOnlyList<BundleEntry> entries,
        DateTime now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces name and entries when the owner's queue is at <paramref name="revision"/>, incrementing it. Checks ownership,
    /// then revision, then the name.
    /// </summary>
    Task<BundleQueueWrite> ReplaceAsync(
        Guid id,
        int userId,
        string name,
        IReadOnlyList<BundleEntry> entries,
        int revision,
        DateTime now,
        CancellationToken cancellationToken);

    /// <summary>Deletes the owner's queue when it is at <paramref name="revision"/>; ownership is checked first.</summary>
    Task<BundleQueueWriteStatus> DeleteAsync(
        Guid id,
        int userId,
        int revision,
        CancellationToken cancellationToken);
}
