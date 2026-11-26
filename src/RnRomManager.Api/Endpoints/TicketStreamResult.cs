using RnRomManager.Core.Bundles;

namespace RnRomManager.Api.Endpoints;

/// <summary>The ticket's attachment response: full body, explicit length, completion-gated status.</summary>
internal sealed class TicketStreamResult(
    string ticket,
    LibraryAttachment attachment,
    BundleDownloadTickets tickets,
    TimeProvider time,
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

            await BundleDownloadEndpoints.CopyAsync(attachment.Stream, ctx.Response.Body, attachment.Length, BundleDownloadEndpoints.InactivityTimeout, time, ctx.RequestAborted);
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
