namespace RnRomManager.Core.Bundles;

public enum BundleTicketIssueStatus
{
    Issued,
    /// <summary>The user already has a pending or sending ticket (409 <c>BundleDownloadBusy</c>).</summary>
    Busy,
    /// <summary><see cref="BundleDownloadTickets.MaxActive"/> tickets are pending or sending across all users (503).</summary>
    Full,
}
