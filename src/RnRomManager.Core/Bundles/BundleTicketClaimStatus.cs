namespace RnRomManager.Core.Bundles;

public enum BundleTicketClaimStatus
{
    /// <summary>The ticket moved from pending to sending; the caller now streams it and must call Finish.</summary>
    Claimed,
    /// <summary>Unknown, evicted or another user's ticket (404).</summary>
    NotFound,
    /// <summary>Already claimed, finished or expired (409).</summary>
    Consumed,
}
