using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Streams the checked members: the single image as is, or an uncompressed ZIP (UTF-8 names, async writes only).
/// Each source is copied up to its measured length; a source that ends early aborts the response. Range headers
/// are ignored: the whole payload is sent.
/// </summary>
internal sealed class DiscResult(string contentType, PlayDiscEntry? cue, IReadOnlyList<PlayDiscEntry> entries) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        var cancellationToken = httpContext.RequestAborted;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = contentType;
        response.Headers.CacheControl = "no-store";
        response.Headers[HeaderNames.AcceptRanges] = "none";
        try
        {
            if (cue is null)
            {
                response.ContentLength = entries[0].Length;
                await CopyAsync(entries[0], response.Body, cancellationToken);
            }
            else
            {
                // The ZIP's own length isn't known up front; the payload size lets the player show progress.
                response.Headers["X-Content-Bytes"] = (cue.Length + entries.Sum(e => e.Length)).ToString(CultureInfo.InvariantCulture);
                // .NET 10 still writes an entry's data descriptor synchronously when the async entry stream is
                // disposed on an unseekable target, so small synchronous writes are held and sent asynchronously.
                await using var body = new AsyncOnlyWriteStream(response.Body);
                await using (var archive = await ZipArchive.CreateAsync(body, ZipArchiveMode.Create, leaveOpen: true,
                    Encoding.UTF8, cancellationToken))
                {
                    foreach (var entry in (IEnumerable<PlayDiscEntry>)[cue, .. entries])
                    {
                        var zipEntry = archive.CreateEntry(entry.Name, CompressionLevel.NoCompression);
                        await using var target = await zipEntry.OpenAsync(cancellationToken);
                        await CopyAsync(entry, target, cancellationToken);
                    }
                }
                await body.FlushAsync(cancellationToken);
            }
            await response.Body.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            httpContext.RequestServices.GetRequiredService<ILogger<DiscResult>>()
                .LogError(ex, "Streaming the disc of {Path} failed after the response started", httpContext.Request.Path);
            httpContext.Abort();
        }
        finally
        {
            foreach (var entry in (IEnumerable<PlayDiscEntry>)[.. entries, .. cue is null ? [] : new[] { cue }])
                await entry.Source.DisposeAsync();
        }
    }

    private static Task CopyAsync(PlayDiscEntry entry, Stream target, CancellationToken cancellationToken) =>
        PlayDisc.CopyMeasuredAsync(entry.Source, entry.Length, target, cancellationToken);
}
