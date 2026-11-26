namespace RnRomManager.Core.Bundles;

public sealed record BundleTicketClaim(BundleTicketClaimStatus Status, BundleFileIdentity? Identity = null);
