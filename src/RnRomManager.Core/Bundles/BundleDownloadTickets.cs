using System.Security.Cryptography;

namespace RnRomManager.Core.Bundles;

/// <summary>
/// Short-lived, random, caller-owned transfer tickets (Bundle Creator B7), in memory (one app instance). Each user holds
/// at most one pending or sending ticket, and at most <see cref="MaxActive"/> are pending or sending in all. A pending
/// ticket expires after <see cref="PendingLifetime"/>; a terminal one is remembered for <see cref="TerminalLifetime"/>,
/// at most <see cref="MaxTerminal"/> of them, the oldest evicted first. Expiry and eviction happen on every call.
/// Thread-safe.
/// </summary>
public sealed class BundleDownloadTickets(TimeProvider time)
{
    public const int MaxActive = 100;
    public const int MaxTerminal = 2000;
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TerminalLifetime = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Ticket> _activeByUser = [];
    private readonly Queue<(Ticket Ticket, DateTimeOffset At)> _terminal = new();

    /// <summary>Pending plus sending tickets, after expiry.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                Sweep();
                return _activeByUser.Count;
            }
        }
    }

    /// <summary>A new pending ticket for <paramref name="identity"/>, unless the user is busy or the global bound is reached.</summary>
    public BundleTicketIssue Issue(int userId, BundleFileIdentity identity)
    {
        lock (_lock)
        {
            Sweep();
            if (_activeByUser.ContainsKey(userId))
                return new BundleTicketIssue(BundleTicketIssueStatus.Busy);
            if (_activeByUser.Count >= MaxActive)
                return new BundleTicketIssue(BundleTicketIssueStatus.Full);

            string id;
            do id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            while (_tickets.ContainsKey(id));
            var ticket = new Ticket(id, userId, identity, time.GetUtcNow());
            _tickets[id] = ticket;
            _activeByUser[userId] = ticket;
            return new BundleTicketIssue(BundleTicketIssueStatus.Issued, id);
        }
    }

    /// <summary>Claims the owner's pending ticket once, atomically (pending → sending).</summary>
    public BundleTicketClaim Claim(string ticket, int userId)
    {
        lock (_lock)
        {
            Sweep();
            if (!_tickets.TryGetValue(ticket, out var found) || found.UserId != userId)
                return new BundleTicketClaim(BundleTicketClaimStatus.NotFound);
            if (found.State != BundleTicketState.Pending)
                return new BundleTicketClaim(BundleTicketClaimStatus.Consumed);
            found.State = BundleTicketState.Sending;
            return new BundleTicketClaim(BundleTicketClaimStatus.Claimed, found.Identity);
        }
    }

    /// <summary>
    /// Ends a sending ticket: <c>sent</c> when <paramref name="errorCode"/> is null, else <c>failed</c> with that code,
    /// and releases the owner's slot. Call it only after the stream is disposed. Any other state is left as it is.
    /// </summary>
    public void Finish(string ticket, string? errorCode)
    {
        lock (_lock)
        {
            if (_tickets.TryGetValue(ticket, out var found) && found.State == BundleTicketState.Sending)
                End(found, errorCode is null ? BundleTicketState.Sent : BundleTicketState.Failed, errorCode);
            Sweep();
        }
    }

    /// <summary>The owner's ticket status, or null when unknown, evicted or another user's.</summary>
    public BundleTicketStatus? Status(string ticket, int userId)
    {
        lock (_lock)
        {
            Sweep();
            return _tickets.TryGetValue(ticket, out var found) && found.UserId == userId
                ? new BundleTicketStatus(found.State, found.ErrorCode)
                : null;
        }
    }

    private void End(Ticket ticket, BundleTicketState state, string? errorCode)
    {
        ticket.State = state;
        ticket.ErrorCode = state == BundleTicketState.Failed ? errorCode : null;
        if (_activeByUser.TryGetValue(ticket.UserId, out var active) && ReferenceEquals(active, ticket))
            _activeByUser.Remove(ticket.UserId);
        _terminal.Enqueue((ticket, time.GetUtcNow()));
    }

    private void Sweep()
    {
        var now = time.GetUtcNow();
        // At most MaxActive entries, so a scan is cheap.
        foreach (var ticket in _activeByUser.Values.Where(t => t.State == BundleTicketState.Pending && now - t.IssuedAt >= PendingLifetime).ToList())
            End(ticket, BundleTicketState.Expired, null);
        while (_terminal.TryPeek(out var oldest) && (now - oldest.At >= TerminalLifetime || _terminal.Count > MaxTerminal))
        {
            _terminal.Dequeue();
            _tickets.Remove(oldest.Ticket.Id);
        }
    }
}
