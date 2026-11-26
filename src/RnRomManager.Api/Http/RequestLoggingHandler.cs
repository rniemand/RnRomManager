using System.Diagnostics;

namespace RnRomManager.Api.Http;

internal sealed class RequestLoggingHandler(ILogger<RequestLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            logger.LogDebug("HTTP {Method} {Url} → {Status} in {Ms} ms", request.Method, request.RequestUri, (int)response.StatusCode,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogDebug("HTTP {Method} {Url} failed after {Ms} ms: {Error}", request.Method, request.RequestUri,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
            throw;
        }
    }
}
