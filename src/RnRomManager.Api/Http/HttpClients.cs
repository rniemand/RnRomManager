using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace RnRomManager.Api.Http;

/// <summary>
/// Outbound HTTP (spec §10.4): one named client per host, 20 s per attempt, 3 retries with exponential backoff from
/// 1 s on 5xx/408/timeouts/connection errors (never on 404), <c>User-Agent: RnRomManager/&lt;version&gt;</c>, and a
/// <c>Debug</c> line per request.
/// </summary>
public static class HttpClients
{
    public const string Dats = "dats";

    public static IServiceCollection AddExternalHttpClients(this IServiceCollection services)
    {
        services.AddTransient<RequestLoggingHandler>();
        services.AddHttpClient(Dats, client => client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent))
            .AddHttpMessageHandler<RequestLoggingHandler>()
            .AddResilienceHandler("external", pipeline => pipeline
                .AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                    UseJitter = false,
                    ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
                })
                .AddTimeout(TimeSpan.FromSeconds(20)));
        return services;
    }

    public static string UserAgent => $"RnRomManager/{StartupRunner.Version.Split('+')[0]}";

    public static bool IsTransient(Outcome<HttpResponseMessage> outcome) => outcome switch
    {
        { Exception: HttpRequestException or Polly.Timeout.TimeoutRejectedException } => true,
        { Result: { } response } => (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout,
        _ => false,
    };

    private sealed class RequestLoggingHandler(ILogger<RequestLoggingHandler> logger) : DelegatingHandler
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
}
