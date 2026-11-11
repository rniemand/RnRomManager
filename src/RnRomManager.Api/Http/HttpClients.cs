using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using RnRomManager.Core.Media;

namespace RnRomManager.Api.Http;

/// <summary>
/// Outbound HTTP (spec §10.4): one named client per host, <c>User-Agent: RnRomManager/&lt;version&gt;</c> and a
/// <c>Debug</c> line per request. The DAT and thumbnail clients also get 20 s per attempt and 3 retries with
/// exponential backoff from 1 s on 5xx/408/timeouts/connection errors (never on 404). The image-URL client (U3c) gets
/// neither: <see cref="ImageUrlDownloader"/> has its own single timeout and never retries.
/// </summary>
public static class HttpClients
{
    public const string Dats = "dats";
    public const string Thumbnails = "thumbnails";
    public const string ImageUrls = "imageUrls";
    public const string TitleDbImages = "titledbImages";

    public static IServiceCollection AddExternalHttpClients(this IServiceCollection services)
    {
        services.AddTransient<RequestLoggingHandler>();
        Configure(services.AddHttpClient(Dats, client => client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent)));
        // Typed client: a fresh HttpClient per resolve, so pooled handlers still rotate.
        Configure(services.AddHttpClient<MediaService>(Thumbnails, client => client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent)));
        // T8: titledb's CDN, with the thumbnail retry policy and no redirects, so every request stays on the allowlisted host.
        Configure(services.AddHttpClient<TitleDbImageClient>(TitleDbImages, client => client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false }));
        services.AddSingleton(ImageUrlPolicy.Default);
        services.AddHttpClient<ImageUrlDownloader>(ImageUrls, client =>
            {
                client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
                // The downloader's own token is the only timeout (headers and body).
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => ImageUrlDownloader.CreateHandler(sp.GetRequiredService<ImageUrlPolicy>().IsAllowed))
            .AddHttpMessageHandler<RequestLoggingHandler>();
        return services;
    }

    private static void Configure(IHttpClientBuilder builder) =>
        builder
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
