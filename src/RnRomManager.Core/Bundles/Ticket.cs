namespace RnRomManager.Core.Bundles;

internal sealed class Ticket(
    string id,
    int userId,
    BundleFileIdentity identity,
    DateTimeOffset issuedAt)
{
    public string Id { get; } = id;
    public int UserId { get; } = userId;
    public BundleFileIdentity Identity { get; } = identity;
    public DateTimeOffset IssuedAt { get; } = issuedAt;
    public BundleTicketState State { get; set; } = BundleTicketState.Pending;
    public string? ErrorCode { get; set; }
}
