namespace RnRomManager.Core.Bundles;

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
