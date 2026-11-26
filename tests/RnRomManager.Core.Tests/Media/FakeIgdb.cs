using System.Collections.Concurrent;
using System.Net;

namespace RnRomManager.Core.Tests.Media;

internal sealed class FakeIgdb : HttpMessageHandler
{
    public Func<HttpRequestMessage, string, Task<HttpResponseMessage>> Token { get; set; } = (_, _) => Task.FromResult(IgdbClientTests.Json(IgdbClientTests.TokenJson));
    public Func<HttpRequestMessage, string, Task<HttpResponseMessage>> Games { get; set; } = (_, _) => Task.FromResult(IgdbClientTests.Json("[]"));
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Images { get; set; } = _ => Task.FromResult(IgdbClientTests.Status(HttpStatusCode.NotFound));
    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    public IEnumerable<(HttpRequestMessage Request, string Body)> TokenRequests => Requests.Where(r => r.Request.RequestUri!.Host == "id.twitch.tv");
    public IEnumerable<(HttpRequestMessage Request, string Body)> GameRequests => Requests.Where(r => r.Request.RequestUri!.Host == "api.igdb.com");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request, body));
        return request.RequestUri!.Host switch
        {
            "id.twitch.tv" => await Token(request, body),
            "api.igdb.com" => await Games(request, body),
            "images.igdb.com" => await Images(request),
            _ => throw new InvalidOperationException($"Unexpected host {request.RequestUri}"),
        };
    }
}
