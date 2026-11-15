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

    /// <summary>B7: a stream with no read or write progress for this long is cancelled and its ticket fails.</summary>
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Failure codes a ticket can end with.</summary>
    public static class ErrorCodes
    {
        /// <summary>The identity no longer resolves when the stream opens.</summary>
        public const string Unavailable = "Unavailable";
        /// <summary>The shared opener refused (missing or unsafe file, unreadable ZIP).</summary>
        public const string OpenFailed = "OpenFailed";
        /// <summary>The client went away mid-stream.</summary>
        public const string Aborted = "Aborted";
        /// <summary>No progress for <see cref="InactivityTimeout"/>.</summary>
        public const string Stalled = "Stalled";
        /// <summary>A read or write failed, or the file ended before its expected length.</summary>
        public const string IoError = "IoError";
    }

    public static RouteGroupBuilder MapBundleDownloadEndpoints(this RouteGroupBuilder api)
    {
        var downloads = api.MapGroup("/bundle-downloads");
        downloads.MapPost("", IssueAsync).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(BundleQueueEndpoints.RequestLimit));
        downloads.MapGet("/{ticket}", Status);
        downloads.MapGet("/{ticket}/file", FileAsync);
        return api;
    }

    /// <summary>
    /// <c>GET /api/bundle-downloads/{ticket}/file</c>: claims the caller's pending ticket once, resolves its identity
    /// again, opens it through <see cref="LibraryDownload"/> and sends the whole file (no ranges). 404 for an unknown or
    /// foreign ticket, 409 <c>BundleDownloadConsumed</c> once claimed, finished or expired. A claimed ticket always ends:
    /// <c>sent</c> only after the expected length is written and the response completes, otherwise <c>failed</c>.
    /// </summary>
    public static async Task<IResult> FileAsync(string ticket, HttpContext ctx, BundleDownloadTickets tickets, IRomRepository roms,
        IGameRepository games, LibraryFiles files, TimeProvider time, ILoggerFactory loggers)
    {
        ctx.Response.Headers.CacheControl = NoStore;
        var claim = tickets.Claim(ticket, CollectionEndpoints.UserId(ctx));
        if (claim.Status == BundleTicketClaimStatus.NotFound)
            return Problems.NotFound(ctx);
        if (claim.Status == BundleTicketClaimStatus.Consumed)
            return Problems.Create(ctx, StatusCodes.Status409Conflict, "BundleDownloadConsumed", "This download link was already used.");

        LibraryAttachment? attachment = null;
        try
        {
            if ((await roms.ResolveBundleFilesAsync([claim.Identity!], ctx.RequestAborted))[0] is not { } target)
            {
                tickets.Finish(ticket, ErrorCodes.Unavailable);
                return Problems.NotFound(ctx);
            }
            var (opened, problem) = target.FileId is { } fileId
                ? await LibraryDownload.OpenSetFileAsync(ctx, target.GameId, target.RomId, fileId, games, roms, files)
                : await LibraryDownload.OpenReleaseAsync(ctx, target.GameId, target.RomId, games, roms, files);
            if (problem is not null)
            {
                tickets.Finish(ticket, ErrorCodes.OpenFailed);
                return problem;
            }
            attachment = opened!;
            var result = new TicketStreamResult(ticket, attachment, tickets, time, loggers.CreateLogger(typeof(BundleDownloadEndpoints).FullName!));
            attachment = null; // the result owns it now
            return result;
        }
        catch (Exception ex)
        {
            if (attachment is not null)
                await attachment.DisposeAsync();
            tickets.Finish(ticket, ex is OperationCanceledException ? ErrorCodes.Aborted : ErrorCodes.IoError);
            throw;
        }
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes, cancelling when a read or write makes no progress for
    /// <paramref name="inactivity"/> (a <see cref="TimeoutException"/>) or <paramref name="aborted"/> fires. A source
    /// that ends early is an <see cref="IOException"/>; bytes past <paramref name="length"/> are never read.
    /// </summary>
    public static async Task CopyAsync(Stream source, Stream target, long length, TimeSpan inactivity, TimeProvider time,
        CancellationToken aborted)
    {
        using var idle = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, idle.Token);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long sent = 0;
            while (sent < length)
            {
                idle.CancelAfter(inactivity);
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), linked.Token);
                if (read == 0)
                    throw new IOException($"The file ended after {sent} of {length} bytes.");
                idle.CancelAfter(inactivity);
                await target.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                sent += read;
            }
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !aborted.IsCancellationRequested)
        {
            throw new TimeoutException($"No progress for {inactivity}.");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The ticket's attachment response: full body, explicit length, completion-gated status.</summary>
    private sealed class TicketStreamResult(string ticket, LibraryAttachment attachment, BundleDownloadTickets tickets, TimeProvider time,
        ILogger logger) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            string? errorCode = ErrorCodes.IoError;
            try
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength = attachment.Length;
                var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
                disposition.SetHttpFileName(attachment.FileName);
                ctx.Response.Headers.ContentDisposition = disposition.ToString();

                await CopyAsync(attachment.Stream, ctx.Response.Body, attachment.Length, InactivityTimeout, time, ctx.RequestAborted);
                await ctx.Response.CompleteAsync();
                errorCode = ctx.RequestAborted.IsCancellationRequested ? ErrorCodes.Aborted : null;
            }
            catch (Exception ex)
            {
                errorCode = ex switch
                {
                    TimeoutException => ErrorCodes.Stalled,
                    OperationCanceledException => ErrorCodes.Aborted,
                    _ when ctx.RequestAborted.IsCancellationRequested => ErrorCodes.Aborted,
                    _ => ErrorCodes.IoError,
                };
                logger.LogWarning(ex, "Bundle download {Ticket} ({FileName}) failed: {ErrorCode}", ticket, attachment.FileName, errorCode);
                ctx.Abort();
            }
            finally
            {
                // B7: abort and dispose before the slot is released.
                await attachment.DisposeAsync();
                tickets.Finish(ticket, errorCode);
            }
        }
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
