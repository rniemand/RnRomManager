using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Bundles;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Endpoints;

/// <summary>Bundle Creator B7: the ticket issue and status handlers, with a substituted ROM repository and a fake clock.</summary>
public class BundleDownloadHandlerTests
{
    private const string Game = "8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly BundleFileTarget Target = new(Guid.Parse(Game), "Orbit Blocks", 7, null, "Orbit Blocks (World).gb");

    private FakeTimeProvider _clock = null!;
    private BundleDownloadTickets _tickets = null!;
    private IRomRepository _roms = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        _tickets = new BundleDownloadTickets(_clock);
        _roms = Substitute.For<IRomRepository>();
        _roms.ResolveBundleFilesAsync(Arg.Any<IReadOnlyList<BundleFileIdentity>>(), Arg.Any<CancellationToken>())
            .Returns([Target]);
    }

    private static DefaultHttpContext Context(int userId, object? body = null)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
        };
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body as string ?? JsonSerializer.Serialize(body));
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
        }
        return ctx;
    }

    private static object Item(object? member = null) => new { gameId = Game, releaseSha1 = Sha, member };

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    private static string? CodeOf(IResult result) => ((ProblemHttpResult)result).ProblemDetails.Extensions["code"] as string;

    private static JsonElement ValueOf(IResult result) => JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);

    private async Task<string> IssueAsync(int userId)
    {
        var result = await BundleDownloadEndpoints.IssueAsync(Context(userId, Item()), _roms, _tickets);
        Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status201Created));
        return ValueOf(result).GetProperty("ticket").GetString()!;
    }

    [Test]
    public async Task Issue_ResolvesNow_AndReturnsTheTicketAndItsFileUrl()
    {
        var ctx = Context(1, Item(new { sha1 = Sha, fileName = "Track 2.bin" }));

        var result = await BundleDownloadEndpoints.IssueAsync(ctx, _roms, _tickets);

        Assert.That(StatusOf(result), Is.EqualTo(201));
        var body = ValueOf(result);
        var ticket = body.GetProperty("ticket").GetString();
        Assert.That(body.GetProperty("fileUrl").GetString(), Is.EqualTo($"/api/bundle-downloads/{ticket}/file"));
        Assert.That(result.GetType().GetProperty("Location")!.GetValue(result), Is.EqualTo($"/api/bundle-downloads/{ticket}"));
        Assert.That(ctx.Response.Headers.CacheControl.ToString(), Is.EqualTo("private, no-store"));
        await _roms.Received(1).ResolveBundleFilesAsync(
            Arg.Is<IReadOnlyList<BundleFileIdentity>>(l => l.Count == 1 && l[0].Member!.FileName == "Track 2.bin" && l[0].GameId == Guid.Parse(Game)),
            Arg.Any<CancellationToken>());
        Assert.That(_tickets.Claim(ticket!, 1).Identity!.Member!.Sha1, Is.EqualTo(Sha));
    }

    [Test]
    public async Task Issue_ForAnUnavailableFile_Is404_AndIssuesNothing()
    {
        _roms.ResolveBundleFilesAsync(Arg.Any<IReadOnlyList<BundleFileIdentity>>(), Arg.Any<CancellationToken>())
            .Returns([(BundleFileTarget?)null]);

        var result = await BundleDownloadEndpoints.IssueAsync(Context(1, Item()), _roms, _tickets);

        Assert.That(StatusOf(result), Is.EqualTo(404));
        Assert.That(_tickets.ActiveCount, Is.Zero);
    }

    [Test]
    public async Task Issue_Malformed_Is400_BeforeAnyLookup()
    {
        var bad = await BundleDownloadEndpoints.IssueAsync(Context(1, new { gameId = Game, releaseSha1 = "NOPE" }), _roms, _tickets);
        var notJson = await BundleDownloadEndpoints.IssueAsync(Context(1, "{ nope"), _roms, _tickets);

        Assert.That((StatusOf(bad), CodeOf(bad)), Is.EqualTo((400, "ValidationFailed")));
        Assert.That(((HttpValidationProblemDetails)((ProblemHttpResult)bad).ProblemDetails).Errors.Keys, Is.EqualTo(new[] { "releaseSha1" }));
        Assert.That(StatusOf(notJson), Is.EqualTo(400));
        await _roms.DidNotReceiveWithAnyArgs().ResolveBundleFilesAsync(default!, default);
    }

    [Test]
    public async Task Issue_WhileTheCallersTicketIsPendingOrSending_IsBusy_ButOtherUsersProceed()
    {
        var ticket = await IssueAsync(1);

        var pending = await BundleDownloadEndpoints.IssueAsync(Context(1, Item()), _roms, _tickets);
        _tickets.Claim(ticket, 1);
        var sending = await BundleDownloadEndpoints.IssueAsync(Context(1, Item()), _roms, _tickets);
        await IssueAsync(2);
        _tickets.Finish(ticket, null);

        Assert.That((StatusOf(pending), CodeOf(pending)), Is.EqualTo((409, "BundleDownloadBusy")));
        Assert.That(CodeOf(sending), Is.EqualTo("BundleDownloadBusy"));
        await IssueAsync(1);
    }

    [Test]
    public async Task Issue_AtTheGlobalBound_Is503()
    {
        for (var user = 1; user <= BundleDownloadTickets.MaxActive; user++)
            await IssueAsync(user);

        var result = await BundleDownloadEndpoints.IssueAsync(Context(1000, Item()), _roms, _tickets);

        Assert.That((StatusOf(result), CodeOf(result)), Is.EqualTo((503, "BundleDownloadsFull")));
    }

    [Test]
    public async Task Status_IsTheCallersOnly_AndNeverCached()
    {
        var ticket = await IssueAsync(1);
        var mine = Context(1);
        var theirs = Context(2);

        var own = BundleDownloadEndpoints.Status(ticket, mine, _tickets);
        var foreign = BundleDownloadEndpoints.Status(ticket, theirs, _tickets);
        var unknown = BundleDownloadEndpoints.Status(new string('0', 64), Context(1), _tickets);

        Assert.That(StatusOf(own), Is.EqualTo(200));
        Assert.That(ValueOf(own).ToString(), Is.EqualTo("""{"state":"pending","errorCode":null}"""));
        Assert.That((StatusOf(foreign), StatusOf(unknown)), Is.EqualTo((404, 404)));
        Assert.That((mine.Response.Headers.CacheControl.ToString(), theirs.Response.Headers.CacheControl.ToString()),
            Is.EqualTo(("private, no-store", "private, no-store")));
    }

    [Test]
    public async Task Status_FollowsTheTicket_ThroughSendingToFailed()
    {
        var ticket = await IssueAsync(1);
        _tickets.Claim(ticket, 1);
        var sending = ValueOf(BundleDownloadEndpoints.Status(ticket, Context(1), _tickets));
        _tickets.Finish(ticket, "Aborted");
        var failed = ValueOf(BundleDownloadEndpoints.Status(ticket, Context(1), _tickets));

        Assert.That(sending.GetProperty("state").GetString(), Is.EqualTo("sending"));
        Assert.That(failed.ToString(), Is.EqualTo("""{"state":"failed","errorCode":"Aborted"}"""));
    }

    [Test]
    public async Task APendingTicket_Expires_AndFreesTheCallerToIssueAgain()
    {
        var ticket = await IssueAsync(1);

        _clock.Advance(BundleDownloadTickets.PendingLifetime);

        Assert.That(ValueOf(BundleDownloadEndpoints.Status(ticket, Context(1), _tickets)).GetProperty("state").GetString(), Is.EqualTo("expired"));
        await IssueAsync(1);
        _clock.Advance(BundleDownloadTickets.TerminalLifetime + BundleDownloadTickets.PendingLifetime);
        Assert.That(StatusOf(BundleDownloadEndpoints.Status(ticket, Context(1), _tickets)), Is.EqualTo(404), "evicted after five minutes");
    }

    [TestCase(BundleTicketState.Pending, "pending")]
    [TestCase(BundleTicketState.Sending, "sending")]
    [TestCase(BundleTicketState.Sent, "sent")]
    [TestCase(BundleTicketState.Failed, "failed")]
    [TestCase(BundleTicketState.Expired, "expired")]
    public void StateNames_AreTheWireNames(BundleTicketState state, string name) =>
        Assert.That(BundleDownloadEndpoints.StateName(state), Is.EqualTo(name));
}
