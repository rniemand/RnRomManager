using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace RnRomManager.Api.Tests;

/// <summary>A real Kestrel server on 127.0.0.1 for image-URL tests; each path is answered by a test-supplied handler.</summary>
public sealed class StubImageServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubImageServer(WebApplication app) => _app = app;

    /// <summary>Path → handler; unknown paths are 404.</summary>
    public ConcurrentDictionary<string, RequestDelegate> Routes { get; } = new();

    /// <summary>Every request path received, in order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public Uri BaseUrl => new(_app.Urls.First());

    public Uri Url(string path) => new(BaseUrl, path);

    public static async Task<StubImageServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new StubImageServer(app);
        app.Run(ctx =>
        {
            server.Requests.Enqueue(ctx.Request.Path + ctx.Request.QueryString);
            return server.Routes.TryGetValue(ctx.Request.Path, out var handler) ? handler(ctx) : NotFound(ctx);
        });
        await app.StartAsync();
        return server;
    }

    private static Task NotFound(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    /// <summary>Answers 200 with these bytes (and their Content-Length).</summary>
    public static RequestDelegate Bytes(byte[] body, string contentType = "application/octet-stream") => async ctx =>
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body);
    };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
