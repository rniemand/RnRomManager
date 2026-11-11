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
            .AddSingleton(new RnRomManager.Core.Dats.DatCache(Microsoft.Extensions.Options.Options.Create(new RnRomManager.Core.Configuration.PathsOptions()),
                new RnRomManager.Core.Systems.SystemRegistry([]), NullLogger<RnRomManager.Core.Dats.DatCache>.Instance))
            .AddExternalHttpClients();
        services.AddHttpClient(HttpClients.Thumbnails).ConfigurePrimaryHttpMessageHandler(() => server);
        var media = services.BuildServiceProvider().GetRequiredService<RnRomManager.Core.Media.MediaService>();
        var handler = new RnRomManager.Systems.GameBoy.GameBoyHandler();

        var result = await media.FindAsync(handler, ["Tetris (World)"], new RnRomManager.Core.Media.CircuitBreaker(), CancellationToken.None);

        Assert.That(result.BoxArt.IsDefault, Is.True);
        Assert.That(server.Requests, Has.Count.EqualTo(2), "404s aren't retried");
        Assert.That(server.Requests.First().Headers.UserAgent.ToString(), Does.StartWith("RnRomManager/"));
    }

    /// <summary>
    /// Spec T2: the titledb body is read after the headers, so the 20 s per-attempt timeout must not reach it. The body
    /// stalls past that timeout on a fake clock and fails if the request's token is ever cancelled.
    /// </summary>
    [Test]
    public async Task DatsClient_BodySlowerThanTheAttemptTimeout_StillCompletes()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var gate = new TaskCompletionSource();
        var body = new byte[] { 1, 2, 3 };
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddLogging()
            .AddExternalHttpClients();
        services.AddHttpClient(HttpClients.Dats).ConfigurePrimaryHttpMessageHandler(() => new SlowBodyHandler(gate.Task, body));
        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.Dats);

        using var response = await client.GetAsync("https://titledb.test/US.en.json", HttpCompletionOption.ResponseHeadersRead);
        var read = response.Content.ReadAsByteArrayAsync();
        clock.Advance(TimeSpan.FromSeconds(25));
        gate.SetResult();

        Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(body));
    }

    /// <summary>Headers at once; the body waits for <paramref name="gate"/> and fails if the request was cancelled meanwhile.</summary>
    private sealed class SlowBodyHandler(Task gate, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowContent(gate, body, cancellationToken) });
    }

    private sealed class SlowContent(Task gate, byte[] body, CancellationToken request) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await gate;
            request.ThrowIfCancellationRequested();
            await stream.WriteAsync(body);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Spec T8: a TitleDb system's media goes through the titledb CDN client, never the libretro one.</summary>
    [Test]
    public async Task MediaService_TitleDbFetch_UsesItsNamedClient()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var cdn = new StubHttpHandler(_ => StubHttpHandler.Bytes(png));
        var thumbnails = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.NotFound));
        var paths = Microsoft.Extensions.Options.Options.Create(new RnRomManager.Core.Configuration.PathsOptions());
        var handler = new ExternalTitleDbTestHandler();
        var dats = new RnRomManager.Core.Dats.DatCache(paths, new RnRomManager.Core.Systems.SystemRegistry([handler]),
            NullLogger<RnRomManager.Core.Dats.DatCache>.Instance);
        dats.Publish(new Dictionary<string, RnRomManager.Core.Dats.DatStore>
        {
            [handler.Id] = new([new("0100000000002000", "Orbit", null, null, null, [], [], "https://img-eshop.cdn.nintendo.net/i/box.png", null)], "2026.11.10"),
        });
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddLogging()
            .AddSingleton(dats)
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new RnRomManager.Core.Configuration.SourcesOptions { ThumbnailBaseUrl = "https://thumbs.test" }))
            .AddExternalHttpClients();
        services.AddHttpClient(HttpClients.Thumbnails).ConfigurePrimaryHttpMessageHandler(() => thumbnails);
        services.AddHttpClient(HttpClients.TitleDbImages).ConfigurePrimaryHttpMessageHandler(() => cdn);
        var media = services.BuildServiceProvider().GetRequiredService<RnRomManager.Core.Media.MediaService>();

        var result = await media.FindAsync(handler, ["Orbit [0100000000002000]"], new RnRomManager.Core.Media.CircuitBreaker(), CancellationToken.None);

        Assert.That(result.BoxArt.SourceUrl, Is.EqualTo("https://img-eshop.cdn.nintendo.net/i/box.png"));
        Assert.That(cdn.Requests, Has.Count.EqualTo(1));
        Assert.That(cdn.Requests.Single().Headers.UserAgent.ToString(), Does.StartWith("RnRomManager/"));
        Assert.That(thumbnails.Requests, Is.Empty);
    }

    /// <summary>Spec T8: the CDN client's own primary handler doesn't follow redirects; a loopback 302 comes back as it is.</summary>
    [Test]
    public async Task TitleDbImagesClient_ReturnsA302_WithoutASecondRequest()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = 0;
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                using var socket = await listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref requests);
                var stream = socket.GetStream();
                var reader = new StreamReader(stream);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { } // the request head
                var reply = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/next\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply));
            }
        });
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddLogging()
            .AddExternalHttpClients();
        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.TitleDbImages);

        using var response = await client.GetAsync($"http://127.0.0.1:{port}/first");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
        await Task.Delay(200); // any follow-up request would have arrived by now
        Assert.That(Volatile.Read(ref requests), Is.EqualTo(1));
        listener.Stop();
        Assert.That(serving.IsFaulted || !serving.IsCompleted, Is.True);
    }
}
