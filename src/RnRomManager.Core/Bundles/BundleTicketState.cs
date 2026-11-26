namespace RnRomManager.Core.Bundles;

/// <summary>A transfer ticket's state (B7). <see cref="Pending"/> and <see cref="Sending"/> hold the owner's slot.</summary>
public enum BundleTicketState
{
    Pending,
    Sending,
    Sent,
    Failed,
    Expired,
}
