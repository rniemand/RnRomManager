namespace RnRomManager.Core.Bundles;

/// <summary>A ticket's poll answer (B7): <see cref="ErrorCode"/> is set only for <see cref="BundleTicketState.Failed"/>.</summary>
public sealed record BundleTicketStatus(BundleTicketState State, string? ErrorCode);
