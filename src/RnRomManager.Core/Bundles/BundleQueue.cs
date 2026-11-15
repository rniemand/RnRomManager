namespace RnRomManager.Core.Bundles;

/// <summary>
/// One file in a cart or saved queue (B3): the stable identity (<see cref="GameId"/>, <see cref="ReleaseSha1"/> and,
/// for a set file, <see cref="Member"/>) plus its last-known labels. A main-file entry has a null <see cref="Member"/>.
/// </summary>
public sealed record BundleEntry(Guid GameId, string ReleaseSha1, BundleMember? Member, string GameTitle, string FileName);

/// <summary>A separately downloadable set file within a release: its SHA-1 and stored <c>rom_files.file_name</c> (B3).</summary>
public sealed record BundleMember(string Sha1, string FileName);

/// <summary>A saved queue's full snapshot (B4: <c>{ id, name, revision, entries }</c>).</summary>
public sealed record BundleQueue(Guid Id, string Name, int Revision, IReadOnlyList<BundleEntry> Entries);

/// <summary>A saved queue in the list (B4: <c>{ id, name, revision, itemCount }</c>).</summary>
public sealed record BundleQueueSummary(Guid Id, string Name, int Revision, int ItemCount);

/// <summary>The limits from B2.</summary>
public static class BundleQueueLimits
{
    public const int MaxQueuesPerUser = 100;
    public const int MaxEntries = 500;
    public const int MaxNameLength = 100;
}

/// <summary>The outcome of a queue write (B4). <see cref="Queue"/> is set only for <see cref="BundleQueueWriteStatus.Ok"/>.</summary>
public sealed record BundleQueueWrite(BundleQueueWriteStatus Status, BundleQueue? Queue = null)
{
    public static readonly BundleQueueWrite NotFound = new(BundleQueueWriteStatus.NotFound);
    public static readonly BundleQueueWrite Changed = new(BundleQueueWriteStatus.Changed);
    public static readonly BundleQueueWrite NameTaken = new(BundleQueueWriteStatus.NameTaken);
    public static readonly BundleQueueWrite Limit = new(BundleQueueWriteStatus.Limit);
}

public enum BundleQueueWriteStatus
{
    Ok,
    /// <summary>Missing or another user's queue (404).</summary>
    NotFound,
    /// <summary>Stale revision (409 <c>BundleQueueChanged</c>).</summary>
    Changed,
    /// <summary>Another of the owner's queues has the name (409 <c>BundleQueueNameTaken</c>).</summary>
    NameTaken,
    /// <summary>The owner already has <see cref="BundleQueueLimits.MaxQueuesPerUser"/> queues (409 <c>BundleQueueLimit</c>).</summary>
    Limit,
}

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
    Task<BundleQueueWrite> CreateAsync(int userId, Guid id, string name, IReadOnlyList<BundleEntry> entries, DateTime now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces name and entries when the owner's queue is at <paramref name="revision"/>, incrementing it. Checks ownership,
    /// then revision, then the name.
    /// </summary>
    Task<BundleQueueWrite> ReplaceAsync(Guid id, int userId, string name, IReadOnlyList<BundleEntry> entries, int revision,
        DateTime now, CancellationToken cancellationToken);

    /// <summary>Deletes the owner's queue when it is at <paramref name="revision"/>; ownership is checked first.</summary>
    Task<BundleQueueWriteStatus> DeleteAsync(Guid id, int userId, int revision, CancellationToken cancellationToken);
}
