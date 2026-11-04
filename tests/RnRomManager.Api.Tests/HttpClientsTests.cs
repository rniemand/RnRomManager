using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Api.Http;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

/// <summary>Spec §10.4 retry policy: 5xx/408/timeouts retried with backoff, 404 never.</summary>
public class HttpClientsTests
{
    private static (HttpClient Client, StubHttpHandler Server) Create(Func<int, HttpStatusCode> status)
    {
        var calls = 0;
        var server = new StubHttpHandler(_ => StubHttpHandler.Status(status(++calls)));
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddLogging()
            .AddExternalHttpClients();
        services.AddHttpClient(HttpClients.Dats).ConfigurePrimaryHttpMessageHandler(() => server);
        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.Dats);
        return (client, server);
    }

    [Test]
    public async Task NotFound_IsNotRetried()
    {
        var (client, server) = Create(_ => HttpStatusCode.NotFound);

        using var response = await client.GetAsync("https://dats.test/x.dat");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(server.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ServerError_IsRetried_ThenSucceeds()
    {
        var (client, server) = Create(call => call == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);

        using var response = await client.GetAsync("https://dats.test/x.dat");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(server.Requests, Has.Count.EqualTo(2));
        Assert.That(server.Requests.First().Headers.UserAgent.ToString(), Does.StartWith("RnRomManager/"));
    }

    [TestCase(HttpStatusCode.InternalServerError, true)]
    [TestCase(HttpStatusCode.BadGateway, true)]
    [TestCase(HttpStatusCode.RequestTimeout, true)]
    [TestCase(HttpStatusCode.NotFound, false)]
    [TestCase(HttpStatusCode.Forbidden, false)]
    [TestCase(HttpStatusCode.TooManyRequests, false)]
    [TestCase(HttpStatusCode.OK, false)]
    public void IsTransient_ByStatus(HttpStatusCode status, bool transient)
    {
        using var response = new HttpResponseMessage(status);
        Assert.That(HttpClients.IsTransient(Polly.Outcome.FromResult(response)), Is.EqualTo(transient));
    }

    [Test]
    public void IsTransient_ConnectionErrorsAndTimeouts()
    {
        Assert.That(HttpClients.IsTransient(Polly.Outcome.FromException<HttpResponseMessage>(new HttpRequestException("refused"))), Is.True);
        Assert.That(HttpClients.IsTransient(Polly.Outcome.FromException<HttpResponseMessage>(new Polly.Timeout.TimeoutRejectedException())), Is.True);
        Assert.That(HttpClients.IsTransient(Polly.Outcome.FromException<HttpResponseMessage>(new InvalidOperationException())), Is.False);
    }

    [Test]
    public async Task MediaService_UsesTheThumbnailsClient()
    {
        var server = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.NotFound));
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddLogging()
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new RnRomManager.Core.Configuration.SourcesOptions { ThumbnailBaseUrl = "https://thumbs.test" }))
            .AddExternalHttpClients();
        services.AddHttpClient(HttpClients.Thumbnails).ConfigurePrimaryHttpMessageHandler(() => server);
        var media = services.BuildServiceProvider().GetRequiredService<RnRomManager.Core.Media.MediaService>();
        var handler = new RnRomManager.Systems.GameBoy.GameBoyHandler();

        var result = await media.FindAsync(handler, ["Tetris (World)"], new RnRomManager.Core.Media.CircuitBreaker(), CancellationToken.None);

        Assert.That(result.BoxArt.IsDefault, Is.True);
        Assert.That(server.Requests, Has.Count.EqualTo(2), "404s aren't retried");
        Assert.That(server.Requests.First().Headers.UserAgent.ToString(), Does.StartWith("RnRomManager/"));
    }
}
