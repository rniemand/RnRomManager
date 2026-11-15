using RnRomManager.Api.Http;
using RnRomManager.Core.Bundles;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/bundle-downloads*</c> (Bundle Creator B7): short-lived, caller-owned transfer tickets that let the Bundle
/// Creator page send one attachment at a time and learn when the server has finished sending it.
/// </summary>
public static class BundleDownloadEndpoints
{
    /// <summary>B7: ticket URLs and status answers are never cached.</summary>
    public const string NoStore = "private, no-store";

    public sealed record IssueRequest(string? GameId, string? ReleaseSha1, BundleMemberInput? Member);

    internal static string FileUrl(string ticket) => $"/api/bundle-downloads/{ticket}/file";

    /// <summary>
    /// <c>POST /api/bundle-downloads</c>: resolves the identity now and issues a pending ticket for it. 400 for a malformed
    /// identity, 404 when it doesn't resolve, 409 <c>BundleDownloadBusy</c> while the caller's previous ticket is pending
    /// or sending, 503 <c>BundleDownloadsFull</c> at the global bound, else 201 <c>{ ticket, fileUrl }</c>.
    /// </summary>
    public static async Task<IResult> IssueAsync(HttpContext ctx, IRomRepository roms, BundleDownloadTickets tickets)
    {
        ctx.Response.Headers.CacheControl = NoStore;
        var (request, problem) = await BundleQueueEndpoints.ReadJsonAsync<IssueRequest>(ctx);
        if (problem is not null)
            return problem;
        if (BundleQueueValidation.ParseIdentity(request!.GameId, request.ReleaseSha1, request.Member, out var identity) is { } error)
            return Problems.Validation(ctx, new Dictionary<string, string[]> { [error.Field] = [error.Message] });

        if ((await roms.ResolveBundleFilesAsync([identity!], ctx.RequestAborted))[0] is null)
            return Problems.NotFound(ctx);

        var issue = tickets.Issue(CollectionEndpoints.UserId(ctx), identity!);
        return issue.Status switch
        {
            BundleTicketIssueStatus.Issued => Results.Created($"/api/bundle-downloads/{issue.Ticket}",
                new { ticket = issue.Ticket, fileUrl = FileUrl(issue.Ticket!) }),
            BundleTicketIssueStatus.Busy => Problems.Create(ctx, StatusCodes.Status409Conflict, "BundleDownloadBusy",
                "Another file of yours is still being sent. Wait for it to finish."),
            BundleTicketIssueStatus.Full => Problems.Create(ctx, StatusCodes.Status503ServiceUnavailable, "BundleDownloadsFull",
                "Too many downloads are in progress. Try again shortly."),
            _ => throw new ArgumentOutOfRangeException(nameof(ctx)),
        };
    }

    /// <summary>
    /// <c>GET /api/bundle-downloads/{ticket}</c>: <c>{ state, errorCode }</c> for the caller's ticket; 404 when it's
    /// unknown, evicted or another user's (including after a restart).
    /// </summary>
    public static IResult Status(string ticket, HttpContext ctx, BundleDownloadTickets tickets)
    {
        ctx.Response.Headers.CacheControl = NoStore;
        if (tickets.Status(ticket, CollectionEndpoints.UserId(ctx)) is not { } status)
            return Problems.NotFound(ctx);
        return Results.Ok(new { state = StateName(status.State), errorCode = status.ErrorCode });
    }

    /// <summary>The wire name of a state: <c>pending</c>, <c>sending</c>, <c>sent</c>, <c>failed</c> or <c>expired</c>.</summary>
    public static string StateName(BundleTicketState state) => state switch
    {
        BundleTicketState.Pending => "pending",
        BundleTicketState.Sending => "sending",
        BundleTicketState.Sent => "sent",
        BundleTicketState.Failed => "failed",
        BundleTicketState.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
