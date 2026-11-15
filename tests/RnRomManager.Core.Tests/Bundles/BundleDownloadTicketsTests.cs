using Microsoft.Extensions.Time.Testing;
using RnRomManager.Core.Bundles;

namespace RnRomManager.Core.Tests.Bundles;

/// <summary>Bundle Creator B7: ticket ownership, single claim, bounds and lifetimes on a fake clock.</summary>
public class BundleDownloadTicketsTests
{
    private static readonly BundleFileIdentity File = new(Guid.Parse("8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f"), new string('a', 40), null);

    private FakeTimeProvider _clock = null!;
    private BundleDownloadTickets _tickets = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        _tickets = new BundleDownloadTickets(_clock);
    }

    private string Issue(int user, BundleFileIdentity? identity = null)
    {
        var issue = _tickets.Issue(user, identity ?? File);
        Assert.That(issue.Status, Is.EqualTo(BundleTicketIssueStatus.Issued));
        return issue.Ticket!;
    }

    private BundleTicketState? State(string ticket, int user) => _tickets.Status(ticket, user)?.State;

    [Test]
    public void Tickets_AreRandom_UrlSafe_AndStartPending()
    {
        var a = Issue(1);
        var b = Issue(2);

        Assert.That(a, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(_tickets.Status(a, 1), Is.EqualTo(new BundleTicketStatus(BundleTicketState.Pending, null)));
    }

    [Test]
    public void AClaim_HappensOnce_AndCarriesTheIdentity()
    {
        var member = File with { Member = new BundleMember(new string('b', 40), "Track 2.bin") };
        var ticket = Issue(1, member);

        var first = _tickets.Claim(ticket, 1);
        var second = _tickets.Claim(ticket, 1);

        Assert.That(first, Is.EqualTo(new BundleTicketClaim(BundleTicketClaimStatus.Claimed, member)));
        Assert.That(second.Status, Is.EqualTo(BundleTicketClaimStatus.Consumed));
        Assert.That(State(ticket, 1), Is.EqualTo(BundleTicketState.Sending));
    }

    [Test]
    public void ConcurrentClaims_OnlyOneWins()
    {
        var ticket = Issue(1);

        var claims = new BundleTicketClaim[32];
        Parallel.For(0, claims.Length, i => claims[i] = _tickets.Claim(ticket, 1));

        Assert.That(claims.Count(c => c.Status == BundleTicketClaimStatus.Claimed), Is.EqualTo(1));
        Assert.That(claims.Count(c => c.Status == BundleTicketClaimStatus.Consumed), Is.EqualTo(31));
    }

    [Test]
    public void AnotherUsersOrAnUnknownTicket_IsNotFound()
    {
        var ticket = Issue(1);

        Assert.That(_tickets.Claim(ticket, 2).Status, Is.EqualTo(BundleTicketClaimStatus.NotFound));
        Assert.That(_tickets.Status(ticket, 2), Is.Null);
        Assert.That(_tickets.Claim(new string('0', 64), 1).Status, Is.EqualTo(BundleTicketClaimStatus.NotFound));
        Assert.That(State(ticket, 1), Is.EqualTo(BundleTicketState.Pending), "the foreign claim changed nothing");
    }

    [Test]
    public void AUser_HoldsOneSlot_UntilTheTicketEnds_WhileOthersProceed()
    {
        var ticket = Issue(1);
        Issue(2);

        Assert.That(_tickets.Issue(1, File).Status, Is.EqualTo(BundleTicketIssueStatus.Busy), "pending");
        _tickets.Claim(ticket, 1);
        Assert.That(_tickets.Issue(1, File).Status, Is.EqualTo(BundleTicketIssueStatus.Busy), "sending");

        _tickets.Finish(ticket, null);

        Assert.That(_tickets.Status(ticket, 1), Is.EqualTo(new BundleTicketStatus(BundleTicketState.Sent, null)));
        Issue(1);
    }

    [Test]
    public void Finish_WithACode_IsFailed_AndOnlyEndsASendingTicket()
    {
        var pending = Issue(1);
        _tickets.Finish(pending, "Aborted");
        Assert.That(State(pending, 1), Is.EqualTo(BundleTicketState.Pending), "never claimed");

        _tickets.Claim(pending, 1);
        _tickets.Finish(pending, "Aborted");
        _tickets.Finish(pending, null);

        Assert.That(_tickets.Status(pending, 1), Is.EqualTo(new BundleTicketStatus(BundleTicketState.Failed, "Aborted")), "the first end wins");
        Assert.That(_tickets.Claim(pending, 1).Status, Is.EqualTo(BundleTicketClaimStatus.Consumed));
    }

    [Test]
    public void TheGlobalBound_Is100PendingOrSending()
    {
        var tickets = Enumerable.Range(1, BundleDownloadTickets.MaxActive).Select(u => Issue(u)).ToList();

        Assert.That(_tickets.Issue(1000, File).Status, Is.EqualTo(BundleTicketIssueStatus.Full));
        Assert.That(_tickets.Issue(1, File).Status, Is.EqualTo(BundleTicketIssueStatus.Busy), "busy before full");
        _tickets.Claim(tickets[0], 1);
        Assert.That(_tickets.Issue(1000, File).Status, Is.EqualTo(BundleTicketIssueStatus.Full), "sending still counts");

        _tickets.Finish(tickets[0], null);

        Issue(1000);
        Assert.That(_tickets.ActiveCount, Is.EqualTo(BundleDownloadTickets.MaxActive));
    }

    [Test]
    public void APendingTicket_ExpiresAfter30Seconds_AndReleasesTheSlot()
    {
        var ticket = Issue(1);

        _clock.Advance(TimeSpan.FromSeconds(29.9));
        Assert.That(State(ticket, 1), Is.EqualTo(BundleTicketState.Pending));
        Assert.That(_tickets.Issue(1, File).Status, Is.EqualTo(BundleTicketIssueStatus.Busy));

        _clock.Advance(TimeSpan.FromSeconds(0.1));

        Assert.That(_tickets.Status(ticket, 1), Is.EqualTo(new BundleTicketStatus(BundleTicketState.Expired, null)));
        Assert.That(_tickets.Claim(ticket, 1).Status, Is.EqualTo(BundleTicketClaimStatus.Consumed));
        Issue(1);
    }

    [Test]
    public void ASendingTicket_NeverExpires_HoweverLong()
    {
        var ticket = Issue(1);
        _tickets.Claim(ticket, 1);

        _clock.Advance(TimeSpan.FromHours(2));

        Assert.That(State(ticket, 1), Is.EqualTo(BundleTicketState.Sending));
        Assert.That(_tickets.Issue(1, File).Status, Is.EqualTo(BundleTicketIssueStatus.Busy));
    }

    [Test]
    public void TerminalStatus_IsKeptForFiveMinutes()
    {
        var ticket = Issue(1);
        _tickets.Claim(ticket, 1);
        _tickets.Finish(ticket, null);

        _clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.That(State(ticket, 1), Is.EqualTo(BundleTicketState.Sent));

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.That(_tickets.Status(ticket, 1), Is.Null);
        Assert.That(_tickets.Claim(ticket, 1).Status, Is.EqualTo(BundleTicketClaimStatus.NotFound));
    }

    [Test]
    public void AnExpiredTicket_IsKeptForFiveMinutesFromItsExpiry()
    {
        var ticket = Issue(1);
        _clock.Advance(BundleDownloadTickets.PendingLifetime);
        Assert.That(State(ticket, 1), Is.EqualTo(BundleTicketState.Expired));

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.That(State(ticket, 1), Is.EqualTo(BundleTicketState.Expired));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(State(ticket, 1), Is.Null);
    }

    [Test]
    public void AtMost2000TerminalTickets_AreKept_OldestEvictedFirst()
    {
        var ended = new List<string>();
        for (var i = 0; i < BundleDownloadTickets.MaxTerminal + 5; i++)
        {
            var ticket = Issue(1);
            _tickets.Claim(ticket, 1);
            _tickets.Finish(ticket, i % 2 == 0 ? null : "OpenFailed");
            ended.Add(ticket);
            _clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.That(ended.Take(5).Select(t => _tickets.Status(t, 1)), Is.All.Null);
        Assert.That(ended.Skip(5).Select(t => State(t, 1)), Has.None.Null);
        Assert.That(_tickets.Status(ended[^1], 1), Is.EqualTo(new BundleTicketStatus(BundleTicketState.Sent, null)));
        Assert.That(_tickets.Status(ended[^2], 1), Is.EqualTo(new BundleTicketStatus(BundleTicketState.Failed, "OpenFailed")));
    }

    [Test]
    public void ActiveTickets_AreNeverEvicted_ByTheTerminalCap()
    {
        var active = Issue(99);
        for (var i = 0; i < BundleDownloadTickets.MaxTerminal + 1; i++)
        {
            var ticket = Issue(1);
            _tickets.Claim(ticket, 1);
            _tickets.Finish(ticket, null);
        }

        Assert.That(State(active, 99), Is.EqualTo(BundleTicketState.Pending));
    }
}
